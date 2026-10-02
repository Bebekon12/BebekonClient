using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;

namespace Bebekon.Core;

public sealed record ServiceRequest(string Operation, ConnectSpec? Spec = null);
public sealed record ServiceStatus(ConnectionState State, string? Error = null, DateTimeOffset? ConnectedAt = null, int? CorePid = null);
public sealed record ServiceResponse(bool Ok, ServiceStatus Status, string? Message = null);
public static class PipeProtocol
{
    public const string ServiceName = "BebekonVPN";
    public const string PipeName = "BebekonVPN.v1";
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        if (bytes.Length > 4 * 1024 * 1024) throw new UserError("Слишком много правил для одного запроса.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct); var size = BitConverter.ToInt32(header);
        if (size is < 1 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid frame.");
        var data = new byte[size]; await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<T>(data, Json.Options) ?? throw new InvalidDataException("Empty frame.");
    }
}
public sealed class ServiceClient
{
    public async Task<ServiceResponse> SendAsync(ServiceRequest request, bool startService = false, CancellationToken ct = default)
    {
        if (startService) await Task.Run(() =>
        {
            try { using var service = new ServiceController(PipeProtocol.ServiceName); if (service.Status == ServiceControllerStatus.Stopped) service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)); }
            catch (Exception) { throw new UserError("Служба VPN недоступна. Установите Setup или запустите portable-install-service.ps1 один раз от администратора."); }
        }, ct);
        using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(startService ? 25 : 4));
        await pipe.ConnectAsync(timeout.Token);
        await PipeProtocol.WriteAsync(pipe, request, timeout.Token); return await PipeProtocol.ReadAsync<ServiceResponse>(pipe, timeout.Token);
    }
}
