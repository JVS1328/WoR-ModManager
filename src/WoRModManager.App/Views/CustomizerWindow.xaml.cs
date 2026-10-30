using System.Windows;
using WoRModManager.App.ViewModels;

namespace WoRModManager.App.Views;

public partial class CustomizerWindow : Window
{
    public CustomizerWindow(CustomizerViewModel vm)
    {
        InitializeComponent();
        App.UseDarkTitleBar(this);
        DataContext = vm;
        vm.Saved += Close;
    }
}
