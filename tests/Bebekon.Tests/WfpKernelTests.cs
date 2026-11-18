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
            using var identity = WindowsIdentity.GetCurrent();
            string[] transports = identity.IsSystem ? [] : [Environment.ProcessPath!];
            Assert.Equal(1, KillSwitch.ApplyPersistent(provider, CoreTests.Spec(profile), transports, tunnelReady: false));
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

    [Fact]
    public async Task InstalledRuleProvesFailoverAndKeepsItsProductionFilters()
    {
        if (!IsEnabled(documentationRangeOnly: true)) return;
        var provider = Guid.NewGuid();
        var rule = new RoutingRule { Name = "Documentation network", Kind = RuleKind.Network, Values = ["192.0.2.87", "2001:db8::87"],
            Transport = RuleTransport.Tcp, SystemProtection = true };
        var spec = CoreTests.Spec(new() { Rules = [rule] });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            Assert.Equal(2, KillSwitch.ApplyPersistent(provider, spec, [Environment.ProcessPath!], tunnelReady: false));
            var report = await KillSwitch.TestInstalledRuleAsync(provider, rule, deadline.Token);
            Assert.Equal(2, report.Length);
            Assert.All(report, check =>
            {
                Assert.True(check.Outcome == WfpTestOutcome.Passed, $"{check.AddressFamily}: {check.Detail}");
                Assert.NotNull(check.FilterId);
                Assert.True(check.Restored);
            });
            Assert.Equal(2, KillSwitch.PersistentBlockCount(provider));
            rule.Values = ["192.0.2.88"];
            await Assert.ThrowsAsync<UserError>(() => KillSwitch.TestInstalledRuleAsync(provider, rule, deadline.Token));
        }
        finally { KillSwitch.ClearPersistent(provider); }
    }

    private static bool IsEnabled(bool documentationRangeOnly = false)
    {
        if (!OperatingSystem.IsWindows() ||
            !(Environment.GetEnvironmentVariable("BEBEKON_WFP_TEST_VM") == "1" ||
              documentationRangeOnly && Environment.GetEnvironmentVariable("BEBEKON_WFP_DOCUMENTATION_FIXTURE") == "1")) return false;
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "The WFP fixture requires an elevated test VM.");
        return true;
    }
}
