using System.Windows.Media;
using System.Windows.Data;

namespace Bebekon.App;

internal sealed record RuleServerChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The same editor state drives new rules and edits; it never changes the profile before Save.</summary>
internal sealed class RuleEditor : StackPanel
{
    private readonly RoutingRule? original;
    internal RuleKind Kind { get; private set; }
    internal bool UseVpn { get; private set; }
    internal TextBox ValueField { get; } = new() { MinHeight = 46 };
    internal TextBox NameField { get; } = new() { MinHeight = 46 };
    internal ComboBox ServerField { get; } = new() { MinHeight = 46, DisplayMemberPath = "Name" };
    private readonly TextBlock explanation = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 38, Margin = new(0, 6, 0, 2), FontSize = 12 };
    private readonly Button picker = new() { MinHeight = 46, MinWidth = 88, Margin = new(8, 0, 0, 0) };
    private readonly Dictionary<RuleKind, RadioButton> typeButtons = [];
    private readonly RadioButton vpnButton;
    private readonly RadioButton directButton;

    public RuleEditor(RoutingRule? old, IEnumerable<Server> servers)
    {
        original = old; Kind = old?.Kind ?? RuleKind.Application; UseVpn = old?.UseVpn ?? true;
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
        Children.Add(Label(I18n.T("Действие", "Action")));
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        vpnButton = Choice(I18n.T("Через VPN", "Through VPN"), "Shield", "RuleAction");
        directButton = Choice(I18n.T("Без VPN", "Direct"), "Proxy", "RuleAction");
        vpnButton.Checked += (_, _) => { UseVpn = true; ServerField.IsEnabled = true; };
        directButton.Checked += (_, _) => { UseVpn = false; ServerField.IsEnabled = false; };
        actions.Children.Add(vpnButton); actions.Children.Add(directButton); Children.Add(actions);
        Children.Add(Label(I18n.T("Через сервер", "Through server")));
        var choices = new List<RuleServerChoice> { new(null, I18n.T("Авто — выбранный сервер", "Auto — selected server")) };
        choices.AddRange(servers.Where(s => s.Supported).Select(s => new RuleServerChoice(s.Id, s.DisplayName)));
        if (old?.ServerId is { } id && choices.All(c => c.Id != id)) choices.Add(new(id, I18n.T("Сервер недоступен — выберите другой", "Server unavailable — choose another")));
        ServerField.ItemsSource = choices; ServerField.SelectedItem = choices.First(c => c.Id == old?.ServerId); Children.Add(ServerField);
        ValueField.Text = old is null ? "" : string.Join(", ", old.Values); NameField.Text = old?.Name ?? "";
        typeButtons[Kind].IsChecked = true; (UseVpn ? vpnButton : directButton).IsChecked = true; UpdateKind();
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
    internal void SelectAction(bool vpn) => (vpn ? vpnButton : directButton).IsChecked = true;
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
    }
    internal RoutingRule Build()
    {
        var values = ValueField.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var name = NameField.Text.Trim();
        if (name.Length == 0 && values.Count > 0) name = Kind == RuleKind.Application ? Path.GetFileNameWithoutExtension(values[0]) : values[0];
        var result = new RoutingRule { Id = original?.Id ?? Guid.NewGuid().ToString("N"), Name = name, Kind = Kind, Values = values, UseVpn = UseVpn, ServerId = UseVpn ? (ServerField.SelectedItem as RuleServerChoice)?.Id : null };
        RuleValidation.Validate(result); return result;
    }
}
