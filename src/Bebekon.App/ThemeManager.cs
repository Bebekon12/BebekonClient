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
            "Blue" => ("#66C7FF", "#478DEF", "#102E40", "#285577"),
            "Emerald" => ("#5CE5BF", "#30BFA4", "#10372F", "#286456"),
            "Violet" => ("#B4A1FF", "#7B83F6", "#272A44", "#4F527E"),
            _ => ("#32E0DB", "#3AA6F4", "#103438", "#236269")
        };
        var resources = Application.Current.Resources;
        resources["Accent"] = Solid(first); resources["AccentSoft"] = Solid(soft); resources["AccentBorder"] = Solid(border);
        resources["AccentGradient"] = Gradient(first, last, new(1, .7));
        resources["FeatureGradient"] = Gradient(soft, settings.PureBlack ? "#101C27" : "#0D222C", new(1, .5));
        resources["PowerGradient"] = Gradient(first, last, new(.85, .15));
        resources["Background"] = Solid(settings.PureBlack ? "#000000" : "#06151C");
        resources["Sidebar"] = Solid(settings.PureBlack ? "#090F14" : "#0E1D24");
        var halo = new RadialGradientBrush(); halo.GradientStops.Add(new(ColorOf(first), 0)); halo.GradientStops.Add(new(Color.FromArgb(0, ColorOf(first).R, ColorOf(first).G, ColorOf(first).B), 1)); halo.Freeze(); resources["Halo"] = halo;
        Motion.ResetAll(); Changed?.Invoke();
    }
}
