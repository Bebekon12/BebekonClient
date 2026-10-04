using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public sealed class TrustTunnelFixtureTheoryAttribute : TheoryAttribute
{
    public TrustTunnelFixtureTheoryAttribute() { if (!File.Exists(Environment.GetEnvironmentVariable("BEBEKON_TRUSTTUNNEL_FIXTURE"))) Skip = "Run scripts/test-trusttunnel.ps1 with WSL Ubuntu and OpenSSL to start the official endpoint fixture."; }
}

public class TrustTunnelIntegrationTests
{
    [TrustTunnelFixtureTheory]
    [InlineData("http2", false)] [InlineData("http3", false)] [InlineData("auto", false)]
    [InlineData("http2", true)] [InlineData("http3", true)]
    public async Task OfficialEndpointCarriesTcpUdpWithVerifiedCertificateAndPinnedRules(string transport, bool pinned)
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("BEBEKON_TRUSTTUNNEL_FIXTURE")!))!;
        var node = ProtocolParser.Parse(fixture["link"]!.GetValue<string>()); node.Transport = transport;
        Assert.Equal("alias.fixture.invalid", node.TrustTunnel!.CustomSni); Assert.False(node.TlsInsecure); Assert.Contains("BEGIN CERTIFICATE", node.TrustTunnel.Certificate);
        var address = fixture["address"]!.GetValue<string>();
        var selected = pinned ? ProtocolParser.Parse($"vless://{CoreTests.Id}@127.0.0.1:1?type=xhttp") : node;
        var profile = new Profile { DefaultRoute = RouteTarget.Vpn, Rules = pinned ? [new() { Name = "Pinned", Kind = RuleKind.Network, Values = [address + "/32"], ServerId = node.Id, UseVpn = true }] : [] };
        var settings = new Settings { TunnelMode = TunnelMode.Proxy };
        var spec = new ConnectSpec(selected, profile, settings, LatencyService.FreePort(), new string('b', 48), [node]);
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            using var client = new CoreSession(TrustTunnelTests.CoreExe, new(root, "client"));
            await client.StartAsync(spec, root, !pinned, lifetime.Token, config =>
            {
                // WSL's virtual endpoint is local to this fixture; let Windows route its private subnet.
                var json = JsonNode.Parse(config)!; json["route"]!["auto_detect_interface"] = false;
                if (pinned) { var rules = json["route"]!["rules"]!.AsArray(); var probe = rules.Single(r => (string?)r?["inbound"]?[0] == "vpn-probe"); rules.Remove(probe); }
                return json.ToJsonString();
            });
            using var http = LatencyService.ProbeClient(spec);
            Assert.Equal("official-trusttunnel-fixture|" + address, await http.GetStringAsync($"http://{address}:{fixture["httpPort"]}/", lifetime.Token));
            Assert.Equal("udp-fixture|" + address, await ProtocolTests.SocksUdp(spec, fixture["udpPort"]!.GetValue<int>(), lifetime.Token, address));
            Assert.True(client.Running);
            var pids = client.CompanionPids.Append(client.Pid!.Value).ToArray(); Assert.Equal(pinned ? 3 : 2, pids.Length);
            if (transport == "auto" || pinned)
            { using var crashed = Process.GetProcessById(pids[0]); crashed.Kill(); await crashed.WaitForExitAsync(lifetime.Token); }
            client.Dispose(); await Task.Delay(150, lifetime.Token);
            Assert.False(client.Running); Assert.Empty(Directory.GetFiles(root, "*.toml")); Assert.False(File.Exists(Path.Combine(root, "sing-box.json")));
            foreach (var pid in pids) Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        finally { Directory.Delete(root, true); }
    }
}
