using System.Globalization;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Bebekon.App;

/// <summary>Small cached outline icons with identical proportions at every Windows scale.</summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(IconView), new FrameworkPropertyMetadata("Shield", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(IconView), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private static readonly Dictionary<string, Geometry> Shapes = BuildShapes();
    private static Dictionary<string, Geometry> BuildShapes()
    {
        var paths = new Dictionary<string, string>
        {
            ["Home"] = "M3,11 L12,3 L21,11 M5,9 V21 H10 V15 H14 V21 H19 V9",
            ["Shield"] = "M12,3 L20,6 V12 C20,17 16,20 12,22 C8,20 4,17 4,12 V6 Z M12,4 V20",
            ["Servers"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M3,12 H21 M12,3 C7,8 7,16 12,21 C17,16 17,8 12,3 M5,6.5 H19 M5,17.5 H19",
            ["Rules"] = "M5,4 V20 M12,4 V20 M19,4 V20 M2,8 H8 M9,15 H15 M16,10 H22",
            ["Subscription"] = "M4,5 H20 V9 C16,9 16,15 20,15 V19 H4 V15 C8,15 8,9 4,9 Z M12,5 V8 M12,11 V13 M12,16 V19",
            ["Settings"] = "M9,3 H15 L16,6 L19,6 L22,11 L20,13 L20,16 L15,20 L12,19 L9,20 L4,16 L4,13 L2,11 L5,6 L8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
            ["Search"] = "M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M14.5,14.5 L21,21",
            ["Power"] = "M12,3 V12 M6,6 A9,9 0 1 0 18,6",
            ["Grid"] = "M4,4 H9 V9 H4 Z M15,4 H20 V9 H15 Z M4,15 H9 V20 H4 Z M15,15 H20 V20 H15 Z",
            ["List"] = "M8,5 H21 M8,12 H21 M8,19 H21 M3,5 H4 M3,12 H4 M3,19 H4",
            ["Bolt"] = "M13,2 L4,14 H11 L10,22 L20,9 H13 Z",
            ["Refresh"] = "M20,8 A9,9 0 1 0 21,14 M20,3 V8 H15",
            ["Star"] = "M12,3 L15,9 L22,10 L17,15 L18,22 L12,18 L6,22 L7,15 L2,10 L9,9 Z",
            ["Plus"] = "M12,5 V19 M5,12 H19",
            ["Close"] = "M6,6 L18,18 M18,6 L6,18",
            ["Chevron"] = "M9,5 L16,12 L9,19",
            ["Down"] = "M5,9 L12,16 L19,9",
            ["Download"] = "M12,3 V15 M7,10 L12,15 L17,10 M4,16 V21 H20 V16",
            ["Upload"] = "M12,16 V3 M7,8 L12,3 L17,8 M4,16 V21 H20 V16",
            ["Edit"] = "M4,16 L16,4 L20,8 L8,20 L3,21 Z M13,7 L17,11",
            ["Trash"] = "M3,6 H21 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17",
            ["Application"] = "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M17.5,14 V21 M14,17.5 H21",
            ["Sparkles"] = "M10,3 L12,8 L17,10 L12,12 L10,17 L8,12 L3,10 L8,8 Z M19,15 L20,18 L23,19 L20,20 L19,23 L18,20 L15,19 L18,18",
            ["Network"] = "M7,12 A5,5 0 1 1 17,12 A5,5 0 1 1 7,12 M12,3 V7 M12,17 V21 M3,12 H7 M17,12 H21 M7,21 H17",
            ["Monitor"] = "M3,4 H21 V17 H3 Z M9,21 H15 M12,17 V21",
            ["Proxy"] = "M3,7 H15 L12,4 M15,7 L12,10 M21,17 H9 L12,14 M9,17 L12,20",
            ["Clock"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,6 V12 L16,15",
            ["Folder"] = "M3,6 H9 L11,9 H21 V20 H3 Z",
            ["Language"] = "M3,5 H15 M9,3 V5 M5,5 C6,11 10,14 14,16 M13,5 C12,11 8,15 3,17 M14,21 L18,11 L22,21 M15.5,17 H20.5",
            ["Palette"] = "M12,3 C4,3 1,10 4,16 C7,22 12,21 12,18 C12,16 14,15 17,15 C24,15 22,3 12,3 Z M7,9 H7.1 M12,6 H12.1 M17,9 H17.1",
            ["Behavior"] = "M8,8 A4,4 0 1 1 16,8 A4,4 0 1 1 8,8 M4,21 V19 C4,13 20,13 20,19 V21",
            ["Info"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,10 V17 M12,6 V6.5",
            ["Check"] = "M5,12 L10,17 L20,6",
            ["Minimize"] = "M5,12 H19",
            ["Maximize"] = "M5,5 H19 V19 H5 Z",
            ["Restore"] = "M8,8 V4 H20 V16 H16 M4,8 H16 V20 H4 Z",
            ["Grip"] = "M8,5 H8.1 M16,5 H16.1 M8,12 H8.1 M16,12 H16.1 M8,19 H8.1 M16,19 H16.1",
            ["Site"] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M3,12 H21 M12,3 C7,8 7,16 12,21 C17,16 17,8 12,3",
            ["Contains"] = "M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M14.5,14.5 L21,21",
        };
        paths["Tun"] = paths["Shield"]; paths["GeoSite"] = paths["Servers"]; paths["GeoIp"] = paths["Network"];
        var result = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, data) in paths) { var shape = Geometry.Parse(data); shape.Freeze(); result[key] = shape; }
        return result;
    }
    protected override void OnRender(DrawingContext dc)
    {
        var key = Icon == "Network" ? "Network" : Icon;
        if (!Shapes.TryGetValue(key ?? "", out var shape)) shape = Shapes["Shield"];
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, shape); dc.Pop(); dc.Pop();
    }
}

public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingRow), new PropertyMetadata("Settings"));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Category { get; set; } = "Network";
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is bool flag && !flag;
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => value is bool flag && !flag;
}
