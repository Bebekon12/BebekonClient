using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public class CoreIntegrationTests
{
    private static string CoreExe => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","..","core","sing-box.exe"));
    [Theory] [InlineData("tcp","none")] [InlineData("grpc","tls")] [InlineData("ws","tls")] [InlineData("httpupgrade","tls")] [InlineData("tcp","reality")]
    public async Task OfficialCoreAcceptsGeneratedSchema(string transport,string security)
    {
        var s=CoreTests.Node($"type={transport}&security={security}&sni=example.com&pbk={new string('A',43)}&sid=abcd");
        var root=Path.Combine(Path.GetTempPath(),"BebekonTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try { var path=Path.Combine(root,"config.json"); await File.WriteAllTextAsync(path,ConfigGenerator.Generate(CoreTests.Spec(node:s))); using var core=new CoreProcess(CoreExe,new(root,"core"));await core.ValidateAsync(path,default); }
        finally {Directory.Delete(root,true);}
    }
    [Fact]
    public async Task RealVlessSelectiveAndEntirePcRouting()
    {
        // Loopback VLESS fixture. The target reports the TCP source: VPN is 127.0.0.2,
        // direct is 127.0.0.1. This proves data traverses the real VLESS core, without provider secrets.
        var root=Path.Combine(Path.GetTempPath(),"BebekonTests",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var target=new TcpListener(IPAddress.Loopback,0);target.Start();var targetPort=((IPEndPoint)target.LocalEndpoint).Port;
        var targetTask=RespondAsync(target,lifetime.Token);
        try
        {
            var vpnPort=LatencyService.FreePort(); var serverPath=Path.Combine(root,"server.json");
            await File.WriteAllTextAsync(serverPath,$$$"""{"log":{"level":"info"},"dns":{"servers":[{"type":"hosts","tag":"hosts","predefined":{"vpn.example.com":["127.0.0.1"],"direct.example.com":["127.0.0.1"]}}]},"inbounds":[{"type":"vless","listen":"127.0.0.1","listen_port":{{{vpnPort}}},"users":[{"uuid":"{{{CoreTests.Id}}}"}]}],"outbounds":[{"type":"direct","inet4_bind_address":"127.0.0.2","domain_resolver":"hosts"}]}""");
            using var serverCore=new CoreProcess(CoreExe,new(root,"server"));await serverCore.StartAsync(serverPath,lifetime.Token);
            foreach(var global in new[]{false,true})
            {
                var node=VlessParser.Parse($"vless://{CoreTests.Id}@127.0.0.1:{vpnPort}?type=tcp&security=none#Fixture");
                var profile=new Profile {DefaultRoute=global?RouteTarget.Vpn:RouteTarget.Direct,Rules=[new(){Name="VPN test site",Values=["vpn.example.com"]}]};
                var spec=new ConnectSpec(node,profile,new(){TunnelMode=TunnelMode.Proxy},LatencyService.FreePort(),new string('b',48));
                var config=JsonNode.Parse(ConfigGenerator.Generate(spec))!; var mixedPort=LatencyService.FreePort(); config["inbounds"]![1]!["listen_port"]=mixedPort;
                config["dns"]!["servers"]=new JsonArray {new JsonObject {["type"]="hosts",["tag"]="direct-dns",["predefined"]=new JsonObject {["vpn.example.com"]=new JsonArray("127.0.0.1"),["direct.example.com"]=new JsonArray("127.0.0.1")}},new JsonObject {["type"]="hosts",["tag"]="vpn-dns",["predefined"]=new JsonObject {["vpn.example.com"]=new JsonArray("127.0.0.1"),["direct.example.com"]=new JsonArray("127.0.0.1")}}};
                var path=Path.Combine(root,"client.json");await File.WriteAllTextAsync(path,config.ToJsonString());
                using var clientCore=new CoreProcess(CoreExe,new(root,"client"));await clientCore.StartAsync(path,lifetime.Token);
                using var http=new HttpClient(new SocketsHttpHandler {UseProxy=true,Proxy=new WebProxy($"http://127.0.0.1:{mixedPort}")}) {Timeout=TimeSpan.FromSeconds(5)};
                var vpn=await http.GetStringAsync($"http://vpn.example.com:{targetPort}/",lifetime.Token); var direct=await http.GetStringAsync($"http://direct.example.com:{targetPort}/",lifetime.Token);
                Assert.Equal("127.0.0.2",vpn);Assert.Equal(global?"127.0.0.2":"127.0.0.1",direct);
                using var sniffRequest=new HttpRequestMessage(HttpMethod.Get,$"http://127.0.0.1:{targetPort}/"); sniffRequest.Headers.Host="vpn.example.com";
                using var sniffResponse=await http.SendAsync(sniffRequest,lifetime.Token); Assert.Equal("127.0.0.2",await sniffResponse.Content.ReadAsStringAsync(lifetime.Token));
                using var probe=LatencyService.ProbeClient(spec); Assert.Equal("127.0.0.2",await probe.GetStringAsync($"http://127.0.0.1:{targetPort}/",lifetime.Token));
                clientCore.Stop();Assert.False(clientCore.Running);
            }
        }
        finally {lifetime.Cancel();target.Stop();try{await targetTask;}catch(OperationCanceledException){}Directory.Delete(root,true);}
    }
    private static async Task RespondAsync(TcpListener listener,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            using var client=await listener.AcceptTcpClientAsync(ct);await using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);string? line;
            while((line=await reader.ReadLineAsync(ct)) is not null && line.Length>0){}
            var body=((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();var bytes=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");await stream.WriteAsync(bytes,ct);
        }
    }
}
