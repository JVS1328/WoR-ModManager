using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
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

    void AddMod_Click(object sender, RoutedEventArgs e) => OpenMenu(AddModButton, 270); // right-aligned with the button (menu is 270 wide)

    void ProfileMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(ProfileMenuButton, ProfileMenuButton.ActualWidth);

    static void OpenMenu(Button button, double width)
    {
        var menu = button.ContextMenu;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = button.ActualWidth - width;
        menu.VerticalOffset = 4;
        menu.IsOpen = true;
    }

    void AddFromFile_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Mod files (*.zip;*.pak)|*.zip;*.pak", Multiselect = true };
        if (d.ShowDialog(this) == true) _ = Vm.AddFilesAsync(d.FileNames);
    }

    void AddFromNexus_Click(object sender, RoutedEventArgs e) => _ = Vm.AddFromNexusLinkAsync();

    static string[] ModFiles(DragEventArgs e) => e.Data.GetData(DataFormats.FileDrop) is string[] files
        ? [.. files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))]
        : [];

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ModFiles(e).Length > 0 && Vm.CanAddMods ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (ModFiles(e) is { Length: > 0 } files) _ = Vm.AddFilesAsync(files);
    }
}
