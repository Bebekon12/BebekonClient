using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bebekon.App;

/// <summary>Where the dialogs read and store the user's own domain groups.</summary>
internal interface IDomainSetLibrary
{
    IReadOnlyList<DomainSet> DomainSetList { get; }
    IReadOnlyList<RouteChoice> RouteChoices { get; }
    void SaveDomainSet(DomainSet? old, DomainSet set);
    void DeleteDomainSet(DomainSet set);
    int RulesUsingCount(DomainSet set);
    void AddGroupRule(DomainSet set, RouteTarget target);
    string DescribeDomain(string text);
    IReadOnlyList<string> SuggestRelated(IReadOnlyCollection<string> domains, IEnumerable<string>? processes = null);
    Task<DomainSet> SubscribeDomainSetAsync(string name, string url);
    Task RefreshDomainSetAsync(DomainSet set);
    IReadOnlyList<RoutingRule> CurrentRules { get; }
}

public static partial class Dialogs
{
    internal static IDomainSetLibrary? Library { get; set; }
    private sealed record GeoChoice(string Value, string Label, DomainSet? Set);

    internal static readonly Dictionary<string, Color> GroupColors = new()
    {
        ["Blue"] = Color.FromRgb(0x4C, 0x8D, 0xFF), ["Cyan"] = Color.FromRgb(0x2E, 0xC5, 0xD8), ["Emerald"] = Color.FromRgb(0x34, 0xC7, 0x7B),
        ["Amber"] = Color.FromRgb(0xFF, 0xB5, 0x47), ["Rose"] = Color.FromRgb(0xFF, 0x6B, 0x8A), ["Violet"] = Color.FromRgb(0xA7, 0x8B, 0xFA)
    };
    internal static Brush GroupBrush(DomainSet set) => new SolidColorBrush(GroupColors.GetValueOrDefault(set.Color, GroupColors["Blue"]));

