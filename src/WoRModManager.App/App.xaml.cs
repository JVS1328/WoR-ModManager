using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WoRModManager.App.ViewModels;
using WoRModManager.App.Views;
using WoRModManager.Core;

namespace WoRModManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            ShowError(a.Exception);
            a.Handled = true; // the app keeps running
        };
        try
        {
            Start(e.Args);
        }
        catch (Exception ex)
        {
            ShowError(ex);
            Shutdown();
        }
    }

    void Start(string[] args)
    {
        if (args.Contains("--cleanup-old")) SelfUpdater.CleanupOld(Environment.ProcessPath!);
        var saved = SettingsStore.Load(MainViewModel.SettingsFile);
        var root = saved.GameRoot is { } r && GamePaths.IsValidGameRoot(r) ? r : DetectGameRoot() ?? SettingsWindow.PickGameRoot();
        if (root is null)
        {
            Shutdown();
            return;
        }
        var mods = saved.ModsFolder ?? GamePaths.DefaultModsFolder(root);
        Directory.CreateDirectory(mods);
        SettingsStore.Save(MainViewModel.SettingsFile, new AppSettings(root, mods));

        var vm = new MainViewModel(new GamePaths(root, mods));
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();
        _ = vm.StartAsync();
    }

    internal static string? DetectGameRoot()
    {
        try { return SteamLocator.FindGameRoot() is { } f && GamePaths.IsValidGameRoot(f) ? f : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void ShowError(Exception e)
    {
        switch (e)
        {
            case GameRunningException:
                PromptWindow.Show("Close War of Rights first.");
                break;
            case UnauthorizedAccessException:
                if (PromptWindow.Ask("Windows blocked writing to the game folder. Relaunch as administrator?", "Relaunch", "Not now")) RelaunchAsAdmin();
                break;
            default:
                PromptWindow.Show(e.Message);
                break;
        }
    }

    static void RelaunchAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" })?.Dispose();
        }
        catch (Win32Exception)
        {
            return; // the player declined the UAC prompt
        }
        Current.Shutdown();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Dark title bar to match the theme (DWMWA_USE_IMMERSIVE_DARK_MODE); ignored on systems without it.
    internal static void UseDarkTitleBar(Window w) => w.SourceInitialized += (_, _) =>
    {
        var on = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, sizeof(int));
    };
}
