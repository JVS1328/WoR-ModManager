using System.Windows;
using Microsoft.Win32;
using WoRModManager.App.ViewModels;
using WoRModManager.Core;

namespace WoRModManager.App.Views;

public partial class SettingsWindow : Window
{
    readonly MainViewModel? vm;
    string? pickedRoot;

    public SettingsWindow(MainViewModel vm) : this((MainViewModel?)vm, false) { }

    SettingsWindow(MainViewModel? vm, bool firstRun)
    {
        InitializeComponent();
        App.UseDarkTitleBar(this);
        this.vm = vm;
        VersionText.Text = $"War of Rights Mod Manager {MainViewModel.AppVersion.ToString(3)}";
        if (firstRun)
        {
            Title = Heading.Text = "Find War of Rights";
            Intro.Visibility = ContinueButton.Visibility = Visibility.Visible;
            ManagedSection.Visibility = Visibility.Collapsed;
            CloseButton.Content = "Quit";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        Refresh();
    }

    // First run without a detectable game: the player must pick a valid folder or quit.
    public static string? PickGameRoot()
    {
        var w = new SettingsWindow(null, true);
        return w.ShowDialog() == true ? w.pickedRoot : null;
    }

    void Refresh()
    {
        GameFolder.Text = vm?.Paths.GameRoot ?? pickedRoot ?? "";
        ModsFolder.Text = vm?.Paths.ModsFolder ?? "";
    }

    void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (App.DetectGameRoot() is { } root) _ = UseGameRootAsync(root);
        else PromptWindow.Show("War of Rights wasn't found in your Steam libraries. Use Browse… to pick its folder.");
    }

    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Choose the War of Rights folder" };
        if (d.ShowDialog(this) != true) return;
        if (GamePaths.IsValidGameRoot(d.FolderName)) _ = UseGameRootAsync(d.FolderName);
        else PromptWindow.Show(@"That folder doesn't contain bin\win_x64\WarOfRights.exe. Pick the War of Rights folder.");
    }

    async Task UseGameRootAsync(string root)
    {
        if (vm is null)
        {
            pickedRoot = root;
            ContinueButton.IsEnabled = true;
        }
        else if (!MainViewModel.SamePath(root, vm.Paths.GameRoot))
        {
            if (vm.IsBusy) PromptWindow.Show("Wait for the current operation to finish, then try again.");
            else await vm.ChangeGameRootAsync(root);
        }
        Refresh();
    }

    void BrowseMods_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Choose an empty folder for the manager's files" };
        if (d.ShowDialog(this) == true) _ = UseModsFolderAsync(d.FolderName);
    }

    void ResetMods_Click(object sender, RoutedEventArgs e) => _ = UseModsFolderAsync(GamePaths.DefaultModsFolder(vm!.Paths.GameRoot));

    async Task UseModsFolderAsync(string folder)
    {
        await vm!.ChangeModsFolderAsync(folder);
        Refresh();
    }

    void OpenGame_Click(object sender, RoutedEventArgs e) => MainViewModel.ShellOpen(vm!.Paths.GameRoot);

    void OpenMods_Click(object sender, RoutedEventArgs e) => MainViewModel.ShellOpen(vm!.Paths.ModsFolder);

    void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
