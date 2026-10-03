using System.Windows.Media;

namespace Bebekon.App;

/// <summary>A small, timer-free sparkline driven by the measured core snapshots.</summary>
public sealed class TrafficChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(IReadOnlyList<double>), typeof(TrafficChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<double>? Samples { get => (IReadOnlyList<double>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        var samples = Samples;
        dc.PushOpacity(.16); dc.DrawLine(new Pen(Stroke, 1), new(0, ActualHeight - 2), new(ActualWidth, ActualHeight - 2)); dc.Pop();
        if (samples is null || samples.Count < 2) return;
        var highest = Math.Max(.1, samples.Where(double.IsFinite).DefaultIfEmpty(0).Max());
        Point At(int i) => new(i * ActualWidth / (samples.Count - 1), ActualHeight - 2 - Math.Clamp(double.IsFinite(samples[i]) ? samples[i] / highest : 0, 0, 1) * (ActualHeight - 6));
        var line = new StreamGeometry();
        using (var path = line.Open()) { path.BeginFigure(At(0), false, false); for (var i = 1; i < samples.Count; i++) path.LineTo(At(i), true, false); }
        line.Freeze();
        var area = new StreamGeometry();
        using (var path = area.Open()) { path.BeginFigure(new(0, ActualHeight), true, true); for (var i = 0; i < samples.Count; i++) path.LineTo(At(i), true, false); path.LineTo(new(ActualWidth, ActualHeight), true, false); }
        area.Freeze();
        dc.PushOpacity(.12); dc.DrawGeometry(Stroke, null, area); dc.Pop();
        dc.DrawGeometry(null, new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round }, line);
    }
}
