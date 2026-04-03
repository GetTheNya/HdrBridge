using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HdrBridge.ViewModels;

namespace HdrBridge;

public partial class MainWindow : Window {
    public MainWindow() {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        // TaskbarIcon sits outside the WPF visual tree, so it does NOT
        // inherit DataContext from the Window. We must set it explicitly.
        TrayIcon.DataContext = DataContext;
    }

    private void SyncToggleMenuItem_Click(object sender, RoutedEventArgs e) {
        if (DataContext is MainViewModel vm) {
            _ = vm.ToggleServices();
        }
    }

    private void TrayContextMenu_Opened(object sender, RoutedEventArgs e) {
        // Push DataContext into the ContextMenu (also disconnected from visual tree)
        if (sender is ContextMenu menu)
            menu.DataContext = DataContext;

        if (DataContext is MainViewModel vm)
            SyncToggleMenuItem.Header = vm.IsServicesEnabled ? "Disable Sync" : "Enable Sync";
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void MenuItem_Open_Click(object sender, RoutedEventArgs e) {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (DataContext is MainViewModel vm)
            vm.SaveStartupPreference();
    }

    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e) {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
            App.AllowMainWindowClose = true;
            TrayIcon?.Dispose();
            Application.Current.Shutdown();
        }));
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
