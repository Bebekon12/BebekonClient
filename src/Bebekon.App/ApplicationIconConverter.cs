using System.Globalization;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bebekon.App;
public sealed class ApplicationIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    public object? Convert(object value, Type target, object parameter, CultureInfo culture)
    {
        if (value is not string path || !Path.IsPathFullyQualified(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? result = null;
        try { using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path); if (icon is not null) { var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24,24)); bitmap.Freeze(); result = bitmap; } }
        catch (Exception e) when (e is IOException or ArgumentException or System.ComponentModel.Win32Exception) { }
        if (Cache.Count >= 128) Cache.Remove(Cache.Keys.First()); Cache[path] = result; return result;
    }
    public object ConvertBack(object value, Type target, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
