using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bebekon.App;

/// <summary>Shared dialog helpers, the bundled GeoSite picker and "pick from rules and folders".</summary>
public static partial class Dialogs
{
    internal static readonly Dictionary<string, Color> GroupColors = new()
    {
        ["Blue"] = Color.FromRgb(0x4C, 0x8D, 0xFF), ["Cyan"] = Color.FromRgb(0x2E, 0xC5, 0xD8), ["Emerald"] = Color.FromRgb(0x34, 0xC7, 0x7B),
        ["Amber"] = Color.FromRgb(0xFF, 0xB5, 0x47), ["Rose"] = Color.FromRgb(0xFF, 0x6B, 0x8A), ["Violet"] = Color.FromRgb(0xA7, 0x8B, 0xFA)
    };

    /// <summary>Button content: icon followed by text.</summary>
    private static StackPanel IconText(string icon, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new IconView { Icon = icon, Width = 15, Height = 15, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }
    private static TextBlock MutedText(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 10) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return block;
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

    /// <summary>Picker for a GeoSite / GeoIP rule: the bundled sets.</summary>
    internal static string? GeoSet(RuleKind kind)
    {
        var w = Shell(I18n.T("Встроенные наборы", "Bundled sets") + " · " + kind, out var body);
        var list = new ListBox { Height = 330, ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        foreach (var value in GeoCatalog.Available(kind))
        {
            var label = IconText(kind == RuleKind.GeoSite ? "GeoSite" : "GeoIp", value); label.HorizontalAlignment = HorizontalAlignment.Left;
            list.Items.Add(new ListBoxItem { Content = label, Tag = value });
        }
        list.SelectedIndex = 0;
        body.Children.Add(list);
        Submit(body, I18n.T("Выбрать", "Select"), () => w.DialogResult = list.SelectedItem is not null);
        return w.ShowDialog() == true ? (list.SelectedItem as ListBoxItem)?.Tag as string : null;
    }

    /// <summary>
    /// Pick entries from current rules and folders: site domains, "Address contains" words (as keyword:word,
    /// only where allowed) and IP/CIDR. Returns the chosen entries, or null when cancelled.
    /// </summary>
    internal static List<string>? PickFromRules(IReadOnlyList<RoutingRule> rules, IReadOnlyList<RuleFolder> folders, bool keywords)
    {
        var w = Shell(I18n.T("Из правил и папок", "From rules and folders"), out var body);
        body.Children.Add(MutedText(I18n.T("Отметьте папки и правила — их сайты" + (keywords ? ", слова" : "") + " и IP-адреса будут добавлены.",
            "Check folders and rules — their sites" + (keywords ? ", words" : "") + " and IP addresses will be added.")));
        var search = new TextBox { Style = (Style)Application.Current.Resources["SearchBox"], Tag = I18n.T("Поиск", "Search"), Margin = new(0, 0, 0, 8) };
        body.Children.Add(search);
        var items = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = items, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var choices = new List<(CheckBox Box, string Text, List<string> Entries)>();
        List<string> EntriesOf(RoutingRule rule) => rule.Kind switch
        {
            RuleKind.Site => [.. rule.Values],
            RuleKind.Contains when keywords => [.. rule.Values.Select(v => DomainSets.KeywordPrefix + v)],
            RuleKind.Network => [.. rule.Values],
            _ => []
        };
        void Add(string icon, string title, string detail, List<string> entries, Thickness margin)
        {
            if (entries.Count == 0) return;
            var label = IconText(icon, title + " · " + detail); label.HorizontalAlignment = HorizontalAlignment.Left;
            var box = new CheckBox { Content = label, Margin = margin };
            items.Children.Add(box); choices.Add((box, title + " " + string.Join(" ", entries), entries));
        }
        foreach (var folder in folders)
        {
            var inside = rules.Where(r => r.FolderId == folder.Id).ToList();
            var entries = inside.SelectMany(EntriesOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Add(folder.Icon, "📁 " + folder.Name, entries.Count + I18n.T(" записей", " entries"), entries, new(0, 4, 0, 4));
        }
        foreach (var rule in rules)
        {
            var entries = EntriesOf(rule);
            Add(rule.DisplayIcon, rule.Name, RouteChoice.NameOf(rule.Target) + " · " + string.Join(", ", entries.Take(3)) + (entries.Count > 3 ? "…" : ""), entries, new(0, 4, 0, 4));
        }
        if (choices.Count == 0) items.Children.Add(MutedText(I18n.T("Нет правил с сайтами или IP-адресами.", "No rules with sites or IP addresses.")));
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
}
