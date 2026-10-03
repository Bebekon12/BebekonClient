using System.Windows.Data;
using System.Windows.Media;

namespace Bebekon.App;
public static class Dialogs
{
    public static bool? RoutingMode(bool current)
    {
        var window = Shell(I18n.T("Режим маршрутизации", "Routing mode"), out var body);
        bool? result = null;
        body.Children.Add(new TextBlock { Text = I18n.T("Выберите, какой трафик направлять через VPN.", "Choose which traffic goes through VPN."), Style = (Style)Application.Current.Resources["Subtitle"], Margin = new(0, 0, 0, 18) });
        foreach (var all in new[] { false, true })
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = I18n.T(all ? "Весь ПК" : "По правилам", all ? "Entire PC" : "By rules"), FontSize = 16, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = I18n.T(all ? "Все приложения и сайты через VPN" : "Выбранные сайты и приложения; остальное напрямую", all ? "All applications and websites use VPN" : "Selected sites and applications; everything else direct"), Style = (Style)Application.Current.Resources["Subtitle"], Margin = new(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
            var button = new Button { Content = content, Tag = all ? "EntirePC" : "ByRules", HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 82, Padding = new(18, 14, 18, 14), Margin = new(0, 0, 0, 12) };
            if (all == current) { button.BorderBrush = (Brush)Application.Current.Resources["Accent"]; button.Background = (Brush)Application.Current.Resources["AccentSoft"]; }
            button.Click += (_, _) => { result = all; window.DialogResult = true; }; body.Children.Add(button);
        }
        return window.ShowDialog() == true ? result : null;
    }
    internal static Action<Window>? RenderObserver { get; set; }
    internal static event Action? ModalStateChanged;
    private static int modalCount;
    internal static bool ModalOpen => modalCount > 0;
    private static Window Shell(string title, out StackPanel body)
    {
        body = new() { Margin = new Thickness(24, 8, 24, 24) };
        var window = new Window { Title = title, Owner = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive) ?? Application.Current.MainWindow, Width = 530, SizeToContent = SizeToContent.Height, MaxHeight = Math.Min(820, SystemParameters.WorkArea.Height - 32), MinHeight = 150, Background = (Brush)Application.Current.Resources["Background"], Foreground = (Brush)Application.Current.Resources["Text"], FontFamily = new("Segoe UI Variable Text"), WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None, UseLayoutRounding = true };
        System.Windows.Shell.WindowChrome.SetWindowChrome(window, new() { CaptionHeight = 58, ResizeBorderThickness = new(0), GlassFrameThickness = new(0) });
        var root = new Border { BorderBrush = (Brush)Application.Current.Resources["Border"], BorderThickness = new(1), CornerRadius = new(16), Background = window.Background };
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new(58) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.Child = grid;
        var header = new Grid { Margin = new(24, 0, 16, 0) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 40, 0) });
        var close = new Button { Style = (Style)Application.Current.Resources["CloseTitleButton"], Width = 36, Height = 36, Content = new IconView { Icon = "Close", Width = 22, Height = 22 }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = I18n.T("Закрыть", "Close") };
        System.Windows.Automation.AutomationProperties.SetName(close, I18n.T("Закрыть", "Close"));
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true); close.Click += (_, _) => window.Close(); header.Children.Add(close); grid.Children.Add(header);
        var scroll = new ScrollViewer { Content = body, MaxHeight = window.MaxHeight - 62, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll); window.Content = root;
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };
        window.Loaded += (_, _) => RenderObserver?.Invoke(window);
        System.Windows.Media.Effects.Effect? previous = null;
        var backdrop = window.Owner?.Content as UIElement;
        window.SourceInitialized += (_, _) => { NativeChrome.Apply(window); if (backdrop is not null) { previous = backdrop.Effect; backdrop.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 8, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance }; } modalCount++; ModalStateChanged?.Invoke(); };
        window.Closed += (_, _) => { if (backdrop is not null) backdrop.Effect = previous; modalCount--; ModalStateChanged?.Invoke(); };
        return window;
    }
    private static TextBox Field(StackPanel body, string label, string value)
    {
        body.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 6), Foreground = (Brush)Application.Current.Resources["Muted"] });
        var field = new TextBox { Text = value, MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center }; body.Children.Add(field); return field;
    }
    private static Button Submit(StackPanel body, string label, Action action)
    {
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new(0, 22, 0, 0) };
        var cancel = new Button { Content = I18n.T("Отмена", "Cancel"), MinWidth = 100, MinHeight = 44, Margin = new(0, 0, 5, 0) }; cancel.Click += (_, _) => Window.GetWindow(body)?.Close(); row.Children.Add(cancel);
        var b = new Button { Content = label, Style = (Style)Application.Current.Resources["AccentButton"], MinWidth = 130, MinHeight = 44, Margin = new(5, 0, 0, 0), IsDefault = true }; b.Click += (_, _) => action(); row.Children.Add(b); body.Children.Add(row); return b;
    }
    public static bool Confirm(string title, string detail)
    {
        var w = Shell(title, out var body); body.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0), Foreground = (Brush)Application.Current.Resources["Muted"] });
        Submit(body, I18n.T("Подтвердить", "Confirm"), () => w.DialogResult = true); return w.ShowDialog() == true;
    }
    public static string? Text(string title, string label, string initial)
    {
        var w = Shell(title, out var body); var field = Field(body, label, initial); Submit(body, I18n.T("Сохранить", "Save"), () => { if (field.Text.Trim().Length is > 0 and <= 160) w.DialogResult = true; }); return w.ShowDialog() == true ? field.Text.Trim() : null;
    }
    public static Subscription? Subscription(Subscription? old)
    {
        var w = Shell(I18n.T(old is null ? "Добавить подписку" : "Изменить подписку", old is null ? "Add subscription" : "Edit subscription"), out var body);
        var name = Field(body, I18n.T("Название (необязательно)", "Name (optional)"), old?.Name ?? "");
        name.Tag = I18n.T("Автоматически по ссылке", "Detected from the link"); var source = Field(body, I18n.T("URL / VLESS / текст подписки", "URL / VLESS / subscription text"), old?.Source ?? "");
        source.TextWrapping = TextWrapping.Wrap; source.MaxHeight = 120; source.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var paste = new Button { Content = I18n.T("Вставить из буфера", "Paste from clipboard"), Margin = new(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }; paste.Click += (_, _) => { if (System.Windows.Clipboard.ContainsText()) source.Text = System.Windows.Clipboard.GetText().Trim(); }; body.Children.Add(paste);
        body.Children.Add(new TextBlock { Text = I18n.T("Ссылка хранится в зашифрованном виде только на этом компьютере.", "The link is encrypted and stored only on this computer."), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new(0, 12, 0, 0) });
        Submit(body, I18n.T("Сохранить", "Save"), () => { if (source.Text.Trim().Length > 0) w.DialogResult = true; });
        return w.ShowDialog() == true ? new() { Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : SuggestSubscriptionName(source.Text.Trim()), Source = source.Text.Trim() } : null;
    }
    internal static string SuggestSubscriptionName(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? uri.IdnHost : source.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ? "VLESS" : I18n.T("Подписка", "Subscription");

    public static RoutingRule? Rule(RoutingRule? old, IEnumerable<Server>? servers = null)
    {
        var w = Shell(I18n.T(old is null ? "Новое правило" : "Изменить правило", old is null ? "New rule" : "Edit rule"), out var body);
        var editor = new RuleEditor(old, servers ?? []); body.Children.Add(editor);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 8, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); body.Children.Add(error);
        RoutingRule? result = null;
        Submit(body, I18n.T("Сохранить", "Save"), () => { try { result = editor.Build(); w.DialogResult = true; } catch (UserError e) { error.Text = e.Message; } });
        return w.ShowDialog() == true ? result : null;
    }
    internal static string? GeoSet(RuleKind kind)
    {
        var w = Shell(I18n.T("Встроенные наборы", "Bundled sets") + " · " + kind, out var body);
        var list = new ListBox { ItemsSource = GeoCatalog.Available(kind), Height = 330, SelectedIndex = 0, ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        body.Children.Add(list); Submit(body, I18n.T("Выбрать", "Select"), () => w.DialogResult = list.SelectedItem is not null);
        return w.ShowDialog() == true ? list.SelectedItem as string : null;
    }
    public static Preset? Preset(List<Preset> presets)
    {
        var w = Shell(I18n.T("Готовые правила", "Service presets"), out var body);
        var list = new ListBox { ItemsSource = presets, DisplayMemberPath = "Name", Height = 200, ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        body.Children.Add(list);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 14, 0, 8) }; summary.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(summary);
        var preview = new ListBox { Height = 210, IsHitTestVisible = false }; body.Children.Add(preview);
        var template = new FrameworkElementFactory(typeof(TextBlock)); template.SetBinding(TextBlock.TextProperty, new Binding("Description")); template.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); preview.ItemTemplate = new DataTemplate { VisualTree = template };
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is not Preset selected) return; summary.Text = selected.Summary; preview.ItemsSource = selected.CreateRules().Select(r => new { Description = (r.UseVpn ? "VPN · " : "Без VPN · ") + r.Name + " — " + r.Description }).ToArray(); };
        list.SelectedIndex = 0;
        Submit(body, I18n.T("Добавить набор", "Add preset"), () => w.DialogResult = list.SelectedItem is not null);
        return w.ShowDialog() == true ? (Preset?)list.SelectedItem : null;
    }
    public static async Task<ApplicationEntry?> ApplicationAsync()
    {
        var apps = await Task.Run(ApplicationDiscovery.Find);
        var w = Shell(I18n.T("Добавить приложение", "Add application"), out var body);
        var search = Field(body, I18n.T("Поиск приложения", "Search applications"), "");
        var group = new ComboBox { ItemsSource = I18n.English ? new[] { "Running applications", "Installed applications", "All" } : new[] { "Запущенные приложения", "Установленные приложения", "Все" }, SelectedIndex = 0, Margin = new(0, 10, 0, 8) }; body.Children.Add(group);
        var list = new ListBox { Height = 270, DisplayMemberPath = "Name", ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] }; VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); body.Children.Add(list);
        list.DisplayMemberPath = "";
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); row.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 8, 10, 8));
        var icon = new FrameworkElementFactory(typeof(System.Windows.Controls.Image)); icon.SetValue(FrameworkElement.WidthProperty, 24.0); icon.SetValue(FrameworkElement.HeightProperty, 24.0); icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0)); icon.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding("Path") { Converter = new ApplicationIconConverter() }); row.AppendChild(icon);
        var labels = new FrameworkElementFactory(typeof(StackPanel)); var appName = new FrameworkElementFactory(typeof(TextBlock)); appName.SetBinding(TextBlock.TextProperty, new Binding("Name")); appName.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); appName.SetValue(FrameworkElement.MaxWidthProperty, 330.0); labels.AppendChild(appName);
        var appPath = new FrameworkElementFactory(typeof(TextBlock)); appPath.SetBinding(TextBlock.TextProperty, new Binding("Path")); appPath.SetValue(TextBlock.FontSizeProperty, 11.0); appPath.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["Muted"]); appPath.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); appPath.SetValue(FrameworkElement.MaxWidthProperty, 330.0); labels.AppendChild(appPath); row.AppendChild(labels); list.ItemTemplate = new DataTemplate { VisualTree = row };
        void Filter() => list.ItemsSource = apps.Where(a => a.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) && (group.SelectedIndex == 2 || a.Group == (group.SelectedIndex == 0 ? "Запущенные" : "Установленные"))).ToArray();
        search.TextChanged += (_, _) => Filter(); group.SelectionChanged += (_, _) => Filter(); Filter();
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new(0, 10, 0, 0) }; list.SelectionChanged += (_, _) => detail.Text = (list.SelectedItem as ApplicationEntry)?.Path; body.Children.Add(detail);
        ApplicationEntry? result = null;
        var manual = new Button { Content = I18n.T("Выбрать .exe вручную", "Choose .exe manually"), Margin = new(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }; manual.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe" }; if (d.ShowDialog(w) == true) { result = new(Path.GetFileNameWithoutExtension(d.FileName), d.FileName, ""); w.DialogResult = true; } }; body.Children.Add(manual);
        Submit(body, I18n.T("Добавить", "Add"), () => { result = list.SelectedItem as ApplicationEntry; if (result is not null) w.DialogResult = true; }); return w.ShowDialog() == true ? result : null;
    }
}
