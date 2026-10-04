#:project ../src/Bebekon.Core/Bebekon.Core.csproj
#:property TargetFramework=net10.0-windows
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Diagnostics;
using System.Security.Cryptography;
using Bebekon.Core;

// Developer-only read/probe tool. Supply a private URL through the environment, not source or logs.
var source = Environment.GetEnvironmentVariable("BEBEKON_SUBSCRIPTION_URL");
if (string.IsNullOrWhiteSpace(source))
{
    Console.WriteLine("Set BEBEKON_SUBSCRIPTION_URL for this process, then run this file with dotnet run.");
    return 1;
}
var executable = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory(), "..", "core", "sing-box.exe"));
var runtimeRoot = Path.GetFullPath(Path.Combine(Paths.UserRoot, "runtime")) + Path.DirectorySeparatorChar;
var probeRoot = Path.GetFullPath(Path.Combine(runtimeRoot, "subscription-check-" + Guid.NewGuid().ToString("N")));
if (!probeRoot.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid probe directory.");
Directory.CreateDirectory(probeRoot);
try
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var nodes = await SubscriptionLoader.LoadAsync(source, lifetime.Token);
    Console.WriteLine($"Loaded {nodes.Count} nodes; supported: {nodes.Count(node => node.Supported)}.");
    if (args.Contains("--latency"))
    {
        var latency = new LatencyService(executable);
        var results = await Task.WhenAll(nodes.Select(node => latency.MeasureAsync(node, LatencyMode.HttpsGet, true, lifetime.Token)));
        for (var i = 0; i < results.Length; i++) Console.WriteLine($"Node {i + 1}: HTTPS GET median {results[i].Milliseconds?.ToString() ?? "unavailable"} ms; flag {CountryInfo.Resolve(nodes[i].Name) ?? "unknown"}.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { await Task.WhenAll(nodes.Select(node => latency.MeasureAsync(node, LatencyMode.HttpsGet, true, cancellation.Token))); throw new InvalidOperationException("Cancellation did not stop the scan."); }
        catch (OperationCanceledException) { Console.WriteLine("Parallel HTTPS scan cancellation completed."); }
        return results.Any(result => result.Milliseconds is not null) ? 0 : 2;
    }
    var specs = new List<ConnectSpec>();
    foreach (var node in nodes.Where(node => node.Supported))
    {
        var spec = new ConnectSpec(node, new(), new(), LatencyService.FreePort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        await CoreSession.ValidateAsync(spec, executable, probeRoot, lifetime.Token);
        specs.Add(spec);
    }
    Console.WriteLine($"Official core accepted {specs.Count} generated configurations.");
    for (var index = 0; index < specs.Count; index++)
    {
        var spec = specs[index];
        try
        {
            using var core = new CoreSession(executable, new(probeRoot, "core"));
            await core.StartAsync(spec, probeRoot, true, lifetime.Token);
            using var http = LatencyService.ProbeClient(spec); http.Timeout = TimeSpan.FromSeconds(10);
            var timer = Stopwatch.StartNew();
            using var response = await http.GetAsync("https://www.gstatic.com/generate_204", lifetime.Token);
            response.EnsureSuccessStatusCode();
            Console.WriteLine($"Node {index + 1}: forced-VPN HTTPS HTTP {(int)response.StatusCode}, {timer.ElapsedMilliseconds} ms ({spec.Server.Transport}/{spec.Server.Security}).");
            return 0;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or UserError or IOException)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            Console.WriteLine($"Node {index + 1}: probe failed ({error.GetType().Name}; connection details withheld).");
        }
    }
    Console.WriteLine("Import/schema succeeded, but no live HTTPS probe succeeded.");
    return 2;
}
catch (Exception error)
{
    if (error is TypeInitializationException initialization) Console.WriteLine($"Initialization failure in {initialization.TypeName}: {initialization.InnerException?.GetType().Name}.");
    if (error is UserError) Console.WriteLine(SafeLog.Redact(error.Message));
    Console.WriteLine($"Subscription verification failed ({error.GetType().Name}; private URL and credentials withheld).");
    return 1;
}
finally
{
    Directory.Delete(probeRoot, true);
}
