using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Bebekon.App;
public partial class HomeView : UserControl
{
    private MainViewModel? vm;
    private Window? owner;
    public HomeView()
    {
        InitializeComponent(); Loaded += (_, _) => { vm = DataContext as MainViewModel; if (vm is not null) vm.PropertyChanged += OnState; owner = Window.GetWindow(this); if (owner is not null) owner.StateChanged += OwnerStateChanged; ThemeManager.Changed += UpdateGlow; Dialogs.ModalStateChanged += UpdateGlow; UpdateGlow(); };
        Unloaded += (_, _) => { if (vm is not null) vm.PropertyChanged -= OnState; if (owner is not null) owner.StateChanged -= OwnerStateChanged; ThemeManager.Changed -= UpdateGlow; Dialogs.ModalStateChanged -= UpdateGlow; vm = null; owner = null; StopMotion(); };
        IsVisibleChanged += (_, _) => UpdateGlow();
    }
    private void OnState(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName is nameof(MainViewModel.Connected) or nameof(MainViewModel.ConnectionBusy) or nameof(MainViewModel.UpdateOverlayOpen)) UpdateGlow(); }
    private void OwnerStateChanged(object? sender, EventArgs e) => UpdateGlow();
    private void StopMotion() { PowerHalo.BeginAnimation(OpacityProperty, null); Orbit.BeginAnimation(RotateTransform.AngleProperty, null); SpeedShift.BeginAnimation(TranslateTransform.XProperty, null); }
    private void UpdateGlow()
    {
        StopMotion(); PowerHalo.Visibility = ThemeManager.Glow ? Visibility.Visible : Visibility.Hidden;
        if (!IsVisible || owner?.WindowState == WindowState.Minimized || Dialogs.ModalOpen || vm?.UpdateOverlayOpen == true || !Motion.Enabled) return;
        if (ThemeManager.Glow && vm?.Connected == true) PowerHalo.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(.12, .24, TimeSpan.FromSeconds(2.8)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        if (vm?.Connected == true) SpeedShift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 12, TimeSpan.FromSeconds(2.8)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        if (vm?.ConnectionBusy == true) Orbit.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.7)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
    }
}
public sealed class LatencyBrushConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture)
    {
        var quality = LatencyDisplay.Quality(value is long ms ? ms : null);
        return Application.Current.Resources[quality switch { LatencyQuality.Good => "PingGood", LatencyQuality.Moderate => "PingModerate", LatencyQuality.Poor => "PingPoor", _ => "Muted" }];
    }
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class ViewContext : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(object), typeof(ViewContext));
    public object Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    protected override Freezable CreateInstanceCore() => new ViewContext();
}
public partial class ServersView : UserControl
{
    public static readonly DependencyProperty EmbeddedProperty = DependencyProperty.Register(nameof(Embedded), typeof(bool), typeof(ServersView), new PropertyMetadata(false));
    public static readonly DependencyProperty ToolbarOnlyProperty = DependencyProperty.Register(nameof(ToolbarOnly), typeof(bool), typeof(ServersView), new PropertyMetadata(false));
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(ServersView), new PropertyMetadata(false));
    public bool Embedded { get => (bool)GetValue(EmbeddedProperty); set => SetValue(EmbeddedProperty, value); }
    public bool ToolbarOnly { get => (bool)GetValue(ToolbarOnlyProperty); set => SetValue(ToolbarOnlyProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); private set => SetValue(CompactProperty, value); }
    public ServersView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Compact = ActualWidth < 780;
    }
}
public partial class SubscriptionsView : UserControl { public SubscriptionsView() => InitializeComponent(); }
public partial class SettingsView : UserControl
{
    private string category = "Network";
    public SettingsView() { InitializeComponent(); Loaded += (_, _) => ApplyFilter(); }
    private void CategoryChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string selected }) return;
        category = selected;
        if (SettingsRows is null) return;
        SettingsSearch.Clear(); ApplyFilter(); SettingsScroll.ScrollToTop();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        if (SettingsRows is null || SettingsSearch is null) return;
        var query = SettingsSearch.Text.Trim(); var count = 0;
        foreach (var row in SettingsRows.Children.OfType<SettingRow>())
        {
            var visible = query.Length > 0
                ? (row.Title + " " + row.Description).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                : category == "All" || row.Category == category;
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) { Motion.Reveal(row, Math.Min(count * 25, 100)); count++; }
        }
        NoSettingsFound.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
public partial class RulesView : UserControl
{
    private System.Windows.Point dragStart;
    private RoutingRule? source;
    public RulesView() => InitializeComponent();
    private void RulesMode(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) vm.IsWholePc = false; }
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel { RuleSortIndex: 1 }) { source = null; return; }
        dragStart = e.GetPosition(this);
        var original = e.OriginalSource as DependencyObject;
        while (original is not null) { if (original is System.Windows.Controls.Primitives.ButtonBase) { source = null; return; } if (original is ListBoxItem item) { source = item.DataContext as RoutingRule; return; } original = VisualTreeHelper.GetParent(original); }
        source = null;
    }
    private void MoveDrag(object sender, System.Windows.Input.MouseEventArgs e) { if (e.LeftButton != MouseButtonState.Pressed || source is null || (e.GetPosition(this) - dragStart).Length < 10) return; var rule = source; source = null; System.Windows.DragDrop.DoDragDrop(this, rule, DragDropEffects.Move); }
    private void DropRule(object sender, System.Windows.DragEventArgs e) { var element = e.OriginalSource as DependencyObject; while (element is not null && element is not ListBoxItem) element = VisualTreeHelper.GetParent(element); if (element is ListBoxItem { DataContext: RoutingRule target } && e.Data.GetData(typeof(RoutingRule)) is RoutingRule origin && DataContext is MainViewModel { RuleSortIndex: 1 } vm) vm.Reorder(origin, target); }
}
public sealed class SelectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type type, object parameter, CultureInfo culture) => values.Length == 2 && values[0] is Server a && values[1] is Server b && a.Id == b.Id;
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class InverseBoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
