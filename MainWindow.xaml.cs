using System;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;

namespace SyncLightBridge;

public partial class MainWindow : Window {
    public MainWindow() {
        InitializeComponent();
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

    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e) {
        // Return first so the tray context menu can close; then exit on the next idle pass.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
            App.AllowMainWindowClose = true;
            TrayIcon?.Dispose();
            Application.Current.Shutdown();
        }));
    }
}
