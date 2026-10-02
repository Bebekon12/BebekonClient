using System.Windows.Data;
using System.Windows.Media;

namespace Bebekon.App;
public static class Dialogs
{
    internal static Action<Window>? RenderObserver { get; set; }
    private static Window Shell(string title, out StackPanel body)
    {
        body = new() { Margin = new Thickness(24, 8, 24, 24) };
        var window = new Window { Title = title, Owner = Application.Current.MainWindow, Width = 530, SizeToContent = SizeToContent.Height, MaxHeight = 720, MinHeight = 150, Background = (Brush)Application.Current.Resources["Background"], Foreground = (Brush)Application.Current.Resources["Text"], FontFamily = new("Segoe UI Variable Text"), WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None, UseLayoutRounding = true };
        System.Windows.Shell.WindowChrome.SetWindowChrome(window, new() { CaptionHeight = 58, ResizeBorderThickness = new(0), GlassFrameThickness = new(0) });
        var root = new Border { BorderBrush = (Brush)Application.Current.Resources["Border"], BorderThickness = new(1), CornerRadius = new(16), Background = window.Background };
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new(58) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.Child = grid;
        var header = new Grid { Margin = new(24, 0, 16, 0) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 40, 0) });
        var close = new Button { Style = (Style)Application.Current.Resources["CloseTitleButton"], Width = 36, Content = new IconView { Icon = "Close", Width = 22, Height = 22 }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = I18n.T("Закрыть", "Close") };
        System.Windows.Automation.AutomationProperties.SetName(close, I18n.T("Закрыть", "Close"));
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true); close.Click += (_, _) => window.Close(); header.Children.Add(close); grid.Children.Add(header);
        var scroll = new ScrollViewer { Content = body, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll); window.Content = root;
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };
        window.Loaded += (_, _) => RenderObserver?.Invoke(window);
        return window;
    }
    private static TextBox Field(StackPanel body, string label, string value)
    {
        body.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 6), Foreground = (Brush)Application.Current.Resources["Muted"] });
        var field = new TextBox { Text = value, MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center }; body.Children.Add(field); return field;
    }
    private static Button Submit(StackPanel body, string label, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 22, 0, 0) };
        var cancel = new Button { Content = I18n.T("Отмена", "Cancel"), MinWidth = 100, Margin = new(0, 0, 8, 0) }; cancel.Click += (_, _) => Window.GetWindow(body)?.Close(); row.Children.Add(cancel);
        var b = new Button { Content = label, Style = (Style)Application.Current.Resources["AccentButton"], MinWidth = 130, IsDefault = true }; b.Click += (_, _) => action(); row.Children.Add(b); body.Children.Add(row); return b;
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
        var name = Field(body, I18n.T("Название", "Name"), old?.Name ?? "Ultima"); var source = Field(body, "URL / VLESS", old?.Source ?? "");
        source.TextWrapping = TextWrapping.Wrap; source.MaxHeight = 120; source.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var paste = new Button { Content = I18n.T("Вставить из буфера", "Paste from clipboard"), Margin = new(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }; paste.Click += (_, _) => { if (System.Windows.Clipboard.ContainsText()) source.Text = System.Windows.Clipboard.GetText().Trim(); }; body.Children.Add(paste);
        body.Children.Add(new TextBlock { Text = I18n.T("Ссылка хранится в зашифрованном виде только на этом компьютере.", "The link is encrypted and stored only on this computer."), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new(0, 12, 0, 0) });
        Submit(body, I18n.T("Сохранить", "Save"), () => { if (name.Text.Trim().Length > 0 && source.Text.Trim().Length > 0) w.DialogResult = true; });
        return w.ShowDialog() == true ? new() { Name = name.Text.Trim(), Source = source.Text.Trim() } : null;
    }
    public static RoutingRule? Rule(RoutingRule? old)
    {
        var w = Shell(I18n.T("Правило маршрутизации", "Routing rule"), out var body);
        var name = Field(body, I18n.T("Название", "Name"), old?.Name ?? "");
        body.Children.Add(new TextBlock { Text = I18n.T("Тип", "Type"), Margin = new(0, 14, 0, 6) });
        var kind = new ComboBox { ItemsSource = I18n.English ? new[] { "Website", "Address contains", "Application", "IP / subnet" } : new[] { "Сайт", "Адрес содержит", "Приложение", "IP / подсеть" }, SelectedIndex = (int)(old?.Kind ?? RuleKind.Site), MinHeight = 40 }; body.Children.Add(kind);
        var values = Field(body, I18n.T("Домен, .exe или IP · несколько значений через запятую", "Domain, .exe or IP · separate multiple values with commas"), old is null ? "" : string.Join(", ", old.Values));
        var picker = new Button { Content = I18n.T("Выбрать .exe", "Choose .exe"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 10, 0, 0) }; picker.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe" }; if (d.ShowDialog(w) == true) { values.Text = d.FileName; kind.SelectedIndex = 2; if (name.Text.Length == 0) name.Text = Path.GetFileNameWithoutExtension(d.FileName); } }; body.Children.Add(picker);
        var route = new CheckBox { Style = (Style)Application.Current.Resources["Switch"], IsChecked = old?.UseVpn ?? true };
        body.Children.Add(new SettingRow { Title = I18n.T("Через VPN", "Through VPN"), Description = I18n.T("Выключите, чтобы направлять трафик напрямую.","Turn off to send this traffic directly."), Icon = "Shield", Content = route, Margin = new(0,16,0,0) });
        body.Children.Add(new TextBlock { Text = I18n.T("Сайт включает все поддомены. Выключенный переключатель означает «Без VPN».", "A website includes all subdomains. An off switch means direct."), Foreground = (Brush)Application.Current.Resources["Muted"], TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        RoutingRule? result = null; var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["Danger"], TextWrapping = TextWrapping.Wrap, FontSize = 12 }; body.Children.Add(error);
        Submit(body, I18n.T("Сохранить", "Save"), () => { try { result = new() { Id = old?.Id ?? Guid.NewGuid().ToString("N"), Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : values.Text.Trim(), Kind = (RuleKind)kind.SelectedIndex, Values = values.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), UseVpn = route.IsChecked == true }; RuleValidation.Validate(result); w.DialogResult = true; } catch (UserError e) { error.Text = e.Message; } });
        return w.ShowDialog() == true ? result : null;
    }
    public static Preset? Preset(List<Preset> presets)
    {
        var w = Shell(I18n.T("Готовые правила", "Service presets"), out var body);
        var list = new ListBox { ItemsSource = presets, DisplayMemberPath = "Name", Height = 350, SelectedIndex = 0, ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] }; body.Children.Add(list); Submit(body, I18n.T("Добавить набор", "Add preset"), () => w.DialogResult = list.SelectedItem is not null); return w.ShowDialog() == true ? (Preset?)list.SelectedItem : null;
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
