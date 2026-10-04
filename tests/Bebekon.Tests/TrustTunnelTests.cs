using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class TrustTunnelTests
{
    internal static string CoreExe => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
    internal static JsonObject Endpoint() => new() { ["hostname"] = "vpn.example.com", ["addresses"] = new JsonArray("edge.example.com:443", "[2001:db8::1]:8443"), ["username"] = "fixture-user", ["password"] = "fixture-p@ss:word", ["name"] = "Fixture", ["upstream_protocol"] = "http2", ["client_random"] = "aabb/ffff", ["anti_dpi"] = true, ["dns_upstreams"] = new JsonArray("https://dns.example.com/dns-query") };
    internal static string Link(params (ulong Tag, byte[] Bytes)[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields) { WriteVarInt(stream, field.Tag); WriteVarInt(stream, (ulong)field.Bytes.Length); stream.Write(field.Bytes); }
        return "tt://?" + Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private static void WriteVarInt(Stream stream, ulong value)
    {
        var size = value < 64 ? 1 : value < 16384 ? 2 : value < (1ul << 30) ? 4 : 8;
        var data = new byte[size]; for (var i = size - 1; i >= 0; i--) { data[i] = (byte)value; value >>= 8; }
        data[0] |= (byte)((size == 1 ? 0 : size == 2 ? 1 : size == 4 ? 2 : 3) << 6); stream.Write(data);
    }
    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
    private static (ulong, byte[])[] Fields => [(0, [1]), (1, Text("vpn.example.com")), (2, Text("edge.example.com:443")), (2, Text("[2001:db8::1]:8443")), (5, Text("fixture-user")), (6, Text("fixture-p@ss:word")), (9, [1]), (10, [1]), (11, Text("aabb/ffff")), (12, Text("Fixture")), (13, new byte[] { 33 }.Concat(Text("https://dns.example.com/dns-query")).ToArray())];
    [Fact]
    public void OfficialLinksLegacyLinksJsonAndTomlPreserveIdentity()
    {
        // Unknown QUIC varint tags must not wrap to a known byte tag.
        var link = Link(Fields.Concat(new[] { (257ul, Text("ignored")) }).ToArray());
        var expected = Assert.Single(VlessParser.ParseSubscription(Endpoint().ToJsonString()));
        var toml = """
            [endpoint]
            hostname = "vpn.example.com"
            addresses = ["edge.example.com:443", "[2001:db8::1]:8443"]
            username = "fixture-user"
            password = "fixture-p@ss:word"
            name = "Fixture"
            upstream_protocol = "http2"
            client_random = "aabb/ffff"
            anti_dpi = true
            dns_upstreams = ["https://dns.example.com/dns-query"]
            """;
        foreach (var source in new[] { link, link.Replace("tt://?", "tt://"), Convert.ToBase64String(Text(link)), toml })
        {
            var s = Assert.Single(VlessParser.ParseSubscription(source)); Assert.True(s.Supported); Assert.Equal("trusttunnel", s.Type);
            Assert.Equal(ServerRefresh.ConnectionKey(expected), ServerRefresh.ConnectionKey(s)); Assert.Equal(expected.Id, s.Id);
        }
        Assert.Equal("TrustTunnel · HTTP/2", expected.Protocol);
    }
    [Fact]
    public void FullTomlIgnoresProviderListenersAndRulesAndRoundTripsSecretsSafely()
    {
        var text = """
            vpn_mode = "selective"
            exclusions = ["example.com"]
            post_quantum_group_enabled = false
            [endpoint]
            hostname = "vpn.example.com"
            addresses = ["edge.example.com:443"]
            username = 'fixture-user'
            password = 'a"b\c'
            [listener.tun]
            device_name = "ProviderTunnel"
            included_routes = ["0.0.0.0/0"]
            """;
        var s = Assert.Single(VlessParser.ParseSubscription(text)); Assert.False(s.TrustTunnel!.PostQuantum);
        var generated = TrustTunnelConfig.Generate(s, new(30001, new string('b', 48)), [30002]);
        var root = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(generated)!;
        var endpoint = Assert.IsType<Tomlyn.Model.TomlTable>(root["endpoint"]);
        Assert.Equal("a\"b\\c", endpoint["password"]); Assert.DoesNotContain("ProviderTunnel", generated); Assert.DoesNotContain("listener.tun", generated);
        Assert.Contains("vpn_mode = \"general\"", generated);
        var reimported = Assert.Single(VlessParser.ParseSubscription(generated)); Assert.Equal("127.0.0.1", reimported.Host);
    }
    [Fact]
    public void OfficialSubscriptionJsonUsesSingularAddressAndVersion()
    {
        var root = Endpoint(); root.Remove("addresses"); root["address"] = "edge.example.com:443"; root["version"] = 1;
        var s = Assert.Single(VlessParser.ParseSubscription(root.ToJsonString())); Assert.Equal("edge.example.com", s.Host); Assert.Equal(443, s.Port);
        root["version"] = 2; Assert.Throws<UserError>(() => VlessParser.ParseSubscription(root.ToJsonString()));
    }
    [Theory]
    [InlineData("tt://?")] [InlineData("tt://?AQoA")] [InlineData("tt://?%ZZ")] [InlineData("tt://?__8")]
    public void BadLinksFailWithoutExposingSecrets(string source) => Assert.DoesNotContain(source, Assert.Throws<UserError>(() => ProtocolParser.Parse(source)).Message);
    [Fact]
    public void RejectsMalformedFieldsAndFutureVersions()
    {
        Assert.Throws<UserError>(() => ProtocolParser.Parse(Link(Fields.Concat(new[] { (0ul, new byte[] { 3 }) }).ToArray())));
        Assert.Throws<UserError>(() => ProtocolParser.Parse(Link(Fields.Concat(new[] { (4ul, new byte[] { 2 }) }).ToArray())));
        Assert.Throws<UserError>(() => ProtocolParser.Parse(Link(Fields.Concat(new[] { (9ul, new byte[] { 0 }) }).ToArray())));
        foreach (var (key, value) in new (string, JsonNode?)[] { ("addresses", new JsonArray("edge.example.com:443/path")), ("hostname", "bad\r\nhost"), ("username", "a:b"), ("client_random", "aabb/ff"), ("certificate", "C:/secret.pem"), ("upstream_protocol", "http1"), ("tls_profile", "chrome"), ("dns_upstreams", new JsonArray("file:///secret")) })
        { var root = Endpoint(); root[key] = value; Assert.Throws<UserError>(() => VlessParser.ParseSubscription(root.ToJsonString())); }
        var duplicate = Endpoint().ToJsonString().Replace("\"hostname\":", "\"hostname\":\"duplicate\",\"hostname\":"); Assert.Throws<UserError>(() => VlessParser.ParseSubscription(duplicate));
    }
    [Fact]
    public void DerCertificateChainsAndLongVarintsAreDecoded()
    {
        using var key = RSA.Create(2048); var request = new CertificateRequest("CN=vpn.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var s = ProtocolParser.Parse(Link(Fields.Concat(new[] { (8ul, cert.RawData), (6ul, Text(new string('x', 300))) }).ToArray()));
        Assert.Contains("BEGIN CERTIFICATE", s.TrustTunnel!.Certificate); Assert.Equal(300, s.Password.Length);
        Assert.Throws<UserError>(() => ProtocolParser.Parse(Link(Fields.Concat(new[] { (8ul, new byte[] { 48, 5, 1 }) }).ToArray())));
    }
    [Fact]
    public void CredentialsPersistAndRefreshKeepsSelectedIdAndFavorite()
    {
        var s = Assert.Single(VlessParser.ParseSubscription(Endpoint().ToJsonString())); s.SubscriptionId = "fixture"; s.Favorite = true;
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N"));
        try { var store = new StateStore(root); store.Save(new() { Servers = [s] }); var loaded = Assert.Single(store.Load().Servers); Assert.Equal(ServerRefresh.ConnectionKey(s), ServerRefresh.ConnectionKey(loaded)); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        var incoming = Assert.Single(VlessParser.ParseSubscription(Endpoint().ToJsonString())); incoming.Name = "Renamed";
        var kept = Assert.Single(ServerRefresh.Merge("fixture", [s], [incoming], s.Id)); Assert.Equal(s.Id, kept.Id); Assert.True(kept.Favorite);
        Assert.DoesNotContain("fixture", SafeLog.Redact("tt://?fixture-secret"));
        Assert.DoesNotContain("fixture", new Subscription { Source = "https://fixture:fixture-secret@example.com/sub" }.SafeSource);
        s.Transport = "http3"; s.PreferredTrustTunnelTransport = "http3";
        incoming = Assert.Single(VlessParser.ParseSubscription(Endpoint().ToJsonString()));
        kept = Assert.Single(ServerRefresh.Merge("fixture", [s], [incoming], s.Id)); Assert.Equal("http3", kept.Transport); Assert.Equal("http3", kept.PreferredTrustTunnelTransport);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task GeneratedMainCoreSchemaIncludesPinnedRelaysBeforeEveryUserRule(bool pinned)
    {
        var tt = Assert.Single(VlessParser.ParseSubscription(Endpoint().ToJsonString()));
        var main = pinned ? CoreTests.Node() : tt;
        var spec = CoreTests.Spec(node: main, profile: new() { DefaultRoute = RouteTarget.Vpn, Rules = [new() { Name = "Pinned", Values = ["example.com"], ServerId = tt.Id }] }) with { RuleServers = [tt] };
        var runtime = TrustTunnelRuntime.Create(spec, false, null)!;
        var config = JsonNode.Parse(ConfigGenerator.Generate(spec, false, null, runtime))!;
        Assert.Equal("direct", (string?)config["route"]!["rules"]![0]!["outbound"]);
        Assert.All(config["inbounds"]!.AsArray().Where(n => (string?)n!["type"] == "direct"), n => Assert.Equal("127.0.0.1", (string?)n!["listen"]));
        Assert.Equal(2, config["inbounds"]!.AsArray().Count(n => (string?)n!["type"] == "direct"));
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { await CoreSession.ValidateAsync(spec, CoreExe, root, default); }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task BasicAuthSubscriptionIsSentOnlyAsHeaderAndProviderRulesAreIgnored()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)); string? auth = null; string? requestLine = null;
        var responder = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct.Token); await using var stream = tcp.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            requestLine = await reader.ReadLineAsync(ct.Token); string? line; while ((line = await reader.ReadLineAsync(ct.Token)) is { Length: > 0 }) if (line.StartsWith("Authorization:")) auth = line;
            var body = Text(Endpoint().ToJsonString()); await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct.Token); await stream.WriteAsync(body, ct.Token);
        });
        var node = Assert.Single(await SubscriptionLoader.LoadAsync($"http://fixture:p%40ss%3Aword@127.0.0.1:{port}/sub", ct.Token)); await responder;
        Assert.Equal("trusttunnel", node.Type); Assert.Equal("Authorization: Basic " + Convert.ToBase64String(Text("fixture:p@ss:word")), auth); Assert.Equal("GET /sub HTTP/1.1", requestLine);
        await Assert.ThrowsAsync<UserError>(() => SubscriptionLoader.LoadAsync("http://fixture:secret@example.com/sub"));
    }
    [Fact]
    public void SubscriptionOnlyV2IsRecognizedButRequiresTheLoader()
    {
        var url = "https://fixture:secret@vpn.example.com/sub"; var link = Link((0, [2]), (14, Text(url)));
        Assert.Equal(url, TrustTunnelParser.SubscriptionUrl(link)); Assert.Throws<UserError>(() => ProtocolParser.Parse(link));
        Assert.Throws<UserError>(() => TrustTunnelParser.SubscriptionUrl(Link((0, [1]), (14, Text(url)))));
        Assert.Throws<UserError>(() => TrustTunnelParser.SubscriptionUrl(Link((0, [2]), (14, Text("http://vpn.example.com/sub")))));
    }
    [Fact]
    public async Task FiveSecondProbeCancellationStopsTheOfficialClientAndRemovesItsConfig()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var root = Endpoint(); root["addresses"] = new JsonArray("127.0.0.1:" + port); root["anti_dpi"] = false;
        var s = Assert.Single(VlessParser.ParseSubscription(root.ToJsonString()));
        var result = await new LatencyService(CoreExe).MeasureAsync(s, LatencyMode.HttpsGet, true, default); Assert.True(result.TimedOut); Assert.Null(result.Milliseconds);
    }
}
