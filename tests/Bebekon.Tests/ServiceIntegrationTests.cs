using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public class ServiceIntegrationTests
{
    private string testPipeName = "";
    [Fact]
    public async Task HelperValidatesStartsStopsAndDetectsCoreCrash()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
        var exe=Path.Combine(root,"src","Bebekon.Service","bin","Release","net10.0-windows","Bebekon.Service.exe");
        using var helper=new Process {StartInfo=new(exe,"--console"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        helper.Start();
        testPipeName = PipeProtocol.PipeName + ".test." + helper.Id;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var status=await Send(new("GetStatus"),timeout.Token);Assert.Equal(ConnectionState.Disconnected,status.Status.State);
            var node=VlessParser.Parse($"vless://{CoreTests.Id}@127.0.0.1:9?security=none&type=tcp#Fixture");
            var spec=new ConnectSpec(node,new(),new(){TunnelMode=TunnelMode.Proxy},LatencyService.FreePort(),new string('c',48));
            Assert.True((await Send(new("ValidateConfig",spec),timeout.Token)).Ok);
            var reply=await Send(new("StartCore",spec),timeout.Token);Assert.True(reply.Ok,reply.Message);Assert.Equal(ConnectionState.Connected,reply.Status.State);
            ServiceResponse sampled;
            do { await Task.Delay(100, timeout.Token); sampled = await Send(new("GetStatus"), timeout.Token); } while (sampled.Status.Traffic is null);
            Assert.Equal(0, sampled.Status.Traffic.UploadBytes); Assert.Equal(0, sampled.Status.Traffic.DownloadBytes);
            using(var core=Process.GetProcessById(reply.Status.CorePid!.Value)){core.Kill();await core.WaitForExitAsync(timeout.Token);}
            await Task.Delay(100,timeout.Token);
            Assert.Equal(ConnectionState.Error,(await Send(new("GetStatus"),timeout.Token)).Status.State);
            Assert.True((await Send(new("StopCore"),timeout.Token)).Ok);
            node.Transport="kcp";Assert.False((await Send(new("StartCore",spec),timeout.Token)).Ok);
            node.Transport="tcp";reply=await Send(new("StartCore",spec),timeout.Token);Assert.True(reply.Ok);
            var childPid=reply.Status.CorePid!.Value;
            helper.Kill();await helper.WaitForExitAsync(timeout.Token);await Task.Delay(200,timeout.Token);
            Assert.Throws<ArgumentException>(()=>Process.GetProcessById(childPid));
        }
        finally {if(!helper.HasExited){helper.Kill();await helper.WaitForExitAsync();}}
    }
    private async Task<ServiceResponse> Send(ServiceRequest request,CancellationToken ct)
    {
        using var pipe=new NamedPipeClientStream(".",testPipeName,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(ct);await PipeProtocol.WriteAsync(pipe,request,ct);return await PipeProtocol.ReadAsync<ServiceResponse>(pipe,ct);
    }
}
