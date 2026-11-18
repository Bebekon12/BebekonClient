using System.Windows.Media;
using System.Windows.Data;

namespace Bebekon.App;

internal sealed record RuleServerChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record RouteChoice(RouteTarget Target, string Label)
{
    public static IReadOnlyList<RouteChoice> Options { get; } =
        [new(RouteTarget.Vpn, "Bebekon VPN"), new(RouteTarget.NetBird, "NetBird"), new(RouteTarget.Direct, "Direct"), new(RouteTarget.Block, "Block")];

    internal static string NameOf(RouteTarget target) => Options.First(option => option.Target == target).Label;
}

/// <summary>The same editor state drives new rules and edits; it never changes the profile before Save.</summary>
internal sealed class RuleEditor : StackPanel
{
    private readonly RoutingRule? original;
    internal RuleKind Kind { get; private set; }
    internal bool UseVpn => Direction == RouteTarget.Vpn;
    internal TextBox ValueField { get; } = new() { MinHeight = 46 };
    internal TextBox NameField { get; } = new() { MinHeight = 46 };
    internal ComboBox ServerField { get; } = new() { MinHeight = 46, DisplayMemberPath = "Name" };
    private readonly TextBlock explanation = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 38, Margin = new(0, 6, 0, 2), FontSize = 12 };
    private readonly Button picker = new() { MinHeight = 46, MinWidth = 88, Margin = new(8, 0, 0, 0) };
    private readonly Dictionary<RuleKind, RadioButton> typeButtons = [];
    private readonly ComboBox direction = new() { MinHeight = 44, DisplayMemberPath = "Label", SelectedValuePath = "Target", ItemsSource = RouteChoice.Options };
    private readonly ComboBox reserve = new() { MinHeight = 44, DisplayMemberPath = "Name" };
    private readonly ComboBox transport = new() { MinHeight = 42 };
    private readonly TextBox port = new() { MinHeight = 42, Tag = "1–65535" };
    private readonly CheckBox protection = new() { Margin = new(0, 12, 0, 0) };
    private readonly StackPanel scope = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox protectedApps = new() { MinHeight = 42, MaxHeight = 105, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CheckBox directFallback = new() { Margin = new(0, 8, 0, 0) };
    private readonly Dictionary<RouteTarget, CheckBox> allowed = [];
    private RouteTarget Direction => direction.SelectedValue is RouteTarget target ? target : RouteTarget.Vpn;

    public RuleEditor(RoutingRule? old, IEnumerable<Server> servers)
    {
        original = old; Kind = old?.Kind ?? RuleKind.Application;
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Children.Add(Label(I18n.T("Тип правила", "Rule type")));
        var types = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var (kind, label, icon) in new[] {
            (RuleKind.Application, I18n.T("Программа", "Application"), "Monitor"), (RuleKind.GeoSite, "GeoSite", "GeoSite"),
            (RuleKind.Site, I18n.T("По суффиксу", "Domain suffix"), "Site"), (RuleKind.Contains, I18n.T("Слово в домене", "Domain keyword"), "Search"),
            (RuleKind.GeoIp, "GeoIP", "GeoIp"), (RuleKind.Network, "IP-CIDR", "Network") })
        {
            var button = Choice(label, icon, "RuleType"); button.Tag = kind;
            button.Checked += (_, _) => { Kind = kind; UpdateKind(); };
            typeButtons.Add(kind, button); types.Children.Add(button);
        }
        Children.Add(types); Children.Add(explanation);
        Children.Add(Label(I18n.T("Значение", "Value")));
        var valueRow = new Grid(); valueRow.ColumnDefinitions.Add(new()); valueRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ValueField.Tag = "Telegram.exe"; valueRow.Children.Add(ValueField); Grid.SetColumn(picker, 1); valueRow.Children.Add(picker);
        picker.Click += async (_, _) =>
        {
            if (Kind == RuleKind.Application)
            {
                picker.IsEnabled = false;
                try { var app = await Dialogs.ApplicationAsync(); if (app is not null) ValueField.Text = app.Path; }
                finally { picker.IsEnabled = true; }
            }
            else if (Kind is RuleKind.GeoSite or RuleKind.GeoIp)
            {
                var value = Dialogs.GeoSet(Kind); if (value is not null) ValueField.Text = value;
            }
        };
        Children.Add(valueRow);
        Children.Add(Label(I18n.T("Название (необязательно)", "Name (optional)")));
        NameField.Tag = I18n.T("Заполнится автоматически", "Filled automatically"); Children.Add(NameField);
        Children.Add(Label(I18n.T("Маршрут", "Route")));
        Children.Add(direction);
        Children.Add(Label(I18n.T("Через сервер", "Through server")));
        var choices = new List<RuleServerChoice> { new(null, I18n.T("Авто — выбранный сервер", "Auto — selected server")) };
        choices.AddRange(servers.Where(s => s.Supported).Select(s => new RuleServerChoice(s.Id, s.DisplayName)));
        if (old?.ServerId is { } id && choices.All(c => c.Id != id)) choices.Add(new(id, I18n.T("Сервер недоступен — выберите другой", "Server unavailable — choose another")));
        ServerField.ItemsSource = choices; ServerField.SelectedItem = choices.First(c => c.Id == old?.ServerId); Children.Add(ServerField);
        var advanced = new StackPanel();
        var firstReserve = old?.FallbackServerIds?.FirstOrDefault();
        var reserves = choices.Where(choice => choice.Id is not null).Prepend(new(null, I18n.T("Без резерва", "No reserve"))).ToList();
        if (firstReserve is not null && reserves.All(choice => choice.Id != firstReserve))
            reserves.Add(new(firstReserve, I18n.T("Резервный сервер недоступен", "Reserve server unavailable")));
        reserve.ItemsSource = reserves;
        reserve.SelectedItem = reserves.First(choice => choice.Id == firstReserve);
        advanced.Children.Add(Label(I18n.T("Резервный сервер", "Reserve server")));
        advanced.Children.Add(reserve);
        directFallback.Content = I18n.T("Direct при потере NetBird", "Use Direct if NetBird is unavailable");
        directFallback.IsChecked = old?.UnavailableRoute == RouteTarget.Direct;
        advanced.Children.Add(directFallback);
        advanced.Children.Add(Label(I18n.T("Разрешённые направления", "Allowed connections")));
        var choicesRow = new WrapPanel();
        foreach (var choice in RouteChoice.Options.Where(option => option.Target != RouteTarget.Block))
        {
            var enabled = new CheckBox { Content = choice.Label, IsChecked = old?.Allows(choice.Target) ?? true, Margin = new(0, 0, 16, 0) };
            enabled.Checked += (_, _) => UpdateDirection();
            enabled.Unchecked += (_, _) => UpdateDirection();
            allowed.Add(choice.Target, enabled);
            choicesRow.Children.Add(enabled);
        }
        advanced.Children.Add(choicesRow);
        advanced.Children.Add(Label(I18n.T("Протокол и порт назначения", "Destination protocol and port")));
        transport.ItemsSource = new[] { I18n.T("Любой протокол", "Any protocol"), "TCP", "UDP" };
        transport.SelectedIndex = old?.Transport is { } protocol ? (int)protocol + 1 : 0;
        port.Text = old?.Port?.ToString() ?? "";
        var networkRow = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        networkRow.Children.Add(transport);
        networkRow.Children.Add(port);
        advanced.Children.Add(networkRow);
        Children.Add(new Expander { Header = I18n.T("Резерв и ограничения", "Fallback and restrictions"),
            Content = advanced, Margin = new(0, 10, 0, 0), Foreground = (Brush)Application.Current.Resources["Text"] });
        protection.Content = I18n.T("Системная защита WFP", "WFP system protection");
        protection.ToolTip = I18n.T("Windows защищает IP/CIDR или весь трафик указанных приложений.", "Windows protects an IP/CIDR or all traffic from the selected executables.");
        protection.IsChecked = old?.SystemProtection == true;
        protection.Checked += (_, _) => UpdateDirection();
        protection.Unchecked += (_, _) => UpdateDirection();
        Children.Add(protection);
        scope.Children.Add(Label(I18n.T("Приложения для этого ресурса", "Applications for this resource")));
        protectedApps.Text = string.Join(Environment.NewLine, old?.ProtectedExecutables ?? []);
        scope.Children.Add(protectedApps);
        var chooseProtectedApp = new Button { Content = I18n.T("Добавить .exe", "Add .exe"), Margin = new(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        chooseProtectedApp.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Multiselect = true };
            if (picker.ShowDialog(Window.GetWindow(this)) == true)
                protectedApps.Text = string.Join(Environment.NewLine, Paths().Concat(picker.FileNames).Distinct(StringComparer.OrdinalIgnoreCase));
        };
        scope.Children.Add(chooseProtectedApp);
        scope.Children.Add(new TextBlock
        {
            Text = I18n.T("Выбранное направление применяется ко всему трафику этих файлов.", "The selected route applies to all traffic from these executables."),
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Subtitle"], Margin = new(0, 6, 0, 0)
        });
        Children.Add(scope);
        direction.SelectionChanged += (_, _) => UpdateDirection();
        direction.SelectedValue = old?.Target ?? RouteTarget.Vpn;
        ValueField.Text = old is null ? "" : string.Join(", ", old.Values); NameField.Text = old?.Name ?? "";
        typeButtons[Kind].IsChecked = true; UpdateKind();
    }
    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 7) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return label;
    }
    private static RadioButton Choice(string text, string icon, string group)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new IconView { Icon = icon, Width = 17, Height = 17, Margin = new(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) }); row.Children.Add(label);
        return new() { GroupName = group, Content = row, Style = (Style)Application.Current.Resources["RuleChoice"], MinHeight = 44, Margin = new(0, 0, 8, 8) };
    }
    internal void SelectKind(RuleKind kind) => typeButtons[kind].IsChecked = true;
    internal void SelectDirection(RouteTarget route) => direction.SelectedValue = route;
    internal void SelectAction(bool vpn) => SelectDirection(vpn ? RouteTarget.Vpn : RouteTarget.Direct);
    private void UpdateKind()
    {
        var (hint, placeholder) = Kind switch {
            RuleKind.Application => (I18n.T("Трафик программы. Укажите имя .exe, полный путь или выберите из списка.", "Application traffic. Enter an .exe name, full path or select from the list."), "Telegram.exe"),
            RuleKind.GeoSite => (I18n.T("Все домены сервиса из встроенного набора. Выберите доступный набор из списка.", "Service domains from a bundled set. Choose an available set from the list."), "openai, telegram"),
            RuleKind.Site => (I18n.T("Сайт и все его поддомены. Несколько доменов — через запятую, без https://.", "Website and all subdomains. Separate domains with commas, without https://."), "figma.com"),
            RuleKind.Contains => (I18n.T("Домены, в которых встречается указанное слово. Несколько слов — через запятую.", "Domains containing this keyword. Separate multiple words with commas."), "claude"),
            RuleKind.GeoIp => (I18n.T("IP-подсети страны или Telegram из встроенного набора. Выберите набор из списка.", "Country or Telegram IP networks from a bundled set. Choose from the list."), "telegram, ru"),
            _ => (I18n.T("IP-адреса и подсети IPv4 / IPv6. Несколько значений — через запятую.", "IPv4 / IPv6 addresses and networks. Separate values with commas."), "192.168.1.0/24") };
        explanation.Text = hint; ValueField.Tag = placeholder;
        picker.Visibility = Kind is RuleKind.Application or RuleKind.GeoSite or RuleKind.GeoIp ? Visibility.Visible : Visibility.Collapsed;
        picker.Content = I18n.T("Список", "List");
        UpdateDirection();
    }
    private void UpdateDirection()
    {
        ServerField.IsEnabled = reserve.IsEnabled = UseVpn;
        var domainScope = RuleValidation.IsDomainRule(Kind) && Direction is RouteTarget.Vpn or RouteTarget.Block;
        protection.IsEnabled = protection.IsChecked == true || Direction != RouteTarget.Direct &&
            (Kind is RuleKind.Application or RuleKind.Network || domainScope);
        scope.Visibility = domainScope && protection.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        directFallback.IsEnabled = Direction == RouteTarget.NetBird && protection.IsChecked != true &&
            allowed.TryGetValue(RouteTarget.Direct, out var direct) && direct.IsChecked == true;
        if (!directFallback.IsEnabled) directFallback.IsChecked = false;
    }
    private string[] Paths() => protectedApps.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    internal RoutingRule Build()
    {
        var values = ValueField.Text.Split(Kind == RuleKind.Application ? new[] { ',', '\n', '\r' } : new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => Kind == RuleKind.Site ? RuleValidation.SiteHost(v, dropWww: true) : v).Where(v => v.Length > 0).Distinct().ToList();
        var name = NameField.Text.Trim();
        if (name.Length == 0 && values.Count > 0) name = Kind == RuleKind.Application ? Path.GetFileNameWithoutExtension(values[0]) : values[0];
        int? destinationPort = null;
        if (!string.IsNullOrWhiteSpace(port.Text))
        {
            if (!int.TryParse(port.Text, out var parsed)) throw new UserError(I18n.T("Введите номер порта.", "Enter a port number."));
            destinationPort = parsed;
        }
        var denied = allowed.Where(entry => entry.Value.IsChecked != true).Select(entry => entry.Key).ToArray();
        var reserveId = (reserve.SelectedItem as RuleServerChoice)?.Id;
        var result = new RoutingRule
        {
            Id = original?.Id ?? Guid.NewGuid().ToString("N"), CreatedAt = original is null ? DateTimeOffset.UtcNow : original.CreatedAt,
            Name = name, Kind = Kind, Values = values, Enabled = original?.Enabled ?? true, UseVpn = Direction != RouteTarget.Direct,
            Destination = Direction is RouteTarget.Direct or RouteTarget.Vpn ? null : Direction,
            ServerId = UseVpn ? (ServerField.SelectedItem as RuleServerChoice)?.Id : null,
            FallbackServerIds = UseVpn && reserveId is not null
                ? original?.FallbackServerIds is { Count: > 0 } previous && previous[0] == reserveId ? [.. previous] : [reserveId] : null,
            UnavailableRoute = directFallback.IsChecked == true ? RouteTarget.Direct : null,
            ForbiddenRoutes = denied.Length == 0 ? null : denied,
            SystemProtection = protection.IsChecked == true,
            ProtectedExecutables = RuleValidation.IsDomainRule(Kind) && Paths() is { Length: > 0 } paths ? [.. paths] : null,
            Transport = transport.SelectedIndex <= 0 ? null : (RuleTransport)(transport.SelectedIndex - 1),
            Port = destinationPort
        };
        RuleValidation.Validate(result); return result;
    }
}
