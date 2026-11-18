using System.Windows.Automation;

namespace Bebekon.App;

public sealed class AddressButton : Button
{
    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(
        nameof(Address), typeof(string), typeof(AddressButton), new PropertyMetadata(null, (sender, _) => ((AddressButton)sender).HideAddress()));
    public string? Address { get => (string?)GetValue(AddressProperty); set => SetValue(AddressProperty, value); }
    private readonly IconView eye = new() { Width = 15, Height = 15, Margin = new(0, 0, 6, 0) };
    private readonly TextBlock value = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private bool revealed;

    public AddressButton()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(eye);
        row.Children.Add(value);
        Content = row;
        Padding = new(7, 3, 7, 3);
        MinHeight = 26;
        Unloaded += (_, _) => HideAddress();
        Loaded += (_, _) => HideAddress();
        HideAddress();
    }

    protected override void OnClick()
    {
        revealed = !revealed;
        RenderAddress();
        base.OnClick();
    }

    private void HideAddress() { revealed = false; RenderAddress(); }
    private void RenderAddress()
    {
        var available = !string.IsNullOrWhiteSpace(Address) && Address != "—";
        IsEnabled = available;
        eye.Icon = revealed ? "Eye" : "EyeOff";
        value.Text = available ? revealed ? Address : "••••" : "—";
        var action = revealed ? I18n.T("Скрыть IP", "Hide IP") : I18n.T("Показать IP", "Show IP");
        AutomationProperties.SetName(this, action);
        ToolTip = revealed ? Address : action;
    }
}
