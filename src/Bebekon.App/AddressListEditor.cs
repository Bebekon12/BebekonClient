using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Bebekon.App;

public static partial class Dialogs
{
    private sealed record AddressEntry(string Value, string Icon, string Kind);

    /// <summary>
    /// Edits a list of protected addresses the way rules are edited: choose the type (site or IP / network),
    /// type a value and add it, see each entry with its icon and remove it, or pull entries from rules and folders.
    /// </summary>
    internal static string[]? AddressList(string title, string description, IEnumerable<string> initial, Func<string[], string[]> validate, Func<List<string>?>? pick = null)
    {
        var w = Shell(title, out var body); w.Width = 580;
        body.Children.Add(MutedText(description));
        var entries = new ObservableCollection<AddressEntry>();
        AddressEntry Describe(string value)
        {
            var network = System.Net.IPAddress.TryParse(value.Split('/')[0], out _);
            return new(value, network ? "Network" : "Site", network ? I18n.T("IP / подсеть", "IP / network") : I18n.T("Сайт и поддомены", "Site and subdomains"));
        }
        foreach (var start in initial.Select(v => v.Trim()).Where(v => v.Length > 0 && !v.StartsWith('#'))) entries.Add(Describe(start));
        TextBlock error = null!;

        // Type, like in the rule editor.
        var types = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        var group = "AddressType" + Guid.NewGuid().ToString("N");
        var site = TypeChoice(I18n.T("Сайт", "Site"), "Site", group); var network = TypeChoice(I18n.T("IP или подсеть", "IP or network"), "Network", group);
        types.Children.Add(site); types.Children.Add(network); body.Children.Add(types);
        var hint = MutedText(""); hint.Margin = new(0, 2, 0, 6); body.Children.Add(hint);
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var field = new TextBox { MinHeight = 46, VerticalContentAlignment = VerticalAlignment.Center };
        var add = new Button { Content = IconText("Plus", I18n.T("Добавить", "Add")), MinHeight = 46, Margin = new(8, 0, 0, 0), Padding = new(16, 0, 16, 0), Style = (Style)Application.Current.Resources["AccentButton"] };
        Grid.SetColumn(add, 1); row.Children.Add(field); row.Children.Add(add); body.Children.Add(row);
        void Kind()
        {
            var isNetwork = network.IsChecked == true;
            field.Tag = isNetwork ? "203.0.113.0/24" : "chatgpt.com";
            hint.Text = isNetwork ? I18n.T("Отдельный IP или подсеть CIDR. Можно вставить несколько через запятую или с новой строки.", "A single IP or CIDR network. Paste several separated by commas or lines.")
                : I18n.T("Домен вместе со всеми поддоменами, без https://. Можно вставить несколько.", "A domain with all its subdomains, without https://. You can paste several.");
        }
        site.Checked += (_, _) => Kind(); network.Checked += (_, _) => Kind(); site.IsChecked = true; Kind();

        // Current entries.
        var header = new Grid { Margin = new(0, 14, 0, 6) };
        var count = new TextBlock { FontWeight = FontWeights.SemiBold };
        header.Children.Add(count);
        if (pick is not null)
        {
            var fromRules = new Button { Content = IconText("Rules", I18n.T("Из правил и папок…", "From rules and folders…")), HorizontalAlignment = HorizontalAlignment.Right, MinHeight = 34, Padding = new(12, 0, 12, 0) };
            fromRules.Click += (_, _) => { if (pick() is { Count: > 0 } picked) Add(picked); };
            header.Children.Add(fromRules);
        }
        body.Children.Add(header);
        var list = new ListBox { ItemsSource = entries, MaxHeight = 260, MinHeight = 70, ItemContainerStyle = (Style)Application.Current.Resources["PickerItem"] };
        var template = new DataTemplate();
        var grid = new FrameworkElementFactory(typeof(DockPanel));
        var remove = new FrameworkElementFactory(typeof(Button));
        remove.SetValue(DockPanel.DockProperty, Dock.Right);
        remove.SetValue(FrameworkElement.StyleProperty, Application.Current.Resources["QuietIconButton"]);
        remove.SetValue(FrameworkElement.ToolTipProperty, I18n.T("Убрать", "Remove"));
        var cross = new FrameworkElementFactory(typeof(IconView)); cross.SetValue(IconView.IconProperty, "Close"); cross.SetValue(FrameworkElement.WidthProperty, 13.0); cross.SetValue(FrameworkElement.HeightProperty, 13.0);
        remove.AppendChild(cross);
        remove.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (((FrameworkElement)sender).DataContext is AddressEntry entry) entries.Remove(entry); }));
        grid.AppendChild(remove);
        var icon = new FrameworkElementFactory(typeof(IconView)); icon.SetBinding(IconView.IconProperty, new System.Windows.Data.Binding(nameof(AddressEntry.Icon)));
        icon.SetValue(FrameworkElement.WidthProperty, 16.0); icon.SetValue(FrameworkElement.HeightProperty, 16.0); icon.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 10, 0));
        icon.SetResourceReference(IconView.ForegroundProperty, "Accent");
        grid.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(StackPanel));
        var value = new FrameworkElementFactory(typeof(TextBlock)); value.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AddressEntry.Value))); value.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var kind = new FrameworkElementFactory(typeof(TextBlock)); kind.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AddressEntry.Kind))); kind.SetValue(TextBlock.FontSizeProperty, 11.0); kind.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.AppendChild(value); text.AppendChild(kind); text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(text);
        template.VisualTree = grid; list.ItemTemplate = template;
        body.Children.Add(list);
        var empty = MutedText(I18n.T("Пока пусто — добавьте сайт или адрес выше.", "Empty — add a site or address above.")); empty.Margin = new(0, 6, 0, 0); body.Children.Add(empty);
        error = ErrorLine(body);
        void Update() { count.Text = I18n.T($"Адреса · {entries.Count}", $"Addresses · {entries.Count}"); empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
        entries.CollectionChanged += (_, _) => Update(); Update();

        void Add(IEnumerable<string> raw)
        {
            try
            {
                error.Text = "";
                var items = raw.SelectMany(v => v.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
                if (items.Length == 0) return;
                var normalized = validate(items);
                foreach (var item in normalized)
                    if (entries.All(e => !e.Value.Equals(item, StringComparison.OrdinalIgnoreCase))) entries.Add(Describe(item));
                field.Text = "";
            }
            catch (UserError e) { error.Text = e.Message; }
        }
        add.Click += (_, _) => Add([field.Text]);
        field.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Add([field.Text]); } };

        string[]? result = null;
        Submit(body, I18n.T("Сохранить", "Save"), () =>
        {
            try
            {
                // Something typed but not added yet is added on save.
                if (field.Text.Trim().Length > 0) { Add([field.Text]); if (error.Text.Length > 0) return; }
                result = validate([.. entries.Select(e => e.Value)]); w.DialogResult = true;
            }
            catch (UserError e) { error.Text = e.Message; }
        });
        return w.ShowDialog() == true ? result : null;
    }
    private static RadioButton TypeChoice(string text, string icon, string group)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new IconView { Icon = icon, Width = 17, Height = 17, Margin = new(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) });
        row.Children.Add(label);
        return new() { GroupName = group, Content = row, Style = (Style)Application.Current.Resources["RuleChoice"], MinHeight = 44, Margin = new(0, 0, 8, 8) };
    }
}
