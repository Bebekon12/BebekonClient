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
}
