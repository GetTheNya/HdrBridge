using System.Windows;
using HdrBridge.Services;
using HdrBridge.ViewModels;

namespace HdrBridge;

public partial class App : Application {
    public static bool AllowMainWindowClose { get; set; }

    public static UsbController UsbController { get; private set; } = null!;
    public static UdpListener UdpListener { get; private set; } = null!;
    public static SettingsService SettingsService { get; private set; } = null!;
    public static EffectManager EffectManager { get; private set; } = null!;
    public static HyperHdrService HyperHdrService { get; private set; } = null!;
    public static MainViewModel MainViewModel { get; private set; } = null!;
    public static NotificationService NotificationService { get; private set; } = null!;
    public static SystemPowerService SystemPowerService { get; private set; } = null!;

    private MainWindow? _mainWindow;
    private UsbDeviceWatcherService? _usbDeviceWatcher;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        NotificationService = new NotificationService();

        SettingsService = new SettingsService();
        UsbController = new UsbController(SettingsService.CurrentSettings.LedCount);
        EffectManager = new EffectManager(UsbController);
        UdpListener = new UdpListener(UsbController, SettingsService);
        HyperHdrService = new HyperHdrService(SettingsService);
        SystemPowerService = new SystemPowerService();
        MainViewModel = new MainViewModel(UsbController, UdpListener, SettingsService, EffectManager, HyperHdrService, NotificationService, SystemPowerService);

        _usbDeviceWatcher = new UsbDeviceWatcherService(0x1A86, 0xFE07,
            onInserted: () => Current.Dispatcher.Invoke(() => UsbController.TryConnect()),
            onRemoved: () => Current.Dispatcher.Invoke(() => UdpListener.Stop()));

        UdpListener.Start();
        _ = MainViewModel.ApplyCurrentModeAsync();

        _mainWindow = new MainWindow {
            DataContext = MainViewModel
        };

        _mainWindow.Closing += (s, args) => {
            if (AllowMainWindowClose)
                return;
            args.Cancel = true;
            _mainWindow.Hide();
        };

        if (!e.Args.Contains("-autostart")) {
            _mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e) {
        UdpListener?.Stop();
        
        try {
            if (MainViewModel.IsHyperHdrServerReachable) {
                HyperHdrService.SetSyncStateAsync(false).ConfigureAwait(false).GetAwaiter().GetResult();
            }
        } catch { }

        _usbDeviceWatcher?.Dispose();
        MainViewModel?.Cleanup();
        UsbController?.SendPowerCommandAsync(false);
        UsbController?.Dispose();
        base.OnExit(e);
    }
}
