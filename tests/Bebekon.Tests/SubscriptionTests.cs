using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class SubscriptionTests
{
    internal static JsonObject Xray(string network = "grpc", string security = "reality") => new()
    {
        ["remarks"] = "Estonia · JSON fixture",
        ["dns"] = new JsonObject { ["servers"] = new JsonArray("provider-dns.example") },
        ["inbounds"] = new JsonArray(new JsonObject { ["port"] = 10888, ["protocol"] = "socks" }),
        ["routing"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["outboundTag"] = "block" }) },
        ["outbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "proxy", ["protocol"] = "vless",
                ["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = "edge.example.com", ["port"] = 443,
                    ["users"] = new JsonArray(new JsonObject { ["id"] = CoreTests.Id, ["encryption"] = "none", ["flow"] = network == "tcp" ? "xtls-rprx-vision" : "" })
                }) },
                ["streamSettings"] = new JsonObject
                {
                    ["network"] = network, ["security"] = security,
                    ["grpcSettings"] = new JsonObject { ["serviceName"] = "edge/service", ["authority"] = "", ["mode"] = false },
                    [security == "reality" ? "realitySettings" : "tlsSettings"] = new JsonObject
                    {
                        ["serverName"] = "tls.example.com", ["fingerprint"] = "firefox",
                        ["publicKey"] = new string('A', 43), ["shortId"] = "abcd", ["alpn"] = new JsonArray("h2")
                    }
                }
            },
            new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" },
            new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" }
        }
    };
    private static JsonNode Outbound(JsonObject profile) => profile["outbounds"]![0]!;
    private static JsonNode Stream(JsonObject profile) => Outbound(profile)["streamSettings"]!;
    private static Server Parse(JsonObject profile) => Assert.Single(VlessParser.ParseSubscription(profile.ToJsonString()));

    [Fact]
    public void UltimaStyleArrayPreservesNineNodes()
    {
        var profiles = new JsonArray();
        for (var i = 0; i < 9; i++) { var profile = Xray(i == 2 ? "tcp" : "grpc", i == 3 ? "tls" : "reality"); profile["remarks"] = "Fixture " + i; profiles.Add(profile); }
        var nodes = VlessParser.ParseSubscription(profiles.ToJsonString());
        Assert.Equal(9, nodes.Count); Assert.All(nodes, node => Assert.True(node.Supported));
        Assert.Equal(8, nodes.Count(node => node.Transport == "grpc")); Assert.Equal("xtls-rprx-vision", nodes[2].Flow);
    }
    [Theory] [InlineData("tls")] [InlineData("reality")]
    public void MapsGrpcTlsAndReality(string security)
    {
        var node = Parse(Xray(security: security));
        Assert.True(node.Supported); Assert.Equal("edge.example.com", node.Host); Assert.Equal(443, node.Port);
        Assert.Equal(CoreTests.Id, node.Uuid); Assert.Equal("Estonia · JSON fixture", node.Name);
        Assert.Equal(security, node.Security); Assert.Equal("tls.example.com", node.Sni); Assert.Equal("firefox", node.Fingerprint);
        Assert.Equal("edge/service", node.ServiceName); Assert.Equal(new[] { "h2" }, node.Alpn);
        if (security == "reality") { Assert.Equal(new string('A', 43), node.PublicKey); Assert.Equal("abcd", node.ShortId); }
        var generated = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(node: node)))!;
        Assert.Equal("grpc", (string?)generated["outbounds"]![0]!["transport"]!["type"]);
        Assert.Equal("edge/service", (string?)generated["outbounds"]![0]!["transport"]!["service_name"]);
        Assert.Equal("direct", (string?)generated["route"]!["final"]);
        Assert.DoesNotContain("provider-dns.example", generated.ToJsonString()); Assert.DoesNotContain("10888", generated.ToJsonString());
    }
    [Fact]
    public void Base64JsonBomAndPropertyOrderKeepStableIdentity()
    {
        var profile = Xray(); var content = profile.ToJsonString(); var expected = Parse(profile);
        Assert.Equal(expected.Id, Assert.Single(VlessParser.ParseSubscription(Convert.ToBase64String(Encoding.UTF8.GetBytes("\uFEFF  " + content)))).Id);
        var reordered = new JsonObject(profile.Reverse().Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        Assert.Equal(expected.Id, Parse(reordered).Id);
    }
    [Fact]
    public void HandlesMultipleUsersAndDuplicateConfigurations()
    {
        var profile = Xray(); var users = Outbound(profile)["settings"]!["vnext"]![0]!["users"]!.AsArray();
        var second = users[0]!.DeepClone(); second["id"] = "afdeca7e-73e2-4f5d-9e7b-0668cb7aa900"; users.Add(second);
        var profiles = new JsonArray(profile, profile.DeepClone());
        Assert.Equal(2, VlessParser.ParseSubscription(profiles.ToJsonString()).Count);
    }
    [Fact]
    public void NewXrayFlatSettingsAndRawTransportAreAccepted()
    {
        var profile = Xray("raw"); var outbound = Outbound(profile);
        outbound["settings"] = new JsonObject { ["address"] = "2001:db8::1", ["port"] = 8443, ["id"] = CoreTests.Id, ["encryption"] = "none", ["flow"] = "xtls-rprx-vision" };
        var node = Parse(profile); Assert.True(node.Supported); Assert.Equal("tcp", node.Transport); Assert.Equal("2001:db8::1", node.Host); Assert.Equal(8443, node.Port);
    }
    [Theory] [InlineData("ws")] [InlineData("http")] [InlineData("httpupgrade")]
    public void MapsTransportPathAndHost(string network)
    {
        var profile = Xray(network, "tls"); Stream(profile)[network + "Settings"] = new JsonObject { ["path"] = "/edge?a=b&c=d" };
        if (network == "ws") Stream(profile)[network + "Settings"]!["headers"] = new JsonObject { ["Host"] = "cdn.example.com" };
        else Stream(profile)[network + "Settings"]!["host"] = network == "http" ? new JsonArray("cdn.example.com") : JsonValue.Create("cdn.example.com");
        var node = Parse(profile); Assert.True(node.Supported); Assert.Equal("/edge?a=b&c=d", node.Path); Assert.Equal("cdn.example.com", node.TransportHost);
    }
    [Theory] [InlineData("xhttp")] [InlineData("kcp")]
    public void UnsupportedTransportStaysVisible(string network)
    {
        var node = Parse(Xray(network)); Assert.False(node.Supported); Assert.Throws<UserError>(() => ConfigGenerator.Generate(CoreTests.Spec(node: node)));
    }
    [Theory] [InlineData("authority")] [InlineData("multiMode")] [InlineData("mode")]
    public void UnsupportedGrpcOptionsAreNotSilentlyDropped(string field)
    {
        var profile = Xray(); Stream(profile)["grpcSettings"]![field] = field == "authority" ? JsonValue.Create("custom.example.com") : JsonValue.Create(true);
        Assert.False(Parse(profile).Supported);
    }
    [Fact]
    public void InsecureTlsIsNotAcceptedAsSupported()
    {
        var profile = Xray(security: "tls"); Stream(profile)["tlsSettings"]!["allowInsecure"] = true; Assert.False(Parse(profile).Supported);
    }
    [Theory]
    [InlineData("[]")] [InlineData("{}")] [InlineData("{\"outbounds\":[]}")]
    [InlineData("{\"proxies\":[]}")] [InlineData("{\"outbounds\":null}")]
    [InlineData("[{\"outbounds\":[]},42]")]
    [InlineData("{\"outbounds\":[{\"protocol\":\"trojan\"}]}")]
    [InlineData("{\"outbounds\":[],\"outbounds\":[]}")]
    public void RejectsMalformedUnknownAndDuplicateJson(string content) => Assert.Throws<UserError>(() => VlessParser.ParseSubscription(content));
    [Fact]
    public void InvalidVlessCredentialsFailWithoutSecretInErrors()
    {
        var profile = Xray(); Outbound(profile)["settings"]!["vnext"]![0]!["users"]![0]!["id"] = "private-invalid-id";
        var error = Assert.Throws<UserError>(() => Parse(profile)); Assert.DoesNotContain("private-invalid-id", error.Message);
    }
    [Fact]
    public async Task SubscriptionUrlLoadsJsonOverHttp()
    {
        var content = Xray().ToJsonString();
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            string? line; while ((line = await reader.ReadLineAsync(timeout.Token)) is not null && line.Length > 0) { }
            var body = Encoding.UTF8.GetBytes(content);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, timeout.Token); await stream.WriteAsync(body, timeout.Token);
        }, timeout.Token);
        var node = Assert.Single(await SubscriptionLoader.LoadAsync($" http://127.0.0.1:{port}/subscription ", timeout.Token));
        await response; Assert.Equal("grpc", node.Transport); Assert.True(node.Supported);
    }
}
