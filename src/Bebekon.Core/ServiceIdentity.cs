using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bebekon.Core;

internal static class ServiceIdentity
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    internal const uint OwnProcess = 0x0010;
    internal const uint Running = 4;

    internal static void Verify(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pipePid)) throw VerificationUnavailable();

        // An ordinary UI cannot OpenProcess on a LocalSystem helper. SCM owns the
        // registered service identity; its query-status right is granted to our owner.
        // Check before sending anything, including the subscription credentials.
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid) throw VerificationUnavailable();
        using var service = OpenService(manager, PipeProtocol.ServiceName, ServiceQueryStatus);
        if (service.IsInvalid || !QueryServiceStatusEx(service, 0, out var status,
                Marshal.SizeOf<ServiceProcessStatus>(), out _)) throw VerificationUnavailable();
        Validate(pipePid, status);
    }

    internal static void Validate(uint pipePid, ServiceProcessStatus status)
    {
        // A stopped/pending service has no trustworthy PID; a shared-process service
        // cannot identify this helper uniquely. Both must fail closed.
        if (status.ServiceType != OwnProcess || status.CurrentState != Running ||
            status.ProcessId == 0 || pipePid == 0 || pipePid != status.ProcessId)
            throw new UserError("Источник команд VPN не прошёл проверку. Перезапустите службу VPN в настройках.");
    }

    private static UserError VerificationUnavailable() =>
        new("Не удалось проверить службу VPN. Перезапустите службу в настройках или установите Bebekon VPN заново для своей учётной записи Windows.");

    // SERVICE_STATUS_PROCESS contains nine DWORD fields on both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceProcessStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level,
        out ServiceProcessStatus status, int size, out int needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