    /// <summary>A round colored badge with the group's icon.</summary>
    private static Border GroupBadge(DomainSet set, double size = 30)
    {
        var brush = GroupBrush(set);
        var tint = ((SolidColorBrush)brush).Color;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new(size / 3), Background = new SolidColorBrush(Color.FromArgb(0x33, tint.R, tint.G, tint.B)),
            Child = new IconView { Icon = set.Icon, Width = size * 0.55, Height = size * 0.55, Foreground = brush }, VerticalAlignment = VerticalAlignment.Center
        };
    }
    private static FrameworkElement GroupRowContent(DomainSet set, string detail)
    {
        var row = new Grid { Margin = new(2) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new());
        row.Children.Add(GroupBadge(set));
        var text = new StackPanel { Margin = new(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = set.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = new TextBlock { Text = detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis }; sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(sub); Grid.SetColumn(text, 1); row.Children.Add(text);
        return row;
    }
    /// <summary>Button content: icon followed by text.</summary>
    private static StackPanel IconText(string icon, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new IconView { Icon = icon, Width = 15, Height = 15, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    /// <summary>Picker for a GeoSite rule: the user's ★ groups first, then bundled sets.</summary>
    internal static string? GeoSet(RuleKind kind)
    {
        var custom = kind == RuleKind.GeoSite && Library is not null;
        var w = Shell(custom ? I18n.T("Группы и наборы · GeoSite", "Groups and sets · GeoSite") : I18n.T("Встроенные наборы", "Bundled sets") + " · " + kind, out var body);
        var list = new ListBox { Height = custom ? 300 : 330, ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        GeoChoice? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as GeoChoice;
        void Reload(string? select = null)
        {
            list.Items.Clear();
            if (custom)
                foreach (var set in Library!.DomainSetList.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                    list.Items.Add(new ListBoxItem { Content = GroupRowContent(set, Count(set.Domains.Count) + " · " + DomainSets.Reference(set)), Tag = new GeoChoice(DomainSets.Reference(set), set.Name, set) });
            foreach (var value in GeoCatalog.Available(kind))
            {
                var label = IconText(kind == RuleKind.GeoSite ? "GeoSite" : "GeoIp", value); label.HorizontalAlignment = HorizontalAlignment.Left;
                list.Items.Add(new ListBoxItem { Content = label, Tag = new GeoChoice(value, value, null) });
            }
            list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((GeoChoice)i.Tag).Value.Equals(select, StringComparison.OrdinalIgnoreCase)) ?? list.Items.OfType<ListBoxItem>().FirstOrDefault();
        }
        Reload();
        if (custom)
        {
            body.Children.Add(MutedText(I18n.T("Сверху — ваши группы доменов, ниже — встроенные наборы. Группы создаются и отправляются другим в «Мои группы».",
                "Your domain groups are on top, bundled sets below. Create and share groups in My groups.")));
            body.Children.Add(list);
            var manage = new Button { Content = IconText("Folder", I18n.T("Мои группы…", "My groups…")), MinHeight = 40, Margin = new(0, 10, 0, 0) };
            manage.Click += (_, _) => { var value = Selected()?.Value; DomainGroups(Selected()?.Set); Reload(value); };
            body.Children.Add(manage);
        }
        else body.Children.Add(list);
        Submit(body, I18n.T("Выбрать", "Select"), () => w.DialogResult = list.SelectedItem is not null);
        return w.ShowDialog() == true ? Selected()?.Value : null;
    }

    /// <summary>
    /// Manager for the user's domain groups: list on the left, the opened group on the right.
    /// Search also answers "which group and rule handle this domain"; related recent domains are
    /// offered as one-click additions.
    /// </summary>
    internal static void DomainGroups(DomainSet? focus = null)
    {
        if (Library is not { } library) return;
        var w = Shell(I18n.T("Мои группы доменов", "My domain groups"), out var body); w.Width = 940;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 8, 0, 0) };
        void Say(string text, bool problem = false) { status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "Muted"); status.Text = text; }
        async void Try(Func<Task> action) { try { Say(""); await action(); } catch (UserError e) { Say(e.Message, true); } }

        var columns = new Grid(); columns.ColumnDefinitions.Add(new() { Width = new(300) }); columns.ColumnDefinitions.Add(new() { Width = new(18) }); columns.ColumnDefinitions.Add(new());
        var left = new StackPanel();
        var search = new TextBox { Style = (Style)Application.Current.Resources["SearchBox"], Tag = I18n.T("Группа или домен, напр. git.hai.ru", "Group or domain, e.g. git.hai.example"), Margin = new(0, 0, 0, 6) };
        var lookup = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(2, 0, 0, 6), Visibility = Visibility.Collapsed }; lookup.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var groups = new ListBox { Height = 330, ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        var leftTools = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new(0, 8, 0, 0) };
        foreach (var element in new UIElement[] { search, lookup, groups, leftTools }) left.Children.Add(element);
        columns.Children.Add(left);
        var right = new StackPanel(); Grid.SetColumn(right, 2); columns.Children.Add(right);
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new());
        var badgeHost = new ContentControl(); header.Children.Add(badgeHost);
        var headerText = new StackPanel { Margin = new(12, 0, 0, 0) }; Grid.SetColumn(headerText, 1); header.Children.Add(headerText);
        var title = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var meta = MutedText(""); meta.Margin = new(0, 2, 0, 0);
        headerText.Children.Add(title); headerText.Children.Add(meta);
        var look = new WrapPanel { Margin = new(0, 10, 0, 0) };
        var source = MutedText(""); source.Margin = new(0, 4, 0, 0);
        var addRow = new Grid { Margin = new(0, 6, 0, 0) }; addRow.ColumnDefinitions.Add(new()); addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var addField = new TextBox { MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, Tag = "example.com, api.example.com" };
        var addButton = new Button { Content = IconText("Plus", I18n.T("Добавить", "Add")), MinHeight = 40, Margin = new(8, 0, 0, 0), Padding = new(14, 0, 14, 0) };
        Grid.SetColumn(addButton, 1); addRow.Children.Add(addField); addRow.Children.Add(addButton);
        var domains = new ListBox { Height = 160, SelectionMode = SelectionMode.Extended, Margin = new(0, 8, 0, 0), ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        var suggestTitle = MutedText(I18n.T("Похожие из недавних подключений — нажмите, чтобы добавить:", "Related recent domains — click to add:")); suggestTitle.Margin = new(0, 8, 0, 4);
        var suggestions = new WrapPanel();
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new(0, 8, 0, 0) };
        var ruleRow = new Grid { Margin = new(0, 4, 0, 0) }; ruleRow.ColumnDefinitions.Add(new() { Width = new(150) }); ruleRow.ColumnDefinitions.Add(new());
        var route = new ComboBox { ItemsSource = library.RouteChoices, DisplayMemberPath = nameof(RouteChoice.Label), SelectedIndex = 0, MinHeight = 40 };
        var createRule = new Button { Content = IconText("Rules", I18n.T("Создать правило из группы", "Create rule from group")), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 40, Margin = new(8, 0, 0, 0) };
        Grid.SetColumn(createRule, 1); ruleRow.Children.Add(route); ruleRow.Children.Add(createRule);
        var empty = MutedText(I18n.T("Групп пока нет. «Новая» — собрать домены под одним именем; «Импорт» — добавить группу, которой с вами поделились; «По ссылке» — группа, которая сама обновляется из списка в интернете; «Из набора» — копия встроенного набора, которую можно править.\n\nЕщё: выделите несколько правил на странице «Правила» → правая кнопка → «Объединить в группу», или добавьте домены прямо из «Подключений».",
            "No groups yet. New puts domains under one name; Import adds a group someone shared; From URL follows a list online; From set copies a bundled set you can edit.\n\nAlso: select rules on the Rules page → right-click → Combine into group, or add domains straight from Connections."));
        foreach (var element in new UIElement[] { header, look, source, addRow, domains, suggestTitle, suggestions, actions, ruleRow, empty }) right.Children.Add(element);

        DomainSet? Current() => (groups.SelectedItem as ListBoxItem)?.Tag as DomainSet;
        bool Matches(string domain, string filter) => domain.Contains(filter, StringComparison.OrdinalIgnoreCase) || filter.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        void Reload(string? select = null)
        {
            var filter = search.Text.Trim();
            var keep = select ?? Current()?.Name;
            groups.Items.Clear();
            foreach (var set in library.DomainSetList.Where(s => filter.Length == 0 || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Domains.Any(d => Matches(d, filter)))
                         .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                groups.Items.Add(new ListBoxItem { Content = GroupRowContent(set, Count(set.Domains.Count) + (set.SourceUrl is null ? "" : " · " + I18n.T("по ссылке", "from URL"))), Tag = set });
            groups.SelectedItem = groups.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((DomainSet)i.Tag).Name.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? groups.Items.OfType<ListBoxItem>().FirstOrDefault();
            var described = filter.Contains('.') ? library.DescribeDomain(filter) : "";
            lookup.Text = described; lookup.Visibility = described.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Show();
        }
        void Show()
        {
            var set = Current();
            foreach (var element in new UIElement[] { header, look, source, addRow, domains, actions, ruleRow }) element.Visibility = set is null ? Visibility.Collapsed : Visibility.Visible;
            empty.Visibility = set is null && library.DomainSetList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            suggestTitle.Visibility = suggestions.Visibility = Visibility.Collapsed;
            if (set is null) return;
            var used = library.RulesUsingCount(set);
            badgeHost.Content = GroupBadge(set, 44);
            title.Text = set.Name;
            meta.Text = Count(set.Domains.Count) + " · " + (used == 0 ? I18n.T("пока не используется в правилах", "not used by rules yet") : I18n.T("в правилах: ", "used by rules: ") + used) +
                " · " + I18n.T("в правиле: ", "in a rule: ") + DomainSets.Reference(set);
            source.Visibility = set.SourceUrl is null ? Visibility.Collapsed : Visibility.Visible;
            source.Text = set.SourceUrl is null ? "" : I18n.T("Обновляется по ссылке: ", "Follows: ") + set.SourceUrl + (set.Updated is { } at ? I18n.T(" · обновлено ", " · updated ") + at.ToLocalTime().ToString("g") : "");
            BuildLook(set);
            var filter = search.Text.Trim();
            domains.ItemsSource = set.Domains.Where(d => filter.Length == 0 || set.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || Matches(d, filter)).ToList();
            suggestions.Children.Clear();
            foreach (var host in library.SuggestRelated(set.Domains).Take(10))
            {
                var chip = new Button { Content = IconText("Plus", host), MinHeight = 30, Padding = new(10, 0, 10, 0), Margin = new(0, 0, 6, 6), FontSize = 12 };
                chip.Click += (_, _) => Try(() => { Replace(set, set.Domains.Append(host)); Reload(set.Name); return Task.CompletedTask; });
                suggestions.Children.Add(chip);
            }
            suggestTitle.Visibility = suggestions.Visibility = suggestions.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void BuildLook(DomainSet set)
        {
            look.Children.Clear();
            foreach (var (name, color) in GroupColors)
            {
                var swatch = new Button { Width = 28, Height = 28, MinHeight = 0, MinWidth = 0, Padding = new(0), Margin = new(0, 0, 6, 6), ToolTip = name, BorderThickness = new(name == set.Color ? 2 : 0),
                    Content = new Border { Width = 16, Height = 16, CornerRadius = new(8), Background = new SolidColorBrush(color) } };
                swatch.SetResourceReference(Control.BorderBrushProperty, "Text");
                swatch.Click += (_, _) => Try(() => { Restyle(set, color: name); return Task.CompletedTask; });
                look.Children.Add(swatch);
            }
            look.Children.Add(new Border { Width = 10 });
            foreach (var icon in DomainSets.Icons)
            {
                var pick = new Button { Width = 30, Height = 28, MinHeight = 0, MinWidth = 0, Padding = new(0), Margin = new(0, 0, 4, 6), ToolTip = icon, BorderThickness = new(icon == set.Icon ? 2 : 0),
                    Content = new IconView { Icon = icon, Width = 15, Height = 15, Foreground = GroupBrush(set) } };
                pick.SetResourceReference(Control.BorderBrushProperty, "Text");
                pick.Click += (_, _) => Try(() => { Restyle(set, icon: icon); return Task.CompletedTask; });
                look.Children.Add(pick);
            }
        }
        void Restyle(DomainSet set, string? color = null, string? icon = null)
        {
            var style = new DomainSet { Color = color ?? set.Color, Icon = icon ?? set.Icon, SourceUrl = set.SourceUrl, Updated = set.Updated };
            library.SaveDomainSet(set, DomainSets.Normalize(set.Name, set.Domains, set.Id, style)); Reload(set.Name);
        }
        void Replace(DomainSet set, IEnumerable<string> newDomains, string? name = null) =>
            library.SaveDomainSet(set, DomainSets.Normalize(name ?? set.Name, newDomains, set.Id, set));

        Button Tool(System.Windows.Controls.Primitives.UniformGrid row, string icon, string text, Func<Task> action)
        {
            var b = new Button { Content = IconText(icon, text), MinHeight = 38, Margin = new(0, 0, 6, 6), Padding = new(6, 0, 6, 0) };
            b.Click += (_, _) => Try(action); row.Children.Add(b); return b;
        }
        Tool(leftTools, "Plus", I18n.T("Новая", "New"), () => { if (DomainSetEditor(null) is { } made) Reload(made.Name); return Task.CompletedTask; });
        Tool(leftTools, "Download", I18n.T("Импорт", "Import"), () => { if (DomainSetImport() is { } made) Reload(made.Name); return Task.CompletedTask; });
        Tool(leftTools, "Link", I18n.T("По ссылке", "From URL"), async () => { if (await DomainSetSubscribeAsync() is { } made) Reload(made.Name); });
        Tool(leftTools, "GeoSite", I18n.T("Из набора", "From set"), () => { if (DomainSetFromBundled() is { } made) Reload(made.Name); return Task.CompletedTask; });
        var removeDomains = Tool(actions, "Trash", I18n.T("Убрать выбранные", "Remove selected"), () =>
        {
            if (Current() is not { } set || domains.SelectedItems.Count == 0) return Task.CompletedTask;
            var drop = domains.SelectedItems.Cast<string>().ToHashSet(StringComparer.Ordinal);
            if (drop.Count == set.Domains.Count) throw new UserError(I18n.T("В группе должен остаться хотя бы один домен. Чтобы убрать всё, удалите группу.", "A group needs at least one domain. Delete the group to remove everything."));
            Replace(set, set.Domains.Where(d => !drop.Contains(d))); Reload(set.Name); return Task.CompletedTask;
        });
        Tool(actions, "Edit", I18n.T("Переименовать", "Rename"), () =>
        {
            if (Current() is { } set && Text(I18n.T("Переименовать группу", "Rename group"), I18n.T("Новое название — правила обновятся сами", "New name — rules update automatically"), set.Name) is { } name)
            { Replace(set, set.Domains, name); Reload(name.Trim()); }
            return Task.CompletedTask;
        });
        Tool(actions, "Copy", I18n.T("Дублировать", "Duplicate"), () =>
        {
            if (Current() is not { } set) return Task.CompletedTask;
            var name = Enumerable.Range(2, 98).Select(i => set.Name + "-" + i).First(n => library.DomainSetList.All(s => !s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
            var copy = DomainSets.Normalize(name, set.Domains, style: new() { Color = set.Color, Icon = set.Icon }); library.SaveDomainSet(null, copy); Reload(copy.Name);
            return Task.CompletedTask;
        });
        Tool(actions, "Upload", I18n.T("Поделиться", "Share"), () => { if (Current() is { } set) DomainSetExport(set); return Task.CompletedTask; });
        var refresh = Tool(actions, "List", I18n.T("Изменить списком", "Edit as text"), async () =>
        {
            if (Current() is not { } set) return;
            if (set.SourceUrl is null) { if (DomainSetEditor(set) is { } made) Reload(made.Name); return; }
            await library.RefreshDomainSetAsync(set); Reload(set.Name); Say(I18n.T("Список обновлён по ссылке.", "Updated from its URL."));
        });
        Tool(actions, "Close", I18n.T("Удалить группу", "Delete group"), () =>
        {
            if (Current() is { } set && Confirm(I18n.T("Удалить группу?", "Delete group?"), set.Name)) { library.DeleteDomainSet(set); Reload(); }
            return Task.CompletedTask;
        });
        void AddDomains()
        {
            if (Current() is not { } set || addField.Text.Trim().Length == 0) return;
            Replace(set, set.Domains.Concat([addField.Text])); addField.Text = ""; Reload(set.Name);
        }
        addButton.Click += (_, _) => Try(() => { AddDomains(); return Task.CompletedTask; });
        addField.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Try(() => { AddDomains(); return Task.CompletedTask; }); } };
        createRule.Click += (_, _) => Try(() =>
        {
            if (Current() is not { } set || route.SelectedItem is not RouteChoice choice) return Task.CompletedTask;
            library.AddGroupRule(set, choice.Target);
            Say(I18n.T($"Правило «{set.Name}» → {choice.Label} добавлено в конец списка правил. Поднимите его стрелками, если нужен более высокий приоритет.",
                $"Rule ‘{set.Name}’ → {choice.Label} was added at the end of the rules. Raise it with the arrows for higher priority."));
            Show(); return Task.CompletedTask;
        });
        groups.SelectionChanged += (_, _) =>
        {
            Show();
            if (Current() is { } set) refresh.Content = IconText(set.SourceUrl is null ? "List" : "Refresh", set.SourceUrl is null ? I18n.T("Изменить списком", "Edit as text") : I18n.T("Обновить", "Refresh"));
        };
        domains.SelectionChanged += (_, _) => removeDomains.IsEnabled = domains.SelectedItems.Count > 0;
        search.TextChanged += (_, _) => Reload();
        body.Children.Add(columns); body.Children.Add(status);
        Reload(focus?.Name);
        removeDomains.IsEnabled = false;
        var done = new Button { Content = I18n.T("Готово", "Done"), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 44, Margin = new(0, 18, 0, 0), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 160 };
        done.Click += (_, _) => w.Close(); body.Children.Add(done);
        w.ShowDialog();
    }

    private static TextBlock MutedText(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 10) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return block;
    }
    private static string Count(int domains) => I18n.English ? domains + (domains == 1 ? " domain" : " domains")
        : domains + (domains % 10 == 1 && domains % 100 != 11 ? " домен" : domains % 10 is >= 2 and <= 4 && domains % 100 is < 12 or > 14 ? " домена" : " доменов");

    /// <summary>
    /// Pick entries from the user's groups and current rules: domains, keyword:word and IP/CIDR.
    /// Returns the chosen entries, or null when cancelled. Keywords are left out where they cannot apply.
    /// </summary>
    internal static List<string>? PickFromRules(bool keywords)
    {
        if (Library is not { } library) return null;
        var w = Shell(I18n.T("Из групп и правил", "From groups and rules"), out var body);
        body.Children.Add(MutedText(I18n.T("Отметьте группы и правила — их домены" + (keywords ? ", слова" : "") + " и адреса будут добавлены.",
            "Check groups and rules — their domains" + (keywords ? ", words" : "") + " and addresses will be added.")));
        var search = new TextBox { Style = (Style)Application.Current.Resources["SearchBox"], Tag = I18n.T("Поиск", "Search"), Margin = new(0, 0, 0, 8) };
        body.Children.Add(search);
        var items = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = items, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var choices = new List<(CheckBox Box, string Text, List<string> Entries)>();
        void Add(string icon, string title, string detail, List<string> entries)
        {
            entries = entries.Where(e => keywords || DomainSets.KindOf(e) != RuleKind.Contains).ToList();
            if (entries.Count == 0) return;
            var label = IconText(icon, title + " · " + detail); label.HorizontalAlignment = HorizontalAlignment.Left;
            var box = new CheckBox { Content = label, Margin = new(0, 4, 0, 4) };
            items.Children.Add(box); choices.Add((box, title + " " + string.Join(" ", entries), entries));
        }
        foreach (var set in library.DomainSetList.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            Add(set.Icon, "★ " + set.Name, Count(set.Domains.Count), set.Domains);
        foreach (var rule in library.CurrentRules)
        {
            var entries = rule.Kind switch
            {
                RuleKind.Site => rule.Values.ToList(),
                RuleKind.Contains => rule.Values.Select(v => DomainSets.KeywordPrefix + v).ToList(),
                RuleKind.Network => rule.Values.ToList(),
                RuleKind.GeoSite => rule.Values.Where(DomainSets.IsReference).SelectMany(v => DomainSets.Find(library.DomainSetList, v)?.Domains ?? []).ToList(),
                _ => []
            };
            Add(rule.DisplayIcon, rule.Name, RouteChoice.NameOf(rule.Target) + " · " + string.Join(", ", entries.Take(3)) + (entries.Count > 3 ? "…" : ""), entries);
        }
        if (choices.Count == 0) items.Children.Add(MutedText(I18n.T("Нет подходящих групп и правил.", "No suitable groups or rules.")));
        search.TextChanged += (_, _) =>
        {
            foreach (var (box, text, _) in choices)
                box.Visibility = search.Text.Trim().Length == 0 || text.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        };
        List<string>? result = null;
        Submit(body, I18n.T("Добавить", "Add"), () =>
        {
            result = choices.Where(c => c.Box.IsChecked == true).SelectMany(c => c.Entries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            w.DialogResult = true;
        });
        return w.ShowDialog() == true ? result : null;
    }

    private static TextBox Lines(StackPanel body, string label, string value, bool readOnly = false, double height = 220)
    {
        body.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 6), Foreground = (Brush)Application.Current.Resources["Muted"], TextWrapping = TextWrapping.Wrap });
        var field = new TextBox { Text = value, Height = height, AcceptsReturn = true, TextWrapping = readOnly ? TextWrapping.Wrap : TextWrapping.NoWrap, IsReadOnly = readOnly, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Padding = new(10, 8, 10, 8) };
        body.Children.Add(field); return field;
    }
    private static TextBlock ErrorLine(StackPanel body)
    {
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 8, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); body.Children.Add(error); return error;
    }

    /// <summary>Creates or edits a group as text. Renaming updates every rule that references it.</summary>
    internal static DomainSet? DomainSetEditor(DomainSet? old, IEnumerable<string>? prefill = null, string suggestedName = "")
    {
        var w = Shell(old is null ? I18n.T("Новая группа доменов", "New domain group") : I18n.T("Изменить группу", "Edit group"), out var body);
        var name = Field(body, I18n.T("Название — в правиле группа выглядит как my:название", "Name — a rule shows the group as my:name"), old?.Name ?? suggestedName);
        name.Tag = "hai";
        var domains = Lines(body, I18n.T("По одной записи в строке: домен (с поддоменами), keyword:слово — как «Адрес содержит», или IP / подсеть. https:// и пути убираются сами.",
            "One entry per line: a domain (with subdomains), keyword:word — like ‘Address contains’, or an IP / network. https:// and paths are removed."),
            string.Join(Environment.NewLine, old?.Domains ?? prefill ?? []));
        var fromRules = new Button { Content = IconText("Rules", I18n.T("Добавить из групп и правил…", "Add from groups and rules…")), MinHeight = 38, Margin = new(0, 8, 0, 0) };
        fromRules.Click += (_, _) =>
        {
            if (PickFromRules(keywords: true) is not { Count: > 0 } picked) return;
            var existing = domains.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            domains.Text = string.Join(Environment.NewLine, existing.Concat(picked.Where(p => !existing.Contains(p))));
        };
        body.Children.Add(fromRules);
        var error = ErrorLine(body);
        DomainSet? result = null;
        Submit(body, I18n.T("Сохранить", "Save"), () =>
        {
            try { result = DomainSets.Normalize(name.Text, [domains.Text], old?.Id, old); Library!.SaveDomainSet(old, result); w.DialogResult = true; }
            catch (UserError e) { error.Text = e.Message; }
        });
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>A group that follows an online list and refreshes itself.</summary>
    internal static async Task<DomainSet?> DomainSetSubscribeAsync()
    {
        var w = Shell(I18n.T("Группа по ссылке", "Group from URL"), out var body);
        body.Children.Add(MutedText(I18n.T("Bebekon скачает список и будет обновлять группу раз в 6 часов. Подходит общая группа Bebekon или простой список доменов (по одному в строке, # — комментарий).",
            "Bebekon downloads the list and refreshes the group every 6 hours. A shared Bebekon group or a plain domain list works (one per line, # comments).")));
        var name = Field(body, I18n.T("Название группы", "Group name"), ""); name.Tag = "work";
        var url = Field(body, I18n.T("Ссылка https://", "https:// link"), ""); url.Tag = "https://example.com/domains.txt";
        var error = ErrorLine(body);
        string? chosenName = null, chosenUrl = null;
        Submit(body, I18n.T("Загрузить", "Load"), () =>
        {
            try
            {
                DomainSets.Normalize(name.Text, ["check.example"]);
                if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new UserError(I18n.T("Укажите адрес https://.", "Enter an https:// address."));
                chosenName = name.Text; chosenUrl = url.Text; w.DialogResult = true;
            }
            catch (UserError e) { error.Text = e.Message; }
        });
        if (w.ShowDialog() != true || chosenName is null || chosenUrl is null) return null;
        return await Library!.SubscribeDomainSetAsync(chosenName, chosenUrl);
    }

    /// <summary>Copies a bundled GeoSite set into an editable group.</summary>
    internal static DomainSet? DomainSetFromBundled()
    {
        var w = Shell(I18n.T("Группа из встроенного набора", "Group from a bundled set"), out var body);
        body.Children.Add(MutedText(I18n.T("Получится обычная группа с доменами набора — её можно править и отправлять. Шаблоны вида regex/keyword в группу не переносятся.",
            "You get a regular group with the set's domains to edit and share. Regex/keyword patterns are not copied.")));
        var list = new ListBox { ItemsSource = GeoCatalog.Available(RuleKind.GeoSite), Height = 300, SelectedIndex = 0, ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        body.Children.Add(list);
        string? chosen = null;
        Submit(body, I18n.T("Скопировать", "Copy"), () => { chosen = list.SelectedItem as string; w.DialogResult = chosen is not null; });
        if (w.ShowDialog() != true || chosen is null) return null;
        var (domains, skipped) = GeoCatalog.Domains(chosen);
        var name = Enumerable.Range(1, 99).Select(i => i == 1 ? chosen + "-my" : chosen + "-my" + i).First(n => Library!.DomainSetList.All(s => !s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        var made = DomainSetEditor(null, domains, name);
        if (made is not null && skipped > 0) Confirm(I18n.T("Группа создана", "Group created"), I18n.T($"Не перенесено шаблонов: {skipped}. Если они важны, укажите встроенный набор «{chosen}» в правиле рядом с группой.", $"Patterns not copied: {skipped}. If they matter, add the bundled set ‘{chosen}’ to the rule next to the group."));
        return made;
    }

    internal static void DomainSetExport(DomainSet set)
    {
        var w = Shell(I18n.T("Поделиться группой", "Share group") + " · " + set.Name, out var body);
        var text = DomainSets.Export(set);
        var link = DomainSets.ToLink(set);
        Lines(body, I18n.T("Ссылка — самый короткий способ: получатель нажмёт на неё, и Bebekon предложит добавить группу.", "A link is the shortest way: the recipient clicks it and Bebekon offers to add the group."), link, readOnly: true, height: 70);
        Lines(body, I18n.T("Или текст и файл — открываются через «Мои группы» → «Импорт».", "Or text and file — opened via My groups → Import."), text, readOnly: true, height: 150);
        var status = MutedText(""); status.Margin = new(0, 8, 0, 0); body.Children.Add(status);
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new(0, 18, 0, 0) };
        void Copy(string value, string done)
        {
            try { Clipboard.SetText(value); status.Text = done; }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = I18n.T("Буфер обмена занят. Повторите.", "Clipboard is busy. Try again."); }
        }
        var save = new Button { Content = IconText("Download", I18n.T("Файл…", "File…")), MinHeight = 44, Margin = new(0, 0, 5, 0) };
        save.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = I18n.T("Группа доменов Bebekon", "Bebekon domain group") + " (*.json)|*.json", FileName = set.Name + ".bebekon-group.json" };
            if (dialog.ShowDialog(w) == true) { File.WriteAllText(dialog.FileName, text); status.Text = I18n.T("Файл сохранён.", "File saved."); }
        };
        var copyText = new Button { Content = IconText("Copy", I18n.T("Текст", "Text")), MinHeight = 44, Margin = new(5, 0, 5, 0) };
        copyText.Click += (_, _) => Copy(text, I18n.T("Текст скопирован. Вставьте в чат.", "Text copied. Paste it into a chat."));
        var copyLink = new Button { Content = IconText("Link", I18n.T("Ссылку", "Link")), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 44, Margin = new(5, 0, 0, 0), IsDefault = true };
        copyLink.Click += (_, _) => Copy(link, I18n.T("Ссылка скопирована. Вставьте в чат.", "Link copied. Paste it into a chat."));
        row.Children.Add(save); row.Children.Add(copyText); row.Children.Add(copyLink); body.Children.Add(row);
        w.ShowDialog();
    }

    /// <summary>Accepts a group link, shared text or file, or a plain list of domains (then asks for a name). Shows a preview before saving.</summary>
    internal static DomainSet? DomainSetImport(string? initial = null)
    {
        var w = Shell(I18n.T("Импорт группы", "Import group"), out var body);
        var prefill = initial ?? "";
        if (prefill.Length == 0)
            try { if (Clipboard.ContainsText() && Clipboard.GetText() is { } candidate && (candidate.Contains("bebekon-domain-set", StringComparison.Ordinal) || DomainSets.IsLink(candidate))) prefill = candidate; }
            catch (System.Runtime.InteropServices.ExternalException) { }
        var text = Lines(body, I18n.T("Вставьте ссылку или текст группы из чата, выберите файл или просто вставьте список доменов.", "Paste a group link or text from a chat, choose a file, or paste a plain list of domains."), prefill, height: 170);
        var preview = MutedText(""); preview.Margin = new(0, 8, 0, 0); body.Children.Add(preview);
        void Preview()
        {
            try { var set = DomainSets.Import(text.Text); preview.Text = I18n.T($"Группа «{set.Name}» · {Count(set.Domains.Count)}: ", $"Group ‘{set.Name}’ · {Count(set.Domains.Count)}: ") + string.Join(", ", set.Domains.Take(6)) + (set.Domains.Count > 6 ? "…" : ""); }
            catch (UserError) { preview.Text = ""; }
        }
        text.TextChanged += (_, _) => Preview(); Preview();
        var file = new Button { Content = IconText("Folder", I18n.T("Выбрать файл…", "Choose file…")), Margin = new(0, 10, 0, 0), MinHeight = 38 };
        file.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = I18n.T("Группа доменов Bebekon", "Bebekon domain group") + " (*.json)|*.json|" + I18n.T("Все файлы", "All files") + "|*.*" };
            if (dialog.ShowDialog(w) != true) return;
            text.Text = new FileInfo(dialog.FileName).Length > 256 * 1024 ? "" : File.ReadAllText(dialog.FileName);
        };
        body.Children.Add(file);
        var error = ErrorLine(body);
        DomainSet? result = null; string[]? plain = null;
        Submit(body, I18n.T("Импортировать", "Import"), () =>
        {
            try
            {
                if (!text.Text.Contains("bebekon-domain-set", StringComparison.Ordinal) && !text.Text.Contains(DomainSets.LinkPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var lines = text.Text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    DomainSets.Normalize("check", lines);
                    plain = lines; w.DialogResult = true; return;
                }
                var set = DomainSets.Import(text.Text);
                var existing = Library!.DomainSetList.FirstOrDefault(s => s.Name.Equals(set.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && !Confirm(I18n.T("Заменить группу?", "Replace group?"), I18n.T($"Группа «{existing.Name}» уже есть. Заменить её домены импортированными? Правила с этой группой продолжат работать.", $"Group ‘{existing.Name}’ already exists. Replace its domains? Rules that use it keep working.")))
                    return;
                if (existing is not null) set.Id = existing.Id;
                Library.SaveDomainSet(existing, set); result = set; w.DialogResult = true;
            }
            catch (UserError e) { error.Text = e.Message; }
        });
        w.ShowDialog();
        return plain is null ? result : DomainSetEditor(null, plain);
    }

    /// <summary>From Connections: picked domains (checked) plus related recent ones (unchecked) into an existing or new group.</summary>
    internal static DomainSet? AddToGroup(IReadOnlyList<string> hosts, IReadOnlyList<string> related)
    {
        if (Library is not { } library) return null;
        var w = Shell(I18n.T("Добавить в группу", "Add to group"), out var body);
        var target = new ComboBox { MinHeight = 42, Margin = new(0, 0, 0, 6) };
        const string NewGroup = "\u0001new";
        target.Items.Add(new ComboBoxItem { Content = IconText("Plus", I18n.T("Новая группа…", "New group…")), Tag = NewGroup });
        foreach (var set in library.DomainSetList.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            target.Items.Add(new ComboBoxItem { Content = IconText(set.Icon, set.Name + " · " + Count(set.Domains.Count)), Tag = set.Id });
        target.SelectedIndex = target.Items.Count > 1 ? 1 : 0;
        body.Children.Add(new TextBlock { Text = I18n.T("Группа", "Group"), Margin = new(0, 4, 0, 6), Foreground = (Brush)Application.Current.Resources["Muted"] });
        body.Children.Add(target);
        var checks = new StackPanel();
        void Add(string host, bool on, bool suggestion)
        {
            var label = IconText(suggestion ? "Sparkles" : "Site", host); label.HorizontalAlignment = HorizontalAlignment.Left;
            checks.Children.Add(new CheckBox { IsChecked = on, Margin = new(0, 4, 0, 4), Content = label, Tag = host });
        }
        foreach (var host in hosts) Add(host, true, false);
        if (related.Count > 0)
        {
            var hint = MutedText(I18n.T("Похожие из недавних подключений (тот же сайт или та же программа):", "Related recent domains (same site or same app):")); hint.Margin = new(0, 10, 0, 2);
            checks.Children.Add(hint);
            foreach (var host in related) Add(host, false, true);
        }
        body.Children.Add(new ScrollViewer { Content = checks, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 8, 0, 0) });
        var error = ErrorLine(body);
        DomainSet? result = null; string[]? fresh = null;
        Submit(body, I18n.T("Добавить", "Add"), () =>
        {
            try
            {
                var chosen = checks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
                if (chosen.Length == 0) throw new UserError(I18n.T("Отметьте хотя бы один домен.", "Check at least one domain."));
                if ((target.SelectedItem as ComboBoxItem)?.Tag as string == NewGroup) { fresh = chosen; w.DialogResult = true; return; }
                var set = library.DomainSetList.First(s => s.Id == (string)((ComboBoxItem)target.SelectedItem).Tag);
                result = DomainSets.Normalize(set.Name, set.Domains.Concat(chosen), set.Id, set);
                library.SaveDomainSet(set, result); w.DialogResult = true;
            }
            catch (UserError e) { error.Text = e.Message; }
        });
        w.ShowDialog();
        return fresh is null ? result : DomainSetEditor(null, fresh);
    }
}
