using System.Windows;

namespace WoRModManager.App.Views;

public partial class PromptWindow : Window
{
    PromptWindow(string message, string ok, string? cancel, string? input)
    {
        InitializeComponent();
        App.UseDarkTitleBar(this);
        Message.Text = message;
        OkButton.Content = ok;
        if (cancel is null) CancelButton.Visibility = Visibility.Collapsed;
        else CancelButton.Content = cancel;
        if (input is not null)
        {
            Input.Text = input;
            Input.Visibility = Visibility.Visible;
            Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
        }
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != this) ?? Application.Current.MainWindow;
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static bool Ask(string message, string ok, string? cancel = "Cancel") => new PromptWindow(message, ok, cancel, null).ShowDialog() == true;

    public static void Show(string message) => Ask(message, "OK", null);

    public static string? AskText(string message, string text, string ok)
    {
        var w = new PromptWindow(message, ok, "Cancel", text);
        return w.ShowDialog() == true && w.Input.Text.Trim() is { Length: > 0 } t ? t : null;
    }
}
