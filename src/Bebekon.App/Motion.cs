using System.Runtime.CompilerServices;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Bebekon.App;
/// <summary>Short interaction animations. No idle timer, and reduced motion is respected.</summary>
public static class Motion
{
    private sealed class State { public ScaleTransform Scale { get; } = new(); }
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();
    private static readonly List<WeakReference<FrameworkElement>> Elements = [];
    public static bool Enabled => ThemeManager.Animations && SystemParameters.ClientAreaAnimation;
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached("Interactive", typeof(bool), typeof(Motion), new PropertyMetadata(false, InteractiveChanged));
    public static void SetInteractive(DependencyObject obj, bool value) => obj.SetValue(InteractiveProperty, value);
    public static bool GetInteractive(DependencyObject obj) => (bool)obj.GetValue(InteractiveProperty);
    public static readonly DependencyProperty SlidingSwitchProperty = DependencyProperty.RegisterAttached("SlidingSwitch", typeof(bool), typeof(Motion), new PropertyMetadata(false, SwitchChanged));
    public static void SetSlidingSwitch(DependencyObject obj, bool value) => obj.SetValue(SlidingSwitchProperty, value);
    public static bool GetSlidingSwitch(DependencyObject obj) => (bool)obj.GetValue(SlidingSwitchProperty);
    public static readonly DependencyProperty SwitchOffsetProperty = DependencyProperty.RegisterAttached("SwitchOffset", typeof(double), typeof(Motion), new PropertyMetadata(0d, OffsetChanged));
    public static void SetSwitchOffset(DependencyObject obj, double value) => obj.SetValue(SwitchOffsetProperty, value);
    public static double GetSwitchOffset(DependencyObject obj) => (double)obj.GetValue(SwitchOffsetProperty);
    private static void Track(FrameworkElement element)
    {
        if (Elements.Count > 500) Elements.RemoveAll(reference => !reference.TryGetTarget(out _));
        Elements.Add(new(element));
    }
    private static void InteractiveChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            var state = States.GetValue(element, _ => new()); element.RenderTransform = state.Scale; element.RenderTransformOrigin = new(.5, .5);
            Track(element); element.MouseEnter += Hover; element.MouseLeave += Hover;
            element.PreviewMouseLeftButtonDown += Press; element.PreviewMouseLeftButtonUp += Press;
            element.LostMouseCapture += Hover; element.Unloaded += Unload;
        }
        else
        {
            element.MouseEnter -= Hover; element.MouseLeave -= Hover; element.PreviewMouseLeftButtonDown -= Press; element.PreviewMouseLeftButtonUp -= Press;
            element.LostMouseCapture -= Hover; element.Unloaded -= Unload; Reset(element);
        }
    }
    private static void Hover(object sender, MouseEventArgs args) => Scale((FrameworkElement)sender, ((FrameworkElement)sender).IsMouseOver ? 1.012 : 1);
    private static void Press(object sender, MouseButtonEventArgs args) => Scale((FrameworkElement)sender, args.ButtonState == MouseButtonState.Pressed ? .975 : ((FrameworkElement)sender).IsMouseOver ? 1.012 : 1);
    private static void Scale(FrameworkElement element, double target)
    {
        if (!States.TryGetValue(element, out var state)) return;
        var from = state.Scale.ScaleX; state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        state.Scale.ScaleX = state.Scale.ScaleY = Enabled && element.IsEnabled ? target : 1;
        if (!Enabled || !element.IsEnabled) return;
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(from, target, 150)); state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(from, target, 150));
    }
    private static DoubleAnimation Tween(double from, double to, int duration) => new(from, to, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
    private static void SwitchChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not ToggleButton button) return;
        if ((bool)args.NewValue) { Track(button); button.Checked += Toggle; button.Unchecked += Toggle; button.Loaded += Toggle; button.Unloaded += Unload; }
        else { button.Checked -= Toggle; button.Unchecked -= Toggle; button.Loaded -= Toggle; button.Unloaded -= Unload; Reset(button); }
    }
    private static void Toggle(object sender, RoutedEventArgs args)
    {
        var button = (ToggleButton)sender; var from = GetSwitchOffset(button); var target = button.IsChecked == true ? 20d : 0d;
        if (args.RoutedEvent == FrameworkElement.LoadedEvent)
        {
            button.ApplyTemplate();
            MoveThumb(button, target);
        }
        button.BeginAnimation(SwitchOffsetProperty, null); SetSwitchOffset(button, target);
        if (button.IsLoaded && Enabled && args.RoutedEvent != FrameworkElement.LoadedEvent) button.BeginAnimation(SwitchOffsetProperty, Tween(from, target, 180));
    }
    private static void OffsetChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    { if (obj is ToggleButton button) MoveThumb(button, (double)args.NewValue); }
    private static void MoveThumb(ToggleButton button, double offset)
    {
        if (button.Template?.FindName("Thumb", button) is not FrameworkElement thumb) return;
        Canvas.SetLeft(thumb, 3 + offset);
    }
    private static void Unload(object sender, RoutedEventArgs args) => Reset((FrameworkElement)sender);
    private static void Reset(FrameworkElement element)
    {
        if (States.TryGetValue(element, out var state)) { state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); state.Scale.ScaleX = state.Scale.ScaleY = 1; }
        if (GetSlidingSwitch(element) && element is ToggleButton toggle) { toggle.BeginAnimation(SwitchOffsetProperty, null); SetSwitchOffset(toggle, toggle.IsChecked == true ? 20d : 0d); }
    }
    internal static void ResetAll() { foreach (var reference in Elements) if (reference.TryGetTarget(out var element)) Reset(element); }
    internal static void Reveal(FrameworkElement element, int delay = 0)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform(); element.RenderTransform = transform; transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = 0;
        if (!Enabled || element.Visibility != Visibility.Visible) return;
        var opacity = Tween(.25, 1, 230); opacity.BeginTime = TimeSpan.FromMilliseconds(delay);
        var offset = Tween(10, 0, 250); offset.BeginTime = opacity.BeginTime;
        element.BeginAnimation(UIElement.OpacityProperty, opacity); transform.BeginAnimation(TranslateTransform.YProperty, offset);
    }
}
