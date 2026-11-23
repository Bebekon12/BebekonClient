using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Bebekon.App;
internal static class NativeChrome
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    internal static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var rounded = 2; var dark = 1;
        // Unsupported attributes on Windows 10 return HRESULTs; the normal window remains usable.
        DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    /// <summary>
    /// A WindowStyle=None window is maximized to the whole monitor by default, under the taskbar,
    /// so the bottom of the app is cut off. Report the monitor's work area as the maximized bounds.
    /// </summary>
    internal static void KeepMaximizedInWorkArea(Window window)
    {
        if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is { } source) source.AddHook(Hook);
    }
    private const int WmGetMinMaxInfo = 0x0024;
    private static nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return 0;
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return 0;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.Work; var bounds = info.Monitor;
        limits.MaxPosition = new(work.Left - bounds.Left, work.Top - bounds.Top);
        limits.MaxSize = new(work.Right - work.Left, work.Bottom - work.Top);
        // An auto-hidden taskbar leaves the work area equal to the monitor; keep one pixel
        // free on its edge so moving the pointer there still reveals it.
        if (work.Equals(bounds) && AutoHiddenTaskbarEdge(bounds) is { } edge)
        {
            if (edge == 1) { limits.MaxPosition.Y += 1; limits.MaxSize.Y -= 1; }
            else if (edge == 3) limits.MaxSize.Y -= 1;
            else if (edge == 0) { limits.MaxPosition.X += 1; limits.MaxSize.X -= 1; }
            else if (edge == 2) limits.MaxSize.X -= 1;
        }
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return 0;
    }
    /// <summary>ABE_LEFT=0, ABE_TOP=1, ABE_RIGHT=2, ABE_BOTTOM=3 for an auto-hidden taskbar on this monitor; otherwise null.</summary>
    private static int? AutoHiddenTaskbarEdge(Rect monitor)
    {
        var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        if (((int)SHAppBarMessage(4 /* ABM_GETSTATE */, ref data) & 1 /* ABS_AUTOHIDE */) == 0) return null;
        if (SHAppBarMessage(5 /* ABM_GETTASKBARPOS */, ref data) == 0) return null;
        var bar = data.Bounds;
        var onMonitor = bar.Left < monitor.Right && bar.Right > monitor.Left && bar.Top < monitor.Bottom && bar.Bottom > monitor.Top;
        return onMonitor ? data.Edge : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct AppBarData { public int Size; public nint Window; public int CallbackMessage, Edge; public Rect Bounds; public nint Param; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, int flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shell32.dll")] private static extern nint SHAppBarMessage(int message, ref AppBarData data);
}
