using System.Windows;
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
        TrayIcon?.Dispose();
        Application.Current.Shutdown();
    }
}
