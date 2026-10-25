using System.Windows;

namespace WoRModManager.App.Views;

public partial class ModDetailsWindow : Window
{
    public ModDetailsWindow()
    {
        InitializeComponent();
        App.UseDarkTitleBar(this);
    }

    // Click runs before the bound command, so the delete confirmation shows over the main window.
    void Uninstall_Click(object sender, RoutedEventArgs e) => Close();
}
