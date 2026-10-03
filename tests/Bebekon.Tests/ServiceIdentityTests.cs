using System.IO.Pipes;
using System.Runtime.InteropServices;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class ServiceIdentityTests
{
    [Fact]
    public void NativeStatusHasTheWindowsLayout() =>
        Assert.Equal(36, Marshal.SizeOf<ServiceIdentity.ServiceProcessStatus>());

    [Fact]
    public void RegisteredRunningOwnProcessIsAccepted() =>
        ServiceIdentity.Validate(123, Status(123));

    [Theory]
    [InlineData(0u, 123u)]
    [InlineData(123u, 0u)]
    [InlineData(124u, 123u)]
    public void ZeroOrCounterfeitPidIsRejected(uint pipePid, uint servicePid) =>
        Assert.Throws<UserError>(() => ServiceIdentity.Validate(pipePid, Status(servicePid)));

    [Theory]
    [InlineData(1u)] // Stopped
    [InlineData(2u)] // Start pending
    [InlineData(3u)] // Stop pending
    [InlineData(5u)] // Continue pending
    [InlineData(6u)] // Pause pending
    [InlineData(7u)] // Paused (not supported by this helper)
    public void NonRunningServiceCannotAuthenticateAPipe(uint state)
    {
        var status = Status(123); status.CurrentState = state;
        Assert.Throws<UserError>(() => ServiceIdentity.Validate(123, status));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)] // Driver
    [InlineData(0x20u)] // Shared process
    [InlineData(0x30u)]
    public void OnlyOwnProcessServicesAreAccepted(uint type)
    {
        var status = Status(123); status.ServiceType = type;
        Assert.Throws<UserError>(() => ServiceIdentity.Validate(123, status));
    }

    [Fact]
    public async Task RealPipeFromAnUnregisteredProcessIsRejected()
    {
        // No installed service is started or modified. This exercises the real pipe
        // PID and native SCM calls rather than giving the check a fabricated PID.
        var name = "Bebekon.Identity.Test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token); await connected;
        Assert.Throws<UserError>(() => ServiceIdentity.Verify(client));
    }

    private static ServiceIdentity.ServiceProcessStatus Status(uint pid) =>
        new() { ServiceType = ServiceIdentity.OwnProcess, CurrentState = ServiceIdentity.Running, ProcessId = pid };
}
