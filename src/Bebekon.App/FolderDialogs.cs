using System.Windows.Media;

namespace Bebekon.App;

public static partial class Dialogs
{
    internal sealed record FolderEditResult(string Name, string Color, string Icon, List<RoutingRule> Rules, (RuleFolder Folder, List<RoutingRule> Rules)? Imported);
    internal enum FolderDeletion { Cancel, KeepRules, DeleteRules }

    /// <summary>
    /// Creates or edits a folder: name, colour, icon and which existing rules it holds. A new folder can
    /// instead be added from a link or text someone shared.
    /// </summary>
    internal static FolderEditResult? FolderEditor(RuleFolder? old, IReadOnlyList<RoutingRule> rules, string? sharedText = null)
    {
        var w = Shell(old is null ? I18n.T("Новая папка правил", "New rule folder") : I18n.T("Изменить папку", "Edit folder"), out var body); w.Width = 600;
        body.Children.Add(MutedText(I18n.T("Папка только собирает правила вместе: каждое правило внутри работает по своему маршруту — через VPN, напрямую или как захотите. Папку можно сворачивать и отправлять другим.",
            "A folder only keeps rules together: each rule inside keeps its own route — VPN, direct or anything else. Folders can be collapsed and shared.")));
        var name = Field(body, I18n.T("Название", "Name"), old?.Name ?? ""); name.Tag = I18n.T("Например: Работа", "e.g. Work");

        // Look.
        var color = old?.Color ?? "Blue"; var icon = old?.Icon ?? "Folder";
        body.Children.Add(new TextBlock { Text = I18n.T("Цвет и значок", "Colour and icon"), Margin = new(0, 12, 0, 6), Foreground = (Brush)Application.Current.Resources["Muted"] });
        var look = new WrapPanel(); body.Children.Add(look);
        void BuildLook()
        {
            look.Children.Clear();
            foreach (var (key, value) in GroupColors)
            {
                var swatch = new Button { Width = 30, Height = 30, MinHeight = 0, MinWidth = 0, Padding = new(0), Margin = new(0, 0, 6, 6), ToolTip = key, BorderThickness = new(key == color ? 2 : 0),
                    Content = new Border { Width = 16, Height = 16, CornerRadius = new(8), Background = new SolidColorBrush(value) } };
                swatch.SetResourceReference(Control.BorderBrushProperty, "Text");
                swatch.Click += (_, _) => { color = key; BuildLook(); };
                look.Children.Add(swatch);
            }
            look.Children.Add(new Border { Width = 10 });
            foreach (var key in RuleFolders.Icons)
            {
                var pick = new Button { Width = 32, Height = 30, MinHeight = 0, MinWidth = 0, Padding = new(0), Margin = new(0, 0, 4, 6), ToolTip = key, BorderThickness = new(key == icon ? 2 : 0),
                    Content = new IconView { Icon = key, Width = 15, Height = 15, Foreground = new SolidColorBrush(GroupColors.GetValueOrDefault(color, GroupColors["Blue"])) } };
                pick.SetResourceReference(Control.BorderBrushProperty, "Text");
                pick.Click += (_, _) => { icon = key; BuildLook(); };
                look.Children.Add(pick);
            }
        }
        BuildLook();

        // Rules inside.
        body.Children.Add(new TextBlock { Text = I18n.T("Правила в папке", "Rules in the folder"), Margin = new(0, 12, 0, 6), Foreground = (Brush)Application.Current.Resources["Muted"] });
        var search = new TextBox { Style = (Style)Application.Current.Resources["SearchBox"], Tag = I18n.T("Поиск правила", "Search rules"), Margin = new(0, 0, 0, 6) };
        body.Children.Add(search);
        var checks = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = checks, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var boxes = new List<(CheckBox Box, RoutingRule Rule)>();
        foreach (var rule in rules)
        {
            var label = IconText(rule.DisplayIcon, rule.Name + " · " + RouteChoice.NameOf(rule.Target) + " · " + rule.Description); label.HorizontalAlignment = HorizontalAlignment.Left;
            var box = new CheckBox { Content = label, Margin = new(0, 3, 0, 3), IsChecked = old is not null && rule.FolderId == old.Id };
            checks.Children.Add(box); boxes.Add((box, rule));
        }
        if (boxes.Count == 0) checks.Children.Add(MutedText(I18n.T("Правил пока нет — добавьте их потом кнопкой «+» на папке.", "No rules yet — add them later with + on the folder.")));
        search.TextChanged += (_, _) =>
        {
            foreach (var (box, rule) in boxes)
                box.Visibility = search.Text.Trim().Length == 0 || rule.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) || rule.Description.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };

