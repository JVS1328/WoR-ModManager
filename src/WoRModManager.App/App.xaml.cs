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

    static Mutex? instance; // held for the app's lifetime

    void Start(string[] args)
    {
        // An updated or elevated copy is started by one that is still exiting, so it waits for it briefly.
        if (!TakeInstanceLock(wait: args.Contains("--cleanup-old") || args.Contains("--relaunch")))
        {
            PromptWindow.Show("The mod manager is already running.");
            Shutdown();
            return;
        }
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

    static bool TakeInstanceLock(bool wait)
    {
        instance = new Mutex(false, "WoRModManager.SingleInstance");
        try { return instance.WaitOne(wait ? TimeSpan.FromSeconds(10) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { return true; } // the previous copy exited without releasing it; it is ours now
    }

    internal static string? DetectGameRoot() => SteamLocator.FindGameRoot() is { } f && GamePaths.IsValidGameRoot(f) ? f : null;

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
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--relaunch") { UseShellExecute = true, Verb = "runas" })?.Dispose();
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
