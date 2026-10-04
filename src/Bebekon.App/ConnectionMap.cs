using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Bebekon.App;

/// <summary>Bundled country geometry, cached between frames; the only moving element is a light trail.</summary>
public sealed class ConnectionMap : FrameworkElement
{
    private sealed record Country(string Code, Point Center, Point[][] Rings);
    private static readonly Country[] Countries = Load();
    public static readonly DependencyProperty ServerNameProperty = DependencyProperty.Register(nameof(ServerName), typeof(string), typeof(ConnectionMap), new FrameworkPropertyMetadata("", Changed));
    public static readonly DependencyProperty OriginProperty = DependencyProperty.Register(nameof(Origin), typeof(GeoPoint), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, Changed));
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(nameof(Active), typeof(bool), typeof(ConnectionMap), new FrameworkPropertyMetadata(false, MotionChanged));
    public static readonly DependencyProperty AnimationEnabledProperty = DependencyProperty.Register(nameof(AnimationEnabled), typeof(bool), typeof(ConnectionMap), new FrameworkPropertyMetadata(true, MotionChanged));
    public static readonly DependencyProperty LatencyProperty = DependencyProperty.Register(nameof(Latency), typeof(string), typeof(ConnectionMap), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LatencyMsProperty = DependencyProperty.Register(nameof(LatencyMs), typeof(long?), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty FlightProperty = DependencyProperty.Register("Flight", typeof(double), typeof(ConnectionMap), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public string ServerName { get => (string)GetValue(ServerNameProperty); set => SetValue(ServerNameProperty, value); }
    public GeoPoint? Origin { get => (GeoPoint?)GetValue(OriginProperty); set => SetValue(OriginProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    public bool AnimationEnabled { get => (bool)GetValue(AnimationEnabledProperty); set => SetValue(AnimationEnabledProperty, value); }
    public string Latency { get => (string)GetValue(LatencyProperty); set => SetValue(LatencyProperty, value); }
    public long? LatencyMs { get => (long?)GetValue(LatencyMsProperty); set => SetValue(LatencyMsProperty, value); }
    private (string Code, StreamGeometry Shape)[]? projected;
    private Point? destination, departure;
    private Point bend;
    private StreamGeometry? beam;
    public ConnectionMap()
    {
        IsHitTestVisible = false; ClipToBounds = true;
        Loaded += (_, _) => UpdateMotion(); Unloaded += (_, _) => BeginAnimation(FlightProperty, null);
        IsVisibleChanged += (_, _) => UpdateMotion(); SizeChanged += (_, _) => { projected = null; InvalidateVisual(); };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) { var map = (ConnectionMap)d; map.projected = null; map.InvalidateVisual(); }
    private static void MotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { var map = (ConnectionMap)d; map.UpdateMotion(); map.InvalidateVisual(); }
    private void UpdateMotion()
    {
        BeginAnimation(FlightProperty, null);
        if (IsLoaded && IsVisible && Active && AnimationEnabled) BeginAnimation(FlightProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.8)) { RepeatBehavior = RepeatBehavior.Forever });
    }
    private static Country[] Load()
    {
        using var stream = typeof(ConnectionMap).Assembly.GetManifestResourceStream("Bebekon.World.json")!;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray().Select(o => {
            var code = o.GetProperty("code").GetString()!;
            if (code == "-99") code = o.GetProperty("name").GetString() switch { "France" => "FR", "Norway" => "NO", "Kosovo" => "XK", _ => "" };
            var coordinates = o.GetProperty("coordinates");
            var polygons = o.GetProperty("type").GetString() == "Polygon" ? new[] { coordinates } : coordinates.EnumerateArray().ToArray();
            var rings = polygons.SelectMany(p => p.EnumerateArray()).Select(r => r.EnumerateArray().Select(p => new Point(p[0].GetDouble(), p[1].GetDouble())).ToArray()).ToArray();
            return new Country(code, new(o.GetProperty("x").GetDouble(), o.GetProperty("y").GetDouble()), rings);
        }).ToArray();
    }
    private void Project()
    {
        var code = CountryInfo.Resolve(ServerName);
        var selected = string.IsNullOrEmpty(code) ? null : Countries.FirstOrDefault(c => c.Code == code);
        var lon = selected?.Center.X ?? 10;
        var lat = Math.Clamp((selected?.Center.Y ?? 35) - 22, -65, 65);
        var span = Origin is null || selected is null ? 245 : Math.Clamp(Math.Abs(MapLocation.LongitudeDelta(Origin.Longitude - lon)) * 2.5 + 60, 245, 460);
        var scale = Math.Min(ActualWidth * .72 / span, ActualHeight / 150);
        Point At(Point p) => new((p.X - lon) * scale + ActualWidth * .61, (lat - p.Y) * scale + ActualHeight * .66);
        projected = Countries.Select(c => {
            var shape = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var path = shape.Open()) foreach (var ring in c.Rings) {
                for (var i = 0; i < ring.Length; i++) { var point = At(ring[i]); if (i == 0 || Math.Abs(ring[i].X - ring[i - 1].X) > 180) path.BeginFigure(point, true, true); else path.LineTo(point, true, false); }
            }
            shape.Freeze(); return (c.Code, shape);
        }).ToArray();
        destination = selected is null ? null : At(selected.Center);
        departure = Origin is null ? null : At(new(lon + MapLocation.LongitudeDelta(Origin.Longitude - lon), Origin.Latitude));
        beam = null;
        if (destination is { } end && departure is { } start) {
            bend = new((start.X + end.X) / 2, Math.Min(start.Y, end.Y) - 36);
            beam = new StreamGeometry(); using (var p = beam.Open()) { p.BeginFigure(start, false, false); p.QuadraticBezierTo(bend, end, true, false); } beam.Freeze();
        }
    }
    private Point BeamPoint(double t) { var s = departure!.Value; var e = destination!.Value; var u = 1 - t; return new(s.X * u * u + 2 * bend.X * u * t + e.X * t * t, s.Y * u * u + 2 * bend.Y * u * t + e.Y * t * t); }
    private static SolidColorBrush Brush(byte alpha, byte r = 28, byte g = 165, byte b = 255) { var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b)); brush.Freeze(); return brush; }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        if (projected is null) Project();
        var code = CountryInfo.Resolve(ServerName);
        var land = Brush(170, 13, 58, 104); var outline = new Pen(Brush(160, 26, 81, 130), .65);
        foreach (var c in projected!) dc.DrawGeometry(!string.IsNullOrEmpty(code) && c.Code == code ? Brush(180, 18, 101, 202) : land, outline, c.Shape);
        if (destination is not { } end) return;
        if (Active && beam is not null && departure is { } start) {
            dc.DrawGeometry(null, new Pen(Brush(20), 13), beam); dc.DrawGeometry(null, new Pen(Brush(50), 6), beam); dc.DrawGeometry(null, new Pen(Brush(240, 43, 184, 255), 1.8), beam);
            dc.DrawEllipse(Brush(75), null, start, 10, 10); dc.DrawEllipse(Brush(255), new Pen(Brushes.White, 1), start, 3, 3);
            if (AnimationEnabled) { var t = (double)GetValue(FlightProperty); var point = BeamPoint(t); dc.DrawEllipse(Brush(50), null, point, 12, 12); dc.DrawEllipse(Brushes.White, null, point, 3, 3); }
        }
        dc.DrawEllipse(Brush(50), null, end, 19, 19); dc.DrawEllipse(Brush(240), new Pen(Brushes.White, 1.5), end, 7, 7);
        var name = CountryInfo.DisplayName(ServerName);
        var caption = name + (LatencyMs is null ? "" : " · " + Latency);
        var label = new FormattedText(caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = 180, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (LatencyMs is not null) label.SetForegroundBrush((Brush)Application.Current.Resources[LatencyDisplay.Quality(LatencyMs) switch { LatencyQuality.Good => "PingGood", LatencyQuality.Moderate => "PingModerate", _ => "PingPoor" }], name.Length, caption.Length - name.Length);
        var rect = new Rect(Math.Clamp(end.X + 14, 10, Math.Max(10, ActualWidth - label.Width - 25)), Math.Clamp(end.Y - 35, 9, Math.Max(9, ActualHeight - 40)), label.Width + 18, 27);
        dc.DrawRoundedRectangle(Brush(240, 7, 27, 51), new Pen(Brush(220), .8), rect, 10, 10); dc.DrawText(label, new(rect.X + 9, rect.Y + 5));
    }
}
