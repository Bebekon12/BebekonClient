using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

[Trait("Category", "WfpKernel")]
public class WfpKernelTests
{
    [Fact]
    public async Task TemporaryFiltersProduceCorrelatedDropEventsAndRestoreLoopback()
    {
        if (!IsEnabled()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var checks = await KillSwitch.TestLocalFilteringAsync(deadline.Token);
        Assert.Equal(4, checks.Length);
        Assert.All(checks, check =>
        {
            Assert.True(check.Outcome == WfpTestOutcome.Passed, $"{check.AddressFamily} {check.Transport}: {check.Detail}");
            Assert.NotNull(check.FilterId);
            Assert.True(check.Restored);
        });
    }

    [Fact]
    public async Task ScopedPersistentPolicyBlocksWithoutItsOwningEngineAndIsRemovable()
    {
        if (!IsEnabled()) return;
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var target = (IPEndPoint)receiver.LocalEndpoint;
        var provider = Guid.NewGuid();
        var profile = new Profile { Rules = [
            new() { Name = "Loopback fixture", Kind = RuleKind.Network, Values = ["127.0.0.1/32"],
                Port = target.Port, Transport = RuleTransport.Tcp, SystemProtection = true }
        ] };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;

        async Task Baseline()
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(target, ct);
            using var accepted = await receiver.AcceptTcpClientAsync(ct);
        }

        await Baseline();
        try
        {
            Assert.Equal(1, KillSwitch.ApplyPersistent(provider, CoreTests.Spec(profile), [], tunnelReady: false));
            // The transaction and its engine are gone. Only the native persistent policy can block this socket.
            Assert.Equal(1, KillSwitch.PersistentBlockCount(provider));
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var source = (IPEndPoint)probe.LocalEndPoint!;
            var started = DateTime.UtcNow.ToFileTimeUtc();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<Exception>(async () => await probe.ConnectAsync(target, attempt.Token));
            var confirmed = false;
            for (var retry = 0; retry < 8 && !confirmed; retry++)
            {
                confirmed = KillSwitch.ObservePersistentDrop(provider, started, source.Port, target.Port, 6);
                if (!confirmed) await Task.Delay(150, ct);
            }
            Assert.True(confirmed, "A socket exception alone is insufficient. Expected a drop event from the persistent provider.");
        }
        finally { KillSwitch.ClearPersistent(provider); }
        Assert.Equal(0, KillSwitch.PersistentBlockCount(provider));
        await Baseline();
    }

    private static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("BEBEKON_WFP_TEST_VM") != "1") return false;
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "The WFP fixture requires an elevated test VM.");
        return true;
    }
}
