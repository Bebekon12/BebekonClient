using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Bebekon.App;
public partial class HomeView : UserControl
{
    private MainViewModel? vm;
    public HomeView()
    {
        InitializeComponent(); Loaded += (_, _) => { vm = DataContext as MainViewModel; if (vm is not null) vm.PropertyChanged += OnState; UpdateGlow(); };
        Unloaded += (_, _) => { if (vm is not null) vm.PropertyChanged -= OnState; vm = null; PowerHalo.BeginAnimation(OpacityProperty, null); };
        IsVisibleChanged += (_, _) => UpdateGlow();
    }
    private void OnState(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Connected)) UpdateGlow(); }
    private void UpdateGlow()
    {
        PowerHalo.BeginAnimation(OpacityProperty, null);
        if (IsVisible && SystemParameters.ClientAreaAnimation && vm?.Connected == true) PowerHalo.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
    }
}
public partial class ServersView : UserControl { public ServersView() => InitializeComponent(); }
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
            if (visible) count++;
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
        dragStart = e.GetPosition(this);
        var original = e.OriginalSource as DependencyObject;
        while (original is not null) { if (original is System.Windows.Controls.Primitives.ButtonBase) { source = null; return; } if (original is ListBoxItem item) { source = item.DataContext as RoutingRule; return; } original = VisualTreeHelper.GetParent(original); }
        source = null;
    }
    private void MoveDrag(object sender, System.Windows.Input.MouseEventArgs e) { if (e.LeftButton != MouseButtonState.Pressed || source is null || (e.GetPosition(this) - dragStart).Length < 10) return; var rule = source; source = null; System.Windows.DragDrop.DoDragDrop(this, rule, DragDropEffects.Move); }
    private void DropRule(object sender, System.Windows.DragEventArgs e) { var element = e.OriginalSource as DependencyObject; while (element is not null && element is not ListBoxItem) element = VisualTreeHelper.GetParent(element); if (element is ListBoxItem { DataContext: RoutingRule target } && e.Data.GetData(typeof(RoutingRule)) is RoutingRule origin && DataContext is MainViewModel vm) vm.Reorder(origin, target); }
}
public sealed class SelectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type type, object parameter, CultureInfo culture) => values.Length == 2 && values[0] is Server a && values[1] is Server b && a.Id == b.Id;
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class FlagView : FrameworkElement
{
    public static readonly DependencyProperty CountryProperty = DependencyProperty.Register(nameof(Country), typeof(string), typeof(FlagView), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Country { get => (string)GetValue(CountryProperty); set => SetValue(CountryProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var text = (Country ?? "").ToLowerInvariant(); var w = ActualWidth; var h = ActualHeight;
        var flags = new Dictionary<string, string[]> { ["estonia"] = ["#0088CF", "#111111", "#FFFFFF"], ["poland"] = ["#FFFFFF", "#E61C47"], ["hungary"] = ["#D92C40", "#FFFFFF", "#45795C"], ["bulgaria"] = ["#FFFFFF", "#009D78", "#E43124"], ["austria"] = ["#DD1435", "#FFFFFF", "#DD1435"], ["russia"] = ["#FFFFFF", "#155CCD", "#DB302E"], ["germany"] = ["#111111", "#DA2431", "#F3CB38"], ["netherlands"] = ["#D93445", "#FFFFFF", "#285BA4"] };
        var vertical = new Dictionary<string, string[]> { ["france"] = ["#002974", "#FFFFFF", "#E12B39"], ["romania"] = ["#06378E", "#FAD72F", "#DF2033"], ["italy"] = ["#039865", "#FFFFFF", "#DF2033"], ["belgium"] = ["#111111", "#FAD72F", "#DF2033"] };
        dc.PushClip(new RectangleGeometry(new(0, 0, w, h), 5, 5));
        var key = flags.Keys.FirstOrDefault(text.Contains);
        if (key is not null) { var colors = flags[key]; for (var i = 0; i < colors.Length; i++) dc.DrawRectangle((Brush)new BrushConverter().ConvertFromString(colors[i])!, null, new(0, h * i / colors.Length, w, h / colors.Length + 0.5)); }
        else if ((key = vertical.Keys.FirstOrDefault(text.Contains)) is not null) { var colors = vertical[key]; for (var i = 0; i < colors.Length; i++) dc.DrawRectangle((Brush)new BrushConverter().ConvertFromString(colors[i])!, null, new(w * i / colors.Length, 0, w / colors.Length + 0.5, h)); }
        else { dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(31, 80, 98)), null, new(0, 0, w, h), 5, 5); dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(70, 219, 216)), 1.2), new(w / 2, h / 2), h * 0.32, h * 0.32); dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(70, 219, 216)), 1), new(w / 2 - h * 0.3, h / 2), new(w / 2 + h * 0.3, h / 2)); }
        dc.Pop();
    }
}
