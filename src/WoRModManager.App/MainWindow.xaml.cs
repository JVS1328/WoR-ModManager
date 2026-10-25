using System.Windows;
using WoRModManager.App.ViewModels;
using WoRModManager.App.Views;

namespace WoRModManager.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        App.UseDarkTitleBar(this);
    }

    MainViewModel Vm => (MainViewModel)DataContext;

    void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(Vm) { Owner = this }.ShowDialog();
}
