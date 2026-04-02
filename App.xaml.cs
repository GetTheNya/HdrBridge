using System.Windows;
using SyncLightBridge.Services;
using SyncLightBridge.ViewModels;
using H.NotifyIcon;

namespace SyncLightBridge;

public partial class App : Application {
    public static UsbController UsbController { get; private set; } = null!;
    public static UdpListener UdpListener { get; private set; } = null!;
    public static SettingsService SettingsService { get; private set; } = null!;
    public static EffectManager EffectManager { get; private set; } = null!;
    public static MainViewModel MainViewModel { get; private set; } = null!;

    private TaskbarIcon? _taskbarIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        SettingsService = new SettingsService();
        UsbController = new UsbController(SettingsService.CurrentSettings.LedCount);
        EffectManager = new EffectManager(UsbController);
        UdpListener = new UdpListener(UsbController, SettingsService);
        MainViewModel = new MainViewModel(UsbController, UdpListener, SettingsService, EffectManager);

        // Create Tray Icon
        _taskbarIcon = (TaskbarIcon)FindResource("TrayIcon");

        try {
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var ctx = visual.RenderOpen()) {
                ctx.DrawRoundedRectangle(System.Windows.Media.Brushes.DodgerBlue, null, new Rect(0, 0, 16, 16), 4, 4);
                // Draw a simple white dot in the center to look a bit like a light
                ctx.DrawEllipse(System.Windows.Media.Brushes.White, null, new Point(8, 8), 3, 3);
            }
            bmp.Render(visual);
            if (_taskbarIcon != null) _taskbarIcon.IconSource = bmp;
        } catch { }

        UdpListener.Start();
        MainViewModel.ApplyCurrentMode();

        _mainWindow = new MainWindow {
            DataContext = MainViewModel
        };

        _mainWindow.Closing += (s, args) => {
            args.Cancel = true;
            _mainWindow.Hide();
        };

        if (!e.Args.Contains("-autostart")) {
            _mainWindow.Show();
        }
    }

    private void TaskbarIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) {
        _mainWindow?.Show();
        if (_mainWindow?.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow?.Activate();
    }

    private void MenuItem_Open_Click(object sender, RoutedEventArgs e) {
        _mainWindow?.Show();
        if (_mainWindow?.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow?.Activate();
    }

    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e) {
        _taskbarIcon?.Dispose();
        UdpListener.Stop();
        UsbController.Dispose();
        Current.Shutdown();
    }
}
