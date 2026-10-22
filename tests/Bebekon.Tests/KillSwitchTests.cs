using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public class KillSwitchTests
{
    private static readonly IPEndPoint Internet = new(IPAddress.Parse("1.1.1.1"), 443);

    private static async Task<bool> CanConnect(IPEndPoint target)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await socket.ConnectAsync(target, timeout.Token); return true; }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return false; }
    }

    /// <summary>Real Windows Filtering Platform filters; needs an elevated Windows run (CI is).</summary>
    [Fact] public async Task BlocksOutsideTheTunnelKeepsLoopbackAndTrustedCoresAndCleansUp()
    {
        // GitHub's Windows runners are elevated and online: there the test must really run.
        var ci = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (!OperatingSystem.IsWindows()) return;
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Assert.True(elevated || !ci, "CI must run this test elevated");
        if (!elevated) return;
        var online = await CanConnect(Internet);
        Assert.True(online || !ci, "CI must have internet for this test");
        if (!online) return; // no internet on this machine: nothing to compare
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var loopback = (IPEndPoint)listener.LocalEndpoint;
        using (KillSwitch.Enable([]))
        {
            Assert.False(await CanConnect(Internet));
            Assert.True(await CanConnect(loopback));
        }
        Assert.True(await CanConnect(Internet));
        // The bundled cores reach their servers directly; here the test host stands in for them.
        using (KillSwitch.Enable([Environment.ProcessPath!, Path.Combine(AppContext.BaseDirectory, "missing.exe")])) Assert.True(await CanConnect(Internet));
        // Disposing twice is harmless and leaves nothing behind.
        var once = KillSwitch.Enable([]); once.Dispose(); once.Dispose();
        Assert.True(await CanConnect(Internet));
    }
}
