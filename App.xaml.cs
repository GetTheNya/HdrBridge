using System.Windows;
using System.Linq;
using SyncLightBridge.Services;
using SyncLightBridge.ViewModels;

namespace SyncLightBridge;

public partial class App : Application {
    public static UsbController UsbController { get; private set; } = null!;
    public static UdpListener UdpListener { get; private set; } = null!;
    public static SettingsService SettingsService { get; private set; } = null!;
    public static EffectManager EffectManager { get; private set; } = null!;
    public static HyperHdrService HyperHdrService { get; private set; } = null!;
    public static MainViewModel MainViewModel { get; private set; } = null!;

    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        SettingsService = new SettingsService();
        UsbController = new UsbController(SettingsService.CurrentSettings.LedCount);
        EffectManager = new EffectManager(UsbController);
        UdpListener = new UdpListener(UsbController, SettingsService);
        HyperHdrService = new HyperHdrService(SettingsService);
        MainViewModel = new MainViewModel(UsbController, UdpListener, SettingsService, EffectManager, HyperHdrService);

        UdpListener.Start();
        _ = MainViewModel.ApplyCurrentModeAsync();

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

    protected override void OnExit(ExitEventArgs e) {
        UdpListener?.Stop();
        
        try {
            HyperHdrService?.SetSyncStateAsync(false).GetAwaiter().GetResult();
        } catch { }

        UsbController?.Dispose();
        base.OnExit(e);
    }
}
