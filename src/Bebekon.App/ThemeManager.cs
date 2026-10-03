using System.Windows.Media;

namespace Bebekon.App;
public static class ThemeManager
{
    public static bool Animations { get; private set; } = true;
    public static bool Glow { get; private set; } = true;
    public static event Action? Changed;
    private static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush Solid(string hex) { var brush = new SolidColorBrush(ColorOf(hex)); brush.Freeze(); return brush; }
    private static LinearGradientBrush Gradient(string first, string last, Point? end = null)
    { var brush = new LinearGradientBrush(ColorOf(first), ColorOf(last), new(0, 0), end ?? new(1, 1)); brush.Freeze(); return brush; }
    public static void Apply(Settings settings)
    {
        Animations = settings.Animations; Glow = settings.GlowEffects;
        var (first, last, soft, border) = settings.AccentColor switch
        {
            "Blue" => ("#75ACFF", "#286BF5", "#132B4D", "#284C77"),
            "Emerald" => ("#5CE5BF", "#30BFA4", "#122C30", "#285D59"),
            "Violet" => ("#B4A1FF", "#7B83F6", "#232741", "#4F527E"),
            _ => ("#52DDE8", "#358DEE", "#122C3D", "#295775")
        };
        var resources = Application.Current.Resources;
        resources["Accent"] = Solid(first); resources["AccentSoft"] = Solid(soft); resources["AccentBorder"] = Solid(border);
        resources["AccentGradient"] = Gradient(settings.AccentColor == "Blue" ? "#2671E8" : first, last, new(1, .7));
        resources["OnAccent"] = Solid(settings.AccentColor == "Blue" ? "#FFFFFF" : "#081523");
        resources["FeatureGradient"] = Gradient(soft, settings.PureBlack ? "#101827" : "#101E34", new(1, .5));
        resources["PanelGradient"] = Gradient(settings.PureBlack ? "#101723" : "#111E31", settings.PureBlack ? "#0C111B" : "#0C1727", new(1, 1));
        resources["PowerGradient"] = Gradient(first, last, new(.85, .15));
        resources["Background"] = Solid(settings.PureBlack ? "#000000" : "#080F1C");
        resources["Sidebar"] = Solid(settings.PureBlack ? "#090E17" : "#0B1525");
        var halo = new RadialGradientBrush(); halo.GradientStops.Add(new(ColorOf(first), 0)); halo.GradientStops.Add(new(Color.FromArgb(0, ColorOf(first).R, ColorOf(first).G, ColorOf(first).B), 1)); halo.Freeze(); resources["Halo"] = halo;
        Motion.ResetAll(); Changed?.Invoke();
    }
}
