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
        name.Tag = I18n.T("Автоматически по ссылке", "Detected from the link"); var source = Field(body, I18n.T("URL / VPN-ссылка / текст подписки", "URL / VPN link / subscription text"), old?.Source ?? "");
        source.TextWrapping = TextWrapping.Wrap; source.MaxHeight = 120; source.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var tools = new WrapPanel { Margin = new(0, 10, 0, 0) };
        var paste = new Button { Content = I18n.T("Вставить из буфера", "Paste from clipboard"), Margin = new(0, 0, 8, 0) }; paste.Click += (_, _) => { if (System.Windows.Clipboard.ContainsText()) source.Text = System.Windows.Clipboard.GetText().Trim(); }; tools.Children.Add(paste);
        var import = new Button { Content = I18n.T("Открыть файл", "Open file"), Tag = "ImportSubscriptionFile" }; tools.Children.Add(import); body.Children.Add(tools);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); body.Children.Add(error);
        import.Click += (_, _) =>
        {
            var file = new Microsoft.Win32.OpenFileDialog { Filter = "VPN configs (*.toml;*.json;*.yaml;*.yml;*.txt)|*.toml;*.json;*.yaml;*.yml;*.txt|All files|*.*" };
            if (file.ShowDialog(w) != true) return;
            try { if (new FileInfo(file.FileName).Length > 4 * 1024 * 1024) throw new UserError(I18n.T("Файл слишком большой (максимум 4 МБ).", "File is too large (maximum 4 MB).")); source.Text = File.ReadAllText(file.FileName).Trim(); error.Text = ""; }
            catch (UserError e) { error.Text = e.Message; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error.Text = I18n.T("Не удалось прочитать файл.", "Could not read this file."); }
        };
        body.Children.Add(new TextBlock { Text = I18n.T("Ссылка хранится в зашифрованном виде только на этом компьютере.", "The link is encrypted and stored only on this computer."), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new(0, 12, 0, 0) });
        Submit(body, I18n.T("Сохранить", "Save"), () => { if (source.Text.Trim().Length > 0) w.DialogResult = true; });
        return w.ShowDialog() == true ? new() { Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : SuggestSubscriptionName(source.Text.Trim()), Source = source.Text.Trim() } : null;
    }
    internal static string SuggestSubscriptionName(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? uri.IdnHost : source.StartsWith("tt://", StringComparison.OrdinalIgnoreCase) ? "TrustTunnel" : ProtocolParser.IsLink(source) ? source.Split(':')[0].ToUpperInvariant() : I18n.T("Подписка", "Subscription");

    public static Subscription? ManualTrustTunnel()
    {
        var window = Shell("TrustTunnel · " + I18n.T("Добавить сервер", "Add server"), out var body);
        var name = Field(body, I18n.T("Название сервера", "Server name"), "");
        var address = Field(body, I18n.T("Адрес сервера (порт по умолчанию 443)", "Server address (default port 443)"), "");
        var hostname = Field(body, I18n.T("Домен сертификата", "Certificate hostname"), "");
        var sni = Field(body, "Custom SNI · " + I18n.T("необязательно", "optional"), "");
        var username = Field(body, I18n.T("Логин", "Username"), "");
        body.Children.Add(new TextBlock { Text = I18n.T("Пароль", "Password"), Margin = new(0, 10, 0, 6), Foreground = (Brush)Application.Current.Resources["Muted"] });
        var password = new PasswordBox { MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 4096 }; body.Children.Add(password);
        body.Children.Add(new TextBlock { Text = I18n.T("Протокол", "Protocol"), Margin = new(0, 10, 0, 6) });
        var protocol = new ComboBox { ItemsSource = new[] { "HTTP/2", "HTTP/3", "Auto" }, SelectedIndex = 0, MinHeight = 42 }; body.Children.Add(protocol);
        body.Children.Add(new TextBlock { Text = I18n.T("Сертификат проверяется обязательно. Ваши правила маршрутизации сохраняются.", "Certificate verification is required. Your routing rules are preserved."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0), Foreground = (Brush)Application.Current.Resources["Muted"] });
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["Warning"], TextWrapping = TextWrapping.Wrap }; body.Children.Add(error);
        Subscription? result = null;
        Submit(body, I18n.T("Добавить", "Add"), () => {
            try {
                var input = address.Text.Trim();
                if (!Uri.TryCreate("tcp://" + input, UriKind.Absolute, out var endpoint) || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.AbsolutePath is not ("" or "/")) throw new UserError(I18n.T("Проверьте адрес сервера.", "Check server address."));
                var port = endpoint.Port < 0 ? 443 : endpoint.Port;
                var normalized = (endpoint.Host.Contains(':') ? "[" + endpoint.IdnHost.Trim('[', ']') + "]" : endpoint.IdnHost) + ":" + port;
                var source = System.Text.Json.JsonSerializer.Serialize(new { name = name.Text.Trim().Length > 0 ? name.Text.Trim() : endpoint.IdnHost, addresses = new[] { normalized }, hostname = hostname.Text.Trim(), custom_sni = sni.Text.Trim(), username = username.Text.Trim(), password = password.Password, upstream_protocol = protocol.SelectedIndex switch { 1 => "http3", 2 => "auto", _ => "http2" } });
                var node = VlessParser.ParseSubscription(source).Single();
                result = new() { Name = node.Name, Source = source }; window.DialogResult = true;
            } catch (UserError e) { error.Text = e.Message; }
        });
        try { return window.ShowDialog() == true ? result : null; } finally { password.Clear(); }
    }

    public static string? TrustTunnelTransport(string current)
    {
        var w = Shell("TrustTunnel · " + I18n.T("Протокол соединения", "Connection protocol"), out var body);
        body.Children.Add(new TextBlock { Text = I18n.T("Параметры TLS и правила маршрутизации сохраняются. Изменение применится к этому серверу.", "TLS settings and routing rules are preserved. This change applies to this server."), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Subtitle"], Margin = new(0, 0, 0, 16) });
        string? selected = null;
        foreach (var (value, title, detail) in new[] {
            ("auto", I18n.T("Автоматически", "Automatic"), I18n.T("HTTP/3 с переходом на HTTP/2 при недоступности UDP", "HTTP/3 with HTTP/2 fallback when UDP is unavailable")),
            ("http2", "HTTP/2 · TCP", I18n.T("Для сетей, в которых UDP ограничен", "For networks that restrict UDP")),
            ("http3", "HTTP/3 · QUIC", I18n.T("Соединение по UDP; сервер должен поддерживать HTTP/3", "UDP connection; the server must support HTTP/3")) })
        {
            var labels = new StackPanel(); labels.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
            labels.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Subtitle"], Margin = new(0, 5, 0, 0) });
            var button = new Button { Content = labels, Tag = value, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(18, 14, 18, 14), MinHeight = 80, Margin = new(0, 0, 0, 10) };
            if (value == current) { button.BorderBrush = (Brush)Application.Current.Resources["Accent"]; button.Background = (Brush)Application.Current.Resources["AccentSoft"]; }
            button.Click += (_, _) => { selected = value; w.DialogResult = true; }; body.Children.Add(button);
        }
        return w.ShowDialog() == true ? selected : null;
    }

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
