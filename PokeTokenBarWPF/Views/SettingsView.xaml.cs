using System.Windows;
using System.Windows.Controls;
using PokeTokenBar.Core;
using PokeTokenBar.ViewModels;

namespace PokeTokenBar.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Store.Settings.Save();
            vm.Store.ApplyRefreshInterval();
            StartupManager.SetEnabled(vm.Store.Settings.LaunchAtStartup);
            vm.ShowToast("✓ Settings saved");
        }
    }
}
