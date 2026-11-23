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
}

public static partial class Dialogs
{
    internal static IDomainSetLibrary? Library { get; set; }
    private sealed record GeoChoice(string Value, string Label, DomainSet? Set);
    private sealed record GroupRow(DomainSet Set, string Label);

    /// <summary>Picker for a GeoSite rule: the user's ★ groups first, then bundled sets.</summary>
    internal static string? GeoSet(RuleKind kind)
    {
        var custom = kind == RuleKind.GeoSite && Library is not null;
        var w = Shell(custom ? I18n.T("Группы и наборы · GeoSite", "Groups and sets · GeoSite") : I18n.T("Встроенные наборы", "Bundled sets") + " · " + kind, out var body);
        List<GeoChoice> Choices() => (custom ? Library!.DomainSetList.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new GeoChoice(DomainSets.Reference(s), "★ " + s.Name + " · " + Count(s.Domains.Count), s)) : [])
            .Concat(GeoCatalog.Available(kind).Select(v => new GeoChoice(v, v, null))).ToList();
        var list = new ListBox { Height = custom ? 300 : 330, DisplayMemberPath = nameof(GeoChoice.Label), ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        void Reload(string? select = null)
        {
            var items = Choices(); list.ItemsSource = items;
            list.SelectedItem = items.FirstOrDefault(c => c.Value.Equals(select, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
        }
        Reload();
        if (custom)
        {
            body.Children.Add(MutedText(I18n.T("★ — ваши группы доменов. Создавайте, смотрите содержимое и делитесь ими в «Мои группы».",
                "★ — your domain groups. Create, inspect and share them in My groups.")));
            body.Children.Add(list);
            var manage = new Button { Content = I18n.T("Мои группы…", "My groups…"), MinHeight = 40, Margin = new(0, 10, 0, 0) };
            manage.Click += (_, _) => { var selected = (list.SelectedItem as GeoChoice)?.Set; DomainGroups(selected); Reload((list.SelectedItem as GeoChoice)?.Value); };
            body.Children.Add(manage);
        }
        else body.Children.Add(list);
        Submit(body, I18n.T("Выбрать", "Select"), () => w.DialogResult = list.SelectedItem is not null);
        return w.ShowDialog() == true ? (list.SelectedItem as GeoChoice)?.Value : null;
    }

    /// <summary>
    /// Manager for the user's domain groups: list on the left, the opened group on the right.
    /// Domains can be added or removed in place; a group can be renamed, duplicated, shared,
    /// imported, deleted or turned into a rule with one click.
    /// </summary>
    internal static void DomainGroups(DomainSet? focus = null)
    {
        if (Library is not { } library) return;
        var w = Shell(I18n.T("Мои группы доменов", "My domain groups"), out var body); w.Width = 900;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 8, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        void Try(Action action) { try { error.Text = ""; action(); } catch (UserError e) { error.Text = e.Message; } }

        var columns = new Grid(); columns.ColumnDefinitions.Add(new() { Width = new(290) }); columns.ColumnDefinitions.Add(new() { Width = new(18) }); columns.ColumnDefinitions.Add(new());
        // Left: groups.
        var left = new StackPanel();
        var search = new TextBox { Style = (Style)Application.Current.Resources["SearchBox"], Tag = I18n.T("Поиск группы или домена", "Search groups or domains"), Margin = new(0, 0, 0, 8) };
        var groups = new ListBox { Height = 352, DisplayMemberPath = nameof(GroupRow.Label), ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        var leftTools = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new(0, 8, 0, 0) };
        left.Children.Add(search); left.Children.Add(groups); left.Children.Add(leftTools);
        columns.Children.Add(left);
        // Right: the opened group.
        var right = new StackPanel(); Grid.SetColumn(right, 2); columns.Children.Add(right);
        var title = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var meta = MutedText(""); meta.Margin = new(0, 3, 0, 10);
        var addRow = new Grid(); addRow.ColumnDefinitions.Add(new()); addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var addField = new TextBox { MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, Tag = "example.com, api.example.com" };
        var addButton = new Button { Content = I18n.T("Добавить", "Add"), MinHeight = 40, Margin = new(8, 0, 0, 0), Padding = new(14, 0, 14, 0) };
        Grid.SetColumn(addButton, 1); addRow.Children.Add(addField); addRow.Children.Add(addButton);
        var domains = new ListBox { Height = 214, SelectionMode = SelectionMode.Extended, Margin = new(0, 8, 0, 0), ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new(0, 8, 0, 0) };
        var ruleRow = new Grid { Margin = new(0, 8, 0, 0) }; ruleRow.ColumnDefinitions.Add(new() { Width = new(150) }); ruleRow.ColumnDefinitions.Add(new());
        var route = new ComboBox { ItemsSource = library.RouteChoices, DisplayMemberPath = nameof(RouteChoice.Label), SelectedIndex = 0, MinHeight = 40 };
        var createRule = new Button { Content = I18n.T("Создать правило из группы", "Create rule from group"), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 40, Margin = new(8, 0, 0, 0) };
        Grid.SetColumn(createRule, 1); ruleRow.Children.Add(route); ruleRow.Children.Add(createRule);
        var empty = MutedText(I18n.T("Групп пока нет. Нажмите «Новая», чтобы собрать домены под одним именем, или «Импорт», чтобы добавить группу, которой с вами поделились.\n\nЕщё группу можно собрать прямо из правил: выделите несколько правил на странице «Правила» и выберите «Объединить в группу» в меню правой кнопки.",
            "No groups yet. Press New to put domains under one name, or Import to add a group someone shared with you.\n\nYou can also build a group from rules: select several rules on the Rules page and choose Combine into group from the right-click menu."));
        foreach (var element in new UIElement[] { title, meta, addRow, domains, actions, ruleRow, empty }) right.Children.Add(element);

        DomainSet? Current() => (groups.SelectedItem as GroupRow)?.Set;
        void Reload(string? select = null)
        {
            var filter = search.Text.Trim();
            var rows = library.DomainSetList.Where(s => filter.Length == 0 || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Domains.Any(d => d.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => new GroupRow(s, "★ " + s.Name + " · " + Count(s.Domains.Count))).ToList();
            var keep = select ?? Current()?.Name;
            groups.ItemsSource = rows;
            groups.SelectedItem = rows.FirstOrDefault(r => r.Set.Name.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
            Show();
        }
        void Show()
        {
            var set = Current();
            foreach (var element in new UIElement[] { title, meta, addRow, domains, actions, ruleRow }) element.Visibility = set is null ? Visibility.Collapsed : Visibility.Visible;
            empty.Visibility = set is null && library.DomainSetList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (set is null) return;
            var used = library.RulesUsingCount(set);
            title.Text = "★ " + set.Name;
            meta.Text = Count(set.Domains.Count) + " · " + (used == 0 ? I18n.T("пока не используется в правилах", "not used by rules yet") : I18n.T("используется в правилах: ", "used by rules: ") + used) +
                " · " + I18n.T("в правиле: ", "in a rule: ") + DomainSets.Reference(set);
            var filter = search.Text.Trim();
            domains.ItemsSource = set.Domains.Where(d => filter.Length == 0 || set.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || d.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        void Replace(DomainSet set, IEnumerable<string> newDomains, string? name = null) =>
            library.SaveDomainSet(set, DomainSets.Normalize(name ?? set.Name, newDomains, set.Id));

        Button Tool(System.Windows.Controls.Primitives.UniformGrid row, string text, Action action, bool accent = false)
        {
            var b = new Button { Content = text, MinHeight = 38, Margin = new(0, 0, 6, 6), Padding = new(6, 0, 6, 0) };
            if (accent) b.Style = (Style)Application.Current.Resources["AccentButton"];
            b.Click += (_, _) => Try(action); row.Children.Add(b); return b;
        }
        Tool(leftTools, I18n.T("Новая", "New"), () => { if (DomainSetEditor(null) is { } made) Reload(made.Name); });
        Tool(leftTools, I18n.T("Импорт", "Import"), () => { if (DomainSetImport() is { } made) Reload(made.Name); });
        var removeDomains = Tool(actions, I18n.T("Убрать выбранные", "Remove selected"), () =>
        {
            if (Current() is not { } set || domains.SelectedItems.Count == 0) return;
            var drop = domains.SelectedItems.Cast<string>().ToHashSet(StringComparer.Ordinal);
            if (drop.Count == set.Domains.Count) throw new UserError(I18n.T("В группе должен остаться хотя бы один домен. Чтобы убрать всё, удалите группу.", "A group needs at least one domain. Delete the group to remove everything."));
            Replace(set, set.Domains.Where(d => !drop.Contains(d))); Reload(set.Name);
        });
        Tool(actions, I18n.T("Переименовать", "Rename"), () =>
        {
            if (Current() is not { } set || Text(I18n.T("Переименовать группу", "Rename group"), I18n.T("Новое название — правила обновятся сами", "New name — rules update automatically"), set.Name) is not { } name) return;
            Replace(set, set.Domains, name); Reload(name.Trim());
        });
        Tool(actions, I18n.T("Дублировать", "Duplicate"), () =>
        {
            if (Current() is not { } set) return;
            var name = Enumerable.Range(2, 98).Select(i => set.Name + "-" + i).First(n => library.DomainSetList.All(s => !s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
            var copy = DomainSets.Normalize(name, set.Domains); library.SaveDomainSet(null, copy); Reload(copy.Name);
        });
        Tool(actions, I18n.T("Поделиться", "Share"), () => { if (Current() is { } set) DomainSetExport(set); });
        Tool(actions, I18n.T("Изменить списком", "Edit as text"), () => { if (Current() is { } set && DomainSetEditor(set) is { } made) Reload(made.Name); });
        Tool(actions, I18n.T("Удалить группу", "Delete group"), () =>
        {
            if (Current() is not { } set || !Confirm(I18n.T("Удалить группу?", "Delete group?"), set.Name)) return;
            library.DeleteDomainSet(set); Reload();
        });
        void AddDomains()
        {
            if (Current() is not { } set || addField.Text.Trim().Length == 0) return;
            Replace(set, set.Domains.Concat([addField.Text])); addField.Text = ""; Reload(set.Name);
        }
        addButton.Click += (_, _) => Try(AddDomains);
        addField.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Try(AddDomains); } };
        createRule.Click += (_, _) => Try(() =>
        {
            if (Current() is not { } set || route.SelectedItem is not RouteChoice choice) return;
            library.AddGroupRule(set, choice.Target);
            error.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            error.Text = I18n.T($"Правило «{set.Name}» → {choice.Label} добавлено в конец списка правил. Поднимите его стрелками, если нужен более высокий приоритет.",
                $"Rule ‘{set.Name}’ → {choice.Label} was added at the end of the rules. Raise it with the arrows for higher priority.");
            Show();
        });
        groups.SelectionChanged += (_, _) => { error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); error.Text = ""; Show(); };
        domains.SelectionChanged += (_, _) => removeDomains.IsEnabled = domains.SelectedItems.Count > 0;
        search.TextChanged += (_, _) => Reload();
        body.Children.Add(columns); body.Children.Add(error);
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

    private static TextBox Lines(StackPanel body, string label, string value, bool readOnly = false)
    {
        body.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 6), Foreground = (Brush)Application.Current.Resources["Muted"], TextWrapping = TextWrapping.Wrap });
        var field = new TextBox { Text = value, Height = 220, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsReadOnly = readOnly, VerticalContentAlignment = VerticalAlignment.Top,
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
        var domains = Lines(body, I18n.T("Домены — по одному в строке или через запятую. Поддомены включаются автоматически, https:// и пути убираются сами.", "Domains — one per line or comma-separated. Subdomains are included; https:// and paths are removed."),
            string.Join(Environment.NewLine, old?.Domains ?? prefill ?? []));
        var error = ErrorLine(body);
        DomainSet? result = null;
        Submit(body, I18n.T("Сохранить", "Save"), () =>
        {
            try { result = DomainSets.Normalize(name.Text, [domains.Text], old?.Id); Library!.SaveDomainSet(old, result); w.DialogResult = true; }
            catch (UserError e) { error.Text = e.Message; }
        });
        return w.ShowDialog() == true ? result : null;
    }

    internal static void DomainSetExport(DomainSet set)
    {
        var w = Shell(I18n.T("Поделиться группой", "Share group") + " · " + set.Name, out var body);
        var text = DomainSets.Export(set);
        Lines(body, I18n.T("Отправьте этот текст в чат или сохраните файл. Получатель откроет «Мои группы» → «Импорт» и вставит его.", "Send this text to a chat or save it as a file. The recipient opens My groups → Import and pastes it."), text, readOnly: true);
        var status = MutedText(""); status.Margin = new(0, 8, 0, 0); body.Children.Add(status);
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new(0, 18, 0, 0) };
        var save = new Button { Content = I18n.T("Сохранить файл…", "Save file…"), MinHeight = 44, Margin = new(0, 0, 5, 0) };
        save.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = I18n.T("Группа доменов Bebekon", "Bebekon domain group") + " (*.json)|*.json", FileName = set.Name + ".bebekon-group.json" };
            if (dialog.ShowDialog(w) == true) { File.WriteAllText(dialog.FileName, text); status.Text = I18n.T("Файл сохранён.", "File saved."); }
        };
        var copy = new Button { Content = I18n.T("Скопировать текст", "Copy text"), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 44, Margin = new(5, 0, 0, 0), IsDefault = true };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); status.Text = I18n.T("Скопировано. Вставьте в чат.", "Copied. Paste it into a chat."); }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = I18n.T("Буфер обмена занят. Повторите.", "Clipboard is busy. Try again."); }
        };
        row.Children.Add(save); row.Children.Add(copy); body.Children.Add(row);
        w.ShowDialog();
    }

    /// <summary>Accepts shared group text or file, or a plain list of domains (then asks for a name).</summary>
    internal static DomainSet? DomainSetImport()
    {
        var w = Shell(I18n.T("Импорт группы", "Import group"), out var body);
        string clip = "";
        try { if (Clipboard.ContainsText() && Clipboard.GetText() is { } candidate && candidate.Contains("bebekon-domain-set", StringComparison.Ordinal)) clip = candidate; }
        catch (System.Runtime.InteropServices.ExternalException) { }
        var text = Lines(body, I18n.T("Вставьте текст группы из чата, выберите файл или просто вставьте список доменов.", "Paste group text from a chat, choose a file, or paste a plain list of domains."), clip);
        var file = new Button { Content = I18n.T("Выбрать файл…", "Choose file…"), Margin = new(0, 10, 0, 0), MinHeight = 38 };
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
                if (!text.Text.Contains("bebekon-domain-set", StringComparison.Ordinal))
                {
                    // A plain domain list: let the user name it in the editor.
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
}
