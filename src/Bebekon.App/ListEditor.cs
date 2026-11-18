namespace Bebekon.App;

public static partial class Dialogs
{
    internal static string[]? Lines(string title, string description, IEnumerable<string> initial,
        Func<string[], string[]> validate, int maxLength = 65536)
    {
        var window = Shell(title, out var body);
        body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["Subtitle"] });
        var field = new TextBox { Text = string.Join(Environment.NewLine, initial), AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top, MinHeight = 150, MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = maxLength, Margin = new(0,10,0,6) };
        body.Children.Add(field);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); body.Children.Add(error);
        string[]? result = null;
        Submit(body, I18n.T("Сохранить", "Save"), () =>
        {
            try { result = validate(field.Text.Replace("\r", "").Split('\n')); window.DialogResult = true; }
            catch (UserError e) { error.Text = e.Message; }
        });
        return window.ShowDialog() == true ? result : null;
    }
}
