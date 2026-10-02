using System.Globalization;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bebekon.App;
public sealed class ApplicationIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ImageSource Fallback = CreateFallback();
    private static ImageSource CreateFallback()
    {
        var geometry = Geometry.Parse("M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z");
        var drawing = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(139,170,182)), 1.5), geometry);
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    public object? Convert(object value, Type target, object parameter, CultureInfo culture)
    {
        if (value is not string path || !Path.IsPathFullyQualified(path)) return Fallback;
        if (Cache.TryGetValue(path, out var cached)) return cached ?? Fallback;
        ImageSource? result = null;
        try { using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path); if (icon is not null) { var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24,24)); bitmap.Freeze(); result = bitmap; } }
        catch (Exception e) when (e is IOException or ArgumentException or System.ComponentModel.Win32Exception) { }
        if (Cache.Count >= 128) Cache.Remove(Cache.Keys.First()); Cache[path] = result; return result ?? Fallback;
    }
    public object ConvertBack(object value, Type target, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
