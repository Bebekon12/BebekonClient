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
    private void StopMotion() { PowerHalo.BeginAnimation(OpacityProperty, null); ConnectRingRotation.BeginAnimation(RotateTransform.AngleProperty, null); Orbit.BeginAnimation(RotateTransform.AngleProperty, null); SpeedShift.BeginAnimation(TranslateTransform.XProperty, null); }
    private void UpdateGlow()
    {
        StopMotion(); PowerHalo.Visibility = ThemeManager.Glow ? Visibility.Visible : Visibility.Hidden;
        HeroMap.AnimationEnabled = IsVisible && owner?.WindowState != WindowState.Minimized && !Dialogs.ModalOpen && vm?.UpdateOverlayOpen != true && Motion.Enabled;
        if (!IsVisible || owner?.WindowState == WindowState.Minimized || Dialogs.ModalOpen || vm?.UpdateOverlayOpen == true || !Motion.Enabled) return;
        if (ThemeManager.Glow && vm?.Connected == true) PowerHalo.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(.12, .24, TimeSpan.FromSeconds(2.8)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        if (vm?.Connected == true) SpeedShift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 12, TimeSpan.FromSeconds(2.8)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        if (vm?.Connected == true) ConnectRingRotation.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        if (vm?.ConnectionBusy == true) Orbit.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.7)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
    }
}
public sealed class LatencyBrushConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture)
    {
        var quality = value is LatencyQuality q ? q : LatencyDisplay.Quality(value is long ms ? ms : null);
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
    public bool Embedded { get => (bool)GetValue(EmbeddedProperty); set => SetValue(EmbeddedProperty, value); }
    public bool ToolbarOnly { get => (bool)GetValue(ToolbarOnlyProperty); set => SetValue(ToolbarOnlyProperty, value); }
    public ServersView()
    {
        InitializeComponent();
    }
}
public partial class SubscriptionsView : UserControl { public SubscriptionsView() => InitializeComponent(); }
public partial class ConnectionsView : UserControl { public ConnectionsView() => InitializeComponent(); }
public partial class ProtectionView : UserControl { public ProtectionView() => InitializeComponent(); }
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
    private RoutingRule[] SelectedRules() => RulesList.SelectedItems.OfType<RoutingRule>().ToArray();
    private void RulesMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = SelectedRules();
        MergeMenu.IsEnabled = vm.CanMergeIntoGroup(selected);
        MergeMenu.Header = I18n.T(selected.Length > 1 ? $"Объединить {selected.Length} правил в группу…" : "Сделать группу из правила…",
            selected.Length > 1 ? $"Combine {selected.Length} rules into a group…" : "Make a group from this rule…");
    }
    private void MergeIntoGroup(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try { vm.MergeIntoGroup(SelectedRules()); } catch (UserError error) { vm.Banner = error.Message; }
    }
    /// <summary>Dragging starts only from the ⋮⋮ handle, so clicks on the row, switch and buttons never start it.</summary>
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        source = null;
        if (DataContext is not MainViewModel { PriorityView: true }) return;
        dragStart = e.GetPosition(this);
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { Tag: "DragHandle" } handle) { source = handle.DataContext as RoutingRule; return; }
            if (element is ListBoxItem) return;
        }
    }
    private void MoveDrag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || source is null || (e.GetPosition(this) - dragStart).Length < 6) return;
        var rule = source; source = null;
        if (RulesList.ItemContainerGenerator.ContainerFromItem(rule) is ListBoxItem origin) origin.Tag = "source";
        try { System.Windows.DragDrop.DoDragDrop(RulesList, rule, DragDropEffects.Move); }
        finally { ClearDropMarks(); }
    }
    private (ListBoxItem Item, RoutingRule Target, bool After)? DropPoint(System.Windows.DragEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem) element = VisualTreeHelper.GetParent(element);
        if (element is not ListBoxItem { DataContext: RoutingRule target } item) return null;
        return (item, target, e.GetPosition(item).Y > item.ActualHeight / 2);
    }
    private void DragOverRule(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        AutoScroll(e);
        var point = DropPoint(e);
        if (point is not { } p || e.Data.GetData(typeof(RoutingRule)) is not RoutingRule origin || DataContext is not MainViewModel vm || !vm.CanDrop(origin, p.Target))
        { ClearDropMarks(keepSource: true); return; }
        e.Effects = DragDropEffects.Move;
        var mark = p.After ? "after" : "before";
        foreach (var item in Containers()) if (!ReferenceEquals(item, p.Item) && item.Tag is "before" or "after") item.Tag = null;
        if (!Equals(p.Item.Tag, mark) && !Equals(p.Item.Tag, "source")) p.Item.Tag = mark;
    }
    private void DragLeaveRule(object sender, System.Windows.DragEventArgs e)
    {
        var position = e.GetPosition(RulesList);
        if (position.X < 0 || position.Y < 0 || position.X > RulesList.ActualWidth || position.Y > RulesList.ActualHeight) ClearDropMarks(keepSource: true);
    }
    private void DropRule(object sender, System.Windows.DragEventArgs e)
    {
        var point = DropPoint(e);
        ClearDropMarks();
        if (point is { } p && e.Data.GetData(typeof(RoutingRule)) is RoutingRule origin && DataContext is MainViewModel vm) vm.Reorder(origin, p.Target, p.After);
    }
    /// <summary>Scrolls a long list while the pointer is held near its top or bottom edge.</summary>
    private void AutoScroll(System.Windows.DragEventArgs e)
    {
        if (FindScroll(RulesList) is not { } scroll || Environment.TickCount64 - lastScroll < 90) return;
        var y = e.GetPosition(RulesList).Y;
        if (y < 40) { scroll.LineUp(); lastScroll = Environment.TickCount64; }
        else if (y > RulesList.ActualHeight - 40) { scroll.LineDown(); lastScroll = Environment.TickCount64; }
    }
    private long lastScroll;
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
    private IEnumerable<ListBoxItem> Containers() => RulesList.Items.Cast<object>().Select(RulesList.ItemContainerGenerator.ContainerFromItem).OfType<ListBoxItem>();
    private void ClearDropMarks(bool keepSource = false) { foreach (var item in Containers()) if (!(keepSource && item.Tag is "source")) item.Tag = null; }
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
