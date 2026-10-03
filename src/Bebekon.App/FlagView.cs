using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bebekon.App;
public sealed class FlagView : FrameworkElement
{
    private static readonly Dictionary<string, BitmapSource?> Images = new(StringComparer.OrdinalIgnoreCase);
    public static readonly DependencyProperty CountryProperty = DependencyProperty.Register(nameof(Country), typeof(string), typeof(FlagView), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Country { get => (string)GetValue(CountryProperty); set => SetValue(CountryProperty, value); }
    internal static BitmapSource? GetImage(string code)
    {
        if (Images.TryGetValue(code, out var cached)) return cached;
        BitmapSource? bitmap = null;
        var path = Path.Combine(AppContext.BaseDirectory, "resources", "flags", code + ".png");
        if (File.Exists(path))
        {
            try { var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.UriSource = new Uri(path); source.EndInit(); source.Freeze(); bitmap = source; }
            catch (Exception e) when (e is IOException or NotSupportedException) { }
        }
        Images[code] = bitmap; return bitmap;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        var code = CountryInfo.Resolve(Country);
        dc.PushClip(new RectangleGeometry(new(0, 0, w, h), 5, 5));
        if (code is not null && GetImage(code) is { } image) dc.DrawImage(image, new(0, 0, w, h));
        else
        {
            dc.DrawRectangle((Brush)FindResource("AccentSoft"), null, new(0, 0, w, h));
            var pen = new Pen((Brush)FindResource("Accent"), 1.1);
            var center = new Point(w / 2, h / 2); var radius = h * .34;
            dc.DrawEllipse(null, pen, center, radius, radius);
            dc.DrawEllipse(null, pen, center, radius * .45, radius);
            dc.DrawLine(pen, new(center.X - radius, center.Y), new(center.X + radius, center.Y));
        }
        dc.Pop();
    }
}