        // Shared folder (new only).
        TextBox? shared = null; TextBlock? preview = null;
        if (old is null)
        {
            var expander = new Expander { Header = I18n.T("Или добавить папку, которой с вами поделились", "Or add a folder someone shared"), Margin = new(0, 12, 0, 0), Style = (Style)Application.Current.Resources["DetailsPanel"] };
            var importBody = new StackPanel(); expander.Content = importBody; body.Children.Add(expander);
            string clip = sharedText ?? "";
            if (clip.Length == 0) try { if (Clipboard.ContainsText() && Clipboard.GetText() is { } candidate && (RuleFolders.IsLink(candidate) || candidate.Contains("bebekon-rule-folder", StringComparison.Ordinal))) clip = candidate; }
            catch (System.Runtime.InteropServices.ExternalException) { }
            shared = Lines(importBody, I18n.T("Вставьте ссылку bebekon://folder/… или текст папки", "Paste a bebekon://folder/… link or folder text"), clip, height: 90);
            preview = MutedText(""); preview.Margin = new(0, 6, 0, 0); importBody.Children.Add(preview);
            var file = new Button { Content = IconText("Folder", I18n.T("Выбрать файл…", "Choose file…")), MinHeight = 36, Margin = new(0, 6, 0, 0) };
            file.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = I18n.T("Папка правил Bebekon", "Bebekon rule folder") + " (*.json)|*.json|" + I18n.T("Все файлы", "All files") + "|*.*" };
                if (dialog.ShowDialog(w) == true) shared.Text = new FileInfo(dialog.FileName).Length > 1024 * 1024 ? "" : File.ReadAllText(dialog.FileName);
            };
            importBody.Children.Add(file);
            void Preview()
            {
                try { var (f, r) = RuleFolders.Import(shared.Text); preview.Text = I18n.T($"Папка «{f.Name}»: ", $"Folder ‘{f.Name}’: ") + string.Join(", ", r.Take(5).Select(x => x.Name + " → " + RouteChoice.NameOf(x.Target))) + (r.Count > 5 ? "…" : ""); }
                catch (UserError) { preview.Text = ""; }
            }
            shared.TextChanged += (_, _) => Preview(); Preview();
            if (clip.Length > 0) expander.IsExpanded = true;
        }
        var error = ErrorLine(body);
        FolderEditResult? result = null;
        Submit(body, old is null ? I18n.T("Создать", "Create") : I18n.T("Сохранить", "Save"), () =>
        {
            try
            {
                if (shared is not null && shared.Text.Trim().Length > 0)
                {
                    var imported = RuleFolders.Import(shared.Text);
                    result = new(imported.Folder.Name, imported.Folder.Color, imported.Folder.Icon, [], imported); w.DialogResult = true; return;
                }
                result = new(RuleFolders.NormalizeName(name.Text), color, icon, boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Rule).ToList(), null);
                w.DialogResult = true;
            }
            catch (UserError e) { error.Text = e.Message; }
        });
        return w.ShowDialog() == true ? result : null;
    }

    internal static void FolderShare(RuleFolder folder, List<RoutingRule> rules)
    {
        var w = Shell(I18n.T("Поделиться папкой", "Share folder") + " · " + folder.Name, out var body);
        var text = RuleFolders.Export(folder, rules);
        var link = RuleFolders.ToLink(folder, rules);
        body.Children.Add(MutedText(I18n.T($"В папке {rules.Count} правил с их маршрутами. Привязки к вашим серверам не передаются — у получателя правила пойдут через его выбранный сервер.",
            $"The folder has {rules.Count} rules with their routes. Links to your servers are not shared — the recipient's rules use their selected server.")));
        Lines(body, I18n.T("Ссылка — получатель нажмёт на неё, и Bebekon покажет папку перед добавлением.", "Link — the recipient clicks it and Bebekon shows the folder before adding it."), link, readOnly: true, height: 70);
        Lines(body, I18n.T("Или текст и файл — добавляются через «Новая папка» → «Добавить папку, которой поделились».", "Or text and file — added via New folder → Add a shared folder."), text, readOnly: true, height: 140);
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
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = I18n.T("Папка правил Bebekon", "Bebekon rule folder") + " (*.json)|*.json", FileName = string.Concat(folder.Name.Split(Path.GetInvalidFileNameChars())) + ".bebekon-folder.json" };
            if (dialog.ShowDialog(w) == true) { File.WriteAllText(dialog.FileName, text); status.Text = I18n.T("Файл сохранён.", "File saved."); }
        };
        var copyText = new Button { Content = IconText("Copy", I18n.T("Текст", "Text")), MinHeight = 44, Margin = new(5, 0, 5, 0) };
        copyText.Click += (_, _) => Copy(text, I18n.T("Текст скопирован. Вставьте в чат.", "Text copied. Paste it into a chat."));
        var copyLink = new Button { Content = IconText("Link", I18n.T("Ссылку", "Link")), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 44, Margin = new(5, 0, 0, 0), IsDefault = true };
        copyLink.Click += (_, _) => Copy(link, I18n.T("Ссылка скопирована. Вставьте в чат.", "Link copied. Paste it into a chat."));
        row.Children.Add(save); row.Children.Add(copyText); row.Children.Add(copyLink); body.Children.Add(row);
        w.ShowDialog();
    }

    internal static FolderDeletion DeleteFolderChoice(string name, int rules)
    {
        var w = Shell(I18n.T("Удалить папку?", "Delete folder?"), out var body);
        body.Children.Add(MutedText(I18n.T($"В папке «{name}» {rules} правил. Можно убрать только папку — правила останутся в списке на своих местах — или удалить их вместе с ней.",
            $"Folder ‘{name}’ has {rules} rules. Remove only the folder — the rules stay in place — or delete them too.")));
        var choice = FolderDeletion.Cancel;
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new(0, 18, 0, 0) };
        Button Option(string text, FolderDeletion value, bool danger = false)
        {
            var b = new Button { Content = text, MinHeight = 44, Margin = new(3, 0, 3, 0) };
            if (danger) b.SetResourceReference(Control.ForegroundProperty, "Danger");
            b.Click += (_, _) => { choice = value; w.DialogResult = value != FolderDeletion.Cancel; };
            row.Children.Add(b); return b;
        }
        Option(I18n.T("Отмена", "Cancel"), FolderDeletion.Cancel).IsCancel = true;
        Option(I18n.T("Только папку", "Folder only"), FolderDeletion.KeepRules).IsDefault = true;
        Option(I18n.T("С правилами", "With rules"), FolderDeletion.DeleteRules, danger: true);
        body.Children.Add(row);
        w.ShowDialog();
        return choice;
    }
}
