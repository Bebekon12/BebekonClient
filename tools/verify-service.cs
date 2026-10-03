#:project ../src/Bebekon.Core/Bebekon.Core.csproj
#:property TargetFramework=net10.0-windows
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Security.Principal;
using System.Security.Cryptography;
using Bebekon.Core;
using Microsoft.Win32;

// Explicit installed-helper regression check. Default mode only requests status.
// --probe starts an isolated proxy core using the saved selection and stops it in
// finally. Neither mode changes Windows proxy/routes or installs/upgrades a service.
using var identity = WindowsIdentity.GetCurrent();
if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Run as an ordinary Windows user to exercise the LocalSystem permission boundary.");
var owner = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\BebekonVPN", "OwnerSid", null) as string;
if (owner != identity.User?.Value)
    throw new InvalidOperationException("An installed helper assigned to this Windows user is required.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var client = new ServiceClient();
for (var i = 0; i < 5; i++)
{
    var reply = await client.SendAsync(new("GetStatus"), startService: true, ct: timeout.Token);
    if (!reply.Ok) throw new InvalidOperationException("The verified helper rejected GetStatus.");
    Console.WriteLine($"Verified LocalSystem helper: status {reply.Status.State}, core running {reply.Status.CorePid is not null}.");
}
Console.WriteLine("Five authenticated status requests passed without elevation or changing the connection.");
if (!args.Contains("--probe")) return;

var before = await client.SendAsync(new("GetStatus"), ct: timeout.Token);
if (before.Status.CorePid is not null || before.Status.State == ConnectionState.Connected)
    throw new InvalidOperationException("An existing connection must remain untouched; disconnect it before this explicit probe.");
var state = new StateStore().Load();
var server = state.Servers.FirstOrDefault(s => s.Id == state.SelectedServerId && s.Supported)
    ?? throw new InvalidOperationException("A saved supported server selection is required.");
var profile = state.Profiles.FirstOrDefault(p => p.Id == state.SelectedProfileId) ?? state.Profiles[0];
state.Settings.TunnelMode = TunnelMode.Proxy;
var spec = new ConnectSpec(server, profile, state.Settings, LatencyService.FreePort(),
    Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
    state.Servers.Where(s => profile.Rules.Any(r => r.UseVpn && r.ServerId == s.Id)).ToList());
var validated = await client.SendAsync(new("ValidateConfig", spec), ct: timeout.Token);
if (!validated.Ok) throw new InvalidOperationException("The installed helper rejected the saved configuration.");
int? startedCorePid = null;
try
{
    var started = await client.SendAsync(new("StartCore", spec), ct: timeout.Token);
    if (started.Ok) startedCorePid = started.Status.CorePid;
    if (!started.Ok || started.Status.State != ConnectionState.Connected || started.Status.CorePid is null)
        throw new InvalidOperationException("The installed helper could not start the selected VPN core.");
    Console.WriteLine("Verified LocalSystem helper launched the real VPN core with the saved profile.");
    using var http = LatencyService.ProbeClient(spec);
    using var response = await http.GetAsync("https://www.gstatic.com/generate_204", timeout.Token);
    response.EnsureSuccessStatusCode();
    _ = await LatencyService.VpnIpAsync(spec, timeout.Token);
    Console.WriteLine($"Forced-VPN HTTPS: HTTP {(int)response.StatusCode}; a valid VPN egress IP was retrieved (details withheld).");
}
finally
{
    if (startedCorePid is not null)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var current = await client.SendAsync(new("GetStatus"), ct: cleanup.Token);
        if (current.Status.CorePid is { } currentPid && currentPid != startedCorePid)
            throw new InvalidOperationException("The helper's core changed during this probe; leave the new connection untouched.");
        var stopped = await client.SendAsync(new("StopCore"), startService: true, ct: cleanup.Token);
        if (!stopped.Ok || stopped.Status.CorePid is not null || stopped.Status.State != ConnectionState.Disconnected)
            throw new InvalidOperationException("The probe core did not stop cleanly.");
        Console.WriteLine("Probe core stopped; no saved settings or Windows network configuration were changed.");
    }
}
