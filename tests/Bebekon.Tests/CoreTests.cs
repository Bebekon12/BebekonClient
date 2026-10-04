using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public class CoreTests
{
    public const string Id = "95ef1266-b9e5-46a4-bd68-9bfc13299732";
    public static Server Node(string parameters = "security=none&type=tcp") => VlessParser.Parse($"vless://{Id}@example.com:443?{parameters}#Estonia");
    public static ConnectSpec Spec(Profile? profile = null, Server? node = null, Settings? settings = null) => new(node ?? Node(), profile ?? new(), settings ?? new(), 17999, new string('a', 48));
    [Fact] public void ParsesTcp() { var s = Node(); Assert.Equal("example.com", s.Host); Assert.Equal(443, s.Port); Assert.Equal(Id, s.Uuid); Assert.Equal("Estonia", s.Name); Assert.True(s.Supported); }
    [Fact] public void ParsesGrpcAndEscapes() { var s = Node("type=grpc&security=tls&sni=edge.example.com&serviceName=my%2Fservice&fp=firefox"); Assert.Equal("my/service", s.ServiceName); Assert.Equal("edge.example.com", s.Sni); Assert.Equal("firefox", s.Fingerprint); }
    [Fact] public void PreservesAlpn() { var s = Node("type=grpc&security=tls&alpn=h2,http%2F1.1"); Assert.True(s.Supported); var config=JsonNode.Parse(ConfigGenerator.Generate(Spec(node:s)))!; Assert.Equal("h2",(string?)config["outbounds"]![0]!["tls"]!["alpn"]![0]); Assert.Equal("http/1.1",s.Alpn[1]); }
    [Fact] public void ParsesReality() { var s = Node("type=tcp&security=reality&sni=example.net&pbk=" + new string('A', 43) + "&sid=abcd&fp=chrome&flow=xtls-rprx-vision"); Assert.True(s.Supported); Assert.Equal("abcd", s.ShortId); }
    [Fact] public void ParsesIpv6() { var s = VlessParser.Parse($"vless://{Id}@[2001:db8::1]:443#IPv6"); Assert.Equal("2001:db8::1", s.Host); }
    [Theory] [InlineData("vless://bad@example.com:443")] [InlineData("vless://95ef1266-b9e5-46a4-bd68-9bfc13299732@example.com")] [InlineData("https://example.com")] [InlineData("vless://95ef1266-b9e5-46a4-bd68-9bfc13299732@example.com:443?security=reality&pbk=bad")] public void RejectsInvalid(string uri) => Assert.Throws<UserError>(() => VlessParser.Parse(uri));
    [Theory] [InlineData("kcp")] public void KeepsUnsupportedVisible(string transport) { var s = Node("type=" + transport); Assert.False(s.Supported); Assert.Throws<UserError>(() => ConfigGenerator.Generate(Spec(node:s))); }
    [Fact] public void RejectsRepeatedParameters() => Assert.Throws<UserError>(() => Node("type=tcp&type=grpc"));
    [Fact] public void Base64AndLinesAgree() { var text = $"vless://{Id}@a.example.com:443#A\nvless://{Id}@b.example.com:443#B"; Assert.Equal(2, VlessParser.ParseSubscription(text).Count); var parsed = VlessParser.ParseSubscription(Convert.ToBase64String(Encoding.UTF8.GetBytes(text))); Assert.Equal("B", parsed[1].Name); }
    [Fact] public void UnknownFormatFails() => Assert.Throws<UserError>(() => VlessParser.ParseSubscription("{\"proxies\":[]}"));
    [Fact] public void MalformedLineIsNotSilentlyDropped() => Assert.Throws<UserError>(() => VlessParser.ParseSubscription($"vless://{Id}@a.example.com:443\nss://unsupported"));
    [Fact] public void SelectiveDefaultsDirect() { var c = JsonNode.Parse(ConfigGenerator.Generate(Spec()))!; Assert.Equal("direct", (string?)c["route"]!["final"]); Assert.Equal("direct-dns", (string?)c["dns"]!["final"]); Assert.True((bool)c["route"]!["auto_detect_interface"]!); }
    [Fact] public void EntirePcDefaultsVpn() { var c = JsonNode.Parse(ConfigGenerator.Generate(Spec(new() { DefaultRoute = RouteTarget.Vpn })))!; Assert.Equal("vpn", (string?)c["route"]!["final"]); Assert.Equal("vpn-dns", (string?)c["dns"]!["final"]); }
    [Fact] public void RoutingPriorityPreservesOrderedProcessException()
    {
        var p = new Profile { Rules = [new() { Name="Discord app", Kind=RuleKind.Application, Values=[@"C:\Apps\discord.exe"], UseVpn=false }, new() { Name="Discord", Values=["discord.com"] }, new() { Name="OpenAI", Values=["openai.com"] }] };
        var c = JsonNode.Parse(ConfigGenerator.Generate(Spec(p)))!; var rules = c["route"]!["rules"]!.AsArray();
        Assert.Equal("sniff", (string?)rules[2]!["action"]); Assert.Equal("300ms", (string?)rules[2]!["timeout"]);
        Assert.Equal("direct", (string?)rules[4]!["outbound"]); Assert.Equal(@"C:\Apps\discord.exe", (string?)rules[4]!["process_path"]![0]); Assert.Equal("discord.com", (string?)rules[5]!["domain_suffix"]![0]); Assert.Equal("vpn", (string?)rules[5]!["outbound"]);
        p.Rules.Move(0,2); var changed = JsonNode.Parse(ConfigGenerator.Generate(Spec(p)))!; Assert.Equal("discord.com", (string?)changed["route"]!["rules"]![4]!["domain_suffix"]![0]);
    }
    [Fact] public void DnsMatchesDomainDirectionInSameOrder()
    {
        var p = new Profile { Rules=[new() { Name="Direct", Values=["a.example.com"], UseVpn=false },new() { Name="VPN", Values=["ip2location.com"] }] };
        var c=JsonNode.Parse(ConfigGenerator.Generate(Spec(p)))!;
        Assert.Equal("direct-dns",(string?)c["dns"]!["rules"]![0]!["server"]); Assert.Equal("vpn-dns",(string?)c["dns"]!["rules"]![1]!["server"]); Assert.Equal("vpn",(string?)c["dns"]!["servers"]![1]!["detour"]);
    }
    [Fact] public void SiteIncludesSubdomainsAndKeywordsStayDistinct()
    {
        var p=new Profile {Rules=[new() {Name="Figma",Values=["figma.com"]},new() {Name="Claude",Kind=RuleKind.Contains,Values=["claude"]}]};
        var c=JsonNode.Parse(ConfigGenerator.Generate(Spec(p)))!; Assert.Equal("figma.com",(string?)c["route"]!["rules"]![4]!["domain_suffix"]![0]); Assert.Equal("claude",(string?)c["route"]!["rules"]![5]!["domain_keyword"]![0]);
    }
    [Fact] public void ExactPathDoesNotAlsoMatchOtherExecutablesWithSameName() { var c=JsonNode.Parse(ConfigGenerator.Generate(Spec(new() { Rules=[new() {Name="App",Kind=RuleKind.Application,Values=[@"C:\Apps\foo.exe"]}]})))!; Assert.Null(c["route"]!["rules"]![4]!["process_name"]); }
    [Fact] public void NameFallbackSupported() { var c=JsonNode.Parse(ConfigGenerator.Generate(Spec(new() { Rules=[new() {Name="App",Kind=RuleKind.Application,Values=["foo.exe"]}]})))!; Assert.Equal("foo.exe",(string?)c["route"]!["rules"]![4]!["process_name"]![0]); }
    [Fact] public void ModernFieldsOnly() { var text=ConfigGenerator.Generate(Spec()); foreach(var forbidden in new[]{"geosite","geoip","inet4_address","sniff_override_destination","address_resolver","independent_cache","external_controller"}) Assert.DoesNotContain("\""+forbidden+"\"",text); Assert.Contains("\"dns_mode\": \"hijack\"",text); }
    [Fact] public void CompatibilityDisablesStrictRoute() { var c=JsonNode.Parse(ConfigGenerator.Generate(Spec(settings:new() {CompatibilityMode=true})))!; Assert.False((bool)c["inbounds"]![1]!["strict_route"]!); }
    [Fact] public void ProbeIsForcedThroughVpn() { var c=JsonNode.Parse(ConfigGenerator.Generate(Spec(),true))!; Assert.Single(c["inbounds"]!.AsArray()); Assert.Equal("vpn",(string?)c["route"]!["final"]); Assert.Equal("127.0.0.1",(string?)c["inbounds"]![0]!["listen"]); Assert.NotNull(c["inbounds"]![0]!["users"]); }
    [Fact] public void SettingsSerializationRoundTrip() { var original=new Settings {AutoConnect=true,Language="English",Mtu=1400,CompatibilityMode=true}; var parsed=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(original,Json.Options),Json.Options)!; Assert.Equal(1400,parsed.Mtu); Assert.True(parsed.AutoConnect); Assert.Equal("English",parsed.Language); }
    [Fact] public void ProfileRoundTrip() { var p=new Profile {Name="Работа", Rules=[new() {Name="Claude",Values=["claude.ai"],UseVpn=false}]}; var content=ProfileCodec.Export(p); Assert.DoesNotContain("outbounds",content); var loaded=ProfileCodec.Import(content); Assert.Equal("Работа",loaded.Name); Assert.False(loaded.Rules[0].UseVpn); Assert.Equal(RouteTarget.Direct,loaded.DefaultRoute); }
    [Theory] [InlineData("{\"name\":\"A\",\"rules\":null}")] [InlineData("{\"name\":\"A\",\"defaultRoute\":99}")] [InlineData("invalid")] public void CorruptProfilesRejected(string content)=>Assert.Throws<UserError>(()=>ProfileCodec.Import(content));
    [Theory] [InlineData("https://figma.com")] [InlineData("*.figma.com")] [InlineData("a/b")] public void InvalidDomainRejected(string value)=>Assert.Throws<UserError>(()=>RuleValidation.Validate(new RoutingRule {Name="Bad",Values=[value]}));
    [Fact] public void CidrValidated() { var r=new RoutingRule {Name="IP",Kind=RuleKind.Network,Values=["1.2.3.4"]}; RuleValidation.Validate(r); Assert.Equal("1.2.3.4/32",r.Values[0]); Assert.Throws<UserError>(()=>RuleValidation.Validate(new RoutingRule {Name="Bad",Kind=RuleKind.Network,Values=["1.2.3.4/99"]})); }
    [Fact] public void DpapiProtectsSubscriptionAndServerSecrets()
    {
        var root=Path.Combine(Path.GetTempPath(),"BebekonTests",Guid.NewGuid().ToString("N"));
        try { var store=new StateStore(root); var state=new AppState {Servers=[Node()],Subscriptions=[new() {Source="https://example.com/private-token"}]}; store.Save(state); var bytes=File.ReadAllBytes(Path.Combine(root,"state.dpapi")); Assert.DoesNotContain("private-token",Encoding.UTF8.GetString(bytes)); Assert.DoesNotContain(Id,Encoding.UTF8.GetString(bytes)); Assert.Equal(Id,store.Load().Servers[0].Uuid); }
        finally {Directory.Delete(root,true);}
    }
    [Fact] public void LogsRedactSecrets() { var text=SafeLog.Redact($"Failed vless://{Id}@example.com https://provider.test/private uuid={Id} password=abc"); Assert.DoesNotContain(Id,text); Assert.DoesNotContain("private",text.Replace("private-link","")); Assert.DoesNotContain("abc",text); }
    [Fact] public async Task PipeFramesRoundTrip() { using var memory=new MemoryStream(); var request=new ServiceRequest("StartCore",Spec()); await PipeProtocol.WriteAsync(memory,request,default); memory.Position=0; var loaded=await PipeProtocol.ReadAsync<ServiceRequest>(memory,default); Assert.Equal("StartCore",loaded.Operation); Assert.Equal(Id,loaded.Spec!.Server.Uuid); }
    [Fact] public async Task PipeRejectsOversizedFrames() { using var memory=new MemoryStream(BitConverter.GetBytes(int.MaxValue)); await Assert.ThrowsAsync<InvalidDataException>(()=>PipeProtocol.ReadAsync<ServiceRequest>(memory,default)); }
}
