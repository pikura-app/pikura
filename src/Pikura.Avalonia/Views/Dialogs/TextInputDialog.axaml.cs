using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Pikura.Avalonia.Views.Dialogs;

public partial class TextInputDialog : Window
{
    public static readonly StyledProperty<string> DialogTitleProperty =
        AvaloniaProperty.Register<TextInputDialog, string>(nameof(DialogTitle), defaultValue: "Input");

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<TextInputDialog, string>(nameof(Description), defaultValue: string.Empty);

    public static readonly StyledProperty<string> InitialValueProperty =
        AvaloniaProperty.Register<TextInputDialog, string>(nameof(InitialValue), defaultValue: string.Empty);

    public string DialogTitle
    {
        get => GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string InitialValue
    {
        get => GetValue(InitialValueProperty);
        set => SetValue(InitialValueProperty, value);
    }

    public string? Result { get; private set; }

    public TextInputDialog()
    {
        InitializeComponent();
        Title = DialogTitle;
        DataContext = this;
        Loaded += (_, _) =>
        {
            var tb = this.FindControl<TextBox>("InputTextBox");
            tb?.Focus();
            tb?.SelectAll();
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Result = InputTextBox?.Text;
        Close(Result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
