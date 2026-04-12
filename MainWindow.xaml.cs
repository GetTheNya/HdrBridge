using HdrBridge.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xceed.Wpf.AvalonDock.Controls;

namespace HdrBridge;

public partial class MainWindow : Window {
    // Delayed single-click timer so double-click doesn't also toggle sync.
    private readonly DispatcherTimer _singleClickTimer;

    public MainWindow() {
        InitializeComponent();
        // TaskbarIcon sits outside the WPF visual tree, so it does NOT
        // inherit DataContext from the Window. Set it right after construction
        // so that it works even when the window is never shown (autostart).
        DataContextChanged += (_, _) => TrayIcon.DataContext = DataContext;

        _singleClickTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _singleClickTimer.Tick += (_, _) => {
            _singleClickTimer.Stop();
            if (DataContext is MainViewModel vm)
                _ = vm.ToggleServices();
        };
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

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e) {
        // Start a short timer; if a double-click follows it will be cancelled.
        _singleClickTimer.Start();
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) {
        // Cancel any pending single-click toggle so we only open the window.
        _singleClickTimer.Stop();
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void MenuItem_Open_Click(object sender, RoutedEventArgs e) {
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (DataContext is MainViewModel vm)
            vm.SaveStartupPreference();
    }

    private void Brightness_Click(object sender, RoutedEventArgs e) {
        var selectedItem = sender as MenuItem;

        ClearAllBrightnessCheckmarks(selectedItem);

        selectedItem.IsChecked = true;

        string tagValue = selectedItem.Tag?.ToString();

        if (DataContext is MainViewModel vm) {
            int brightness = vm.HyperHdrMaxBrightness;

            switch (tagValue) {
                case "0": brightness = 100;  break;
                case "1": brightness = 75; break;
                case "2": brightness = 50;  break;
                case "3": brightness = 25; break;
            }

            vm.HyperHdrMaxBrightness = brightness;
        }
    }

    private void Brightness_Change(object sender, RoutedEventArgs e) {
        var selectedItem = sender as MenuItem;

        int step = (selectedItem.Tag.ToString() == "0") ? 10 : -10;

        if (DataContext is MainViewModel vm) {
            int brightness = vm.HyperHdrMaxBrightness;

            brightness += step;

            if (brightness > 100) brightness = 100;
            if (brightness < 5) brightness = 5;

            vm.HyperHdrMaxBrightness = brightness;

            ClearAllBrightnessCheckmarks(selectedItem);
        }
    }

    private void ClearAllBrightnessCheckmarks(MenuItem currentItem) {
        if (currentItem.Parent is MenuItem parentMenu) {
            foreach (var item in parentMenu.Items) {
                if (item is MenuItem mi) {
                    mi.IsChecked = false;
                }
            }
        }
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
