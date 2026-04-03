using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncLightBridge.Models;
using SyncLightBridge.Services;
using System;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SyncLightBridge.ViewModels;

public partial class MainViewModel : ObservableObject {
    private readonly UsbController _usbController;
    private readonly UdpListener _udpListener;
    private readonly NotificationService _notifications;
    public SettingsService SettingsService { get; }
    private readonly EffectManager _effectManager;
    private readonly HyperHdrService _hyperHdrService;
    private readonly BitmapFrame _trayIconActive;
    private readonly BitmapFrame _trayIconPaused;

    [ObservableProperty] private bool _isUsbConnected;
    [ObservableProperty] private bool _isUdpListening;
    [ObservableProperty] private HardwareEffect? _selectedHardwareEffect;
    [ObservableProperty] private bool _isServicesEnabled = true;
    [ObservableProperty] private string _servicesStatusString = "System Active";
    [ObservableProperty] private string _servicesStatusColor = "#60A917";

    [ObservableProperty] private byte _staticColorR = 255;
    [ObservableProperty] private byte _staticColorG = 255;
    [ObservableProperty] private byte _staticColorB = 255;
    [ObservableProperty] private byte _hardwareEffectSpeed = 127;
    [ObservableProperty] private string _lastSentEffectId = "None";
    [ObservableProperty] private bool _isHyperHdrServerReachable;

    public string SyncToggleMenuHeader => IsServicesEnabled ? "Disable Sync" : "Enable Sync";
    public string TrayToolTipText => IsServicesEnabled ? "SyncLight Bridge — Syncing" : "SyncLight Bridge — Paused";
    public BitmapFrame TrayIconSource => IsServicesEnabled ? _trayIconActive : _trayIconPaused;
    public bool IsManualMode => !IsServicesEnabled;

    public string SliderLabel => SelectedHardwareEffect?.Category == EffectCategory.Rhythm ? "Microphone Sensitivity" : "Effect Speed / Intensity";

    public System.Windows.Media.Color StaticColorMedia {
        get => System.Windows.Media.Color.FromRgb(StaticColorR, StaticColorG, StaticColorB);
        set {
            if (value.R != StaticColorR || value.G != StaticColorG || value.B != StaticColorB) {
                StaticColorR = value.R;
                StaticColorG = value.G;
                StaticColorB = value.B;
                OnPropertyChanged(nameof(StaticColorMedia));
            }
        }
    }

    private readonly System.Timers.Timer _staticColorTimer;
    private readonly DispatcherTimer _hyperHdrHeartbeatTimer;
    private bool _pendingHyperHdrEnable;

    public ObservableCollection<HardwareEffect> AvailableEffects { get; }

    public AppMode CurrentMode {
        get => SettingsService.CurrentSettings.SelectedMode;
        set {
            if (SettingsService.CurrentSettings.SelectedMode != value) {
                SettingsService.CurrentSettings.SelectedMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentModeString));
                _ = ApplyCurrentModeAsync();
            }
        }
    }

    public string CurrentModeString {
        get {
            if (!IsServicesEnabled) return "System Disabled";
            return SettingsService.CurrentSettings.SelectedMode switch {
                AppMode.HyperHDRSync => "Mode: HyperHDR Sync Active",
                AppMode.StaticColor => "Mode: Static Color Active",
                AppMode.HardwareEffect => "Mode: Hardware Effect Active",
                _ => "Unknown"
            };
        }
    }

    public string CurrentModeColor {
        get {
            if (!IsServicesEnabled) return "#888888";
            return SettingsService.CurrentSettings.SelectedMode switch {
                AppMode.HyperHDRSync => "#60A917", // Green
                AppMode.StaticColor => "#0050EF", // Blue
                AppMode.HardwareEffect => "#AA00FF", // Purple
                _ => "#888888"
            };
        }
    }

    public MainViewModel(
        UsbController usbController,
        UdpListener udpListener,
        SettingsService settingsService,
        EffectManager effectManager,
        HyperHdrService hyperHdrService,
        NotificationService notificationService) {
        _usbController = usbController;
        _udpListener = udpListener;
        SettingsService = settingsService;
        _effectManager = effectManager;
        _hyperHdrService = hyperHdrService;
        _notifications = notificationService;

        _trayIconActive = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/app.ico"));
        _trayIconPaused = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/tray_paused.ico"));

        AvailableEffects = new ObservableCollection<HardwareEffect>(_effectManager.AvailableEffects);
        SelectedHardwareEffect = AvailableEffects.FirstOrDefault();

        _usbController.ConnectionChanged += (s, connected) => {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() => OnUsbConnectionChanged(connected));
        };

        _udpListener.ListenerStateChanged += (s, listening) => { System.Windows.Application.Current?.Dispatcher?.Invoke(() => IsUdpListening = listening); };

        _usbController.RemoteButtonPressed += (s, action) => {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() => {
                if (action == RemoteButtonAction.PowerToggle) {
                    _ = RemotePowerToggleWithToastAsync();
                } else if (action == RemoteButtonAction.StaticColorForce) {
                    if (IsServicesEnabled) _ = ToggleServices();
                    ServicesStatusString = "Manual: Static Color";
                    ServicesStatusColor = "#E51400";
                    _notifications.Show("SyncLight Bridge", "Ambilight: static color");
                } else if (action == RemoteButtonAction.DynamicEffectForce) {
                    if (IsServicesEnabled) _ = ToggleServices();
                    ServicesStatusString = "Manual: Music Mode";
                    ServicesStatusColor = "#E51400";
                    _notifications.Show("SyncLight Bridge", "Ambilight: music mode");
                } else if (action == RemoteButtonAction.UnknownOverrideForce) {
                    if (IsServicesEnabled) _ = ToggleServices();
                    ServicesStatusString = "Manual: Remote Override";
                    ServicesStatusColor = "#E51400";
                    _notifications.Show("SyncLight Bridge", "Ambilight: remote override");
                }
            });
        };

        _staticColorTimer = new System.Timers.Timer(150); // ~6.6 FPS
        _staticColorTimer.Elapsed += (s, e) => {
            if (CurrentMode == AppMode.StaticColor && IsServicesEnabled) {
                SendStaticColorFrame();
            }
        };
        _staticColorTimer.Start();

        _hyperHdrHeartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _hyperHdrHeartbeatTimer.Tick += async (_, _) => await OnHyperHdrHeartbeatAsync();
        _hyperHdrHeartbeatTimer.Start();
        _ = OnHyperHdrHeartbeatAsync();

        IsUsbConnected = _usbController.IsConnected;
        IsUdpListening = _udpListener.IsListening;
    }

    private void OnUsbConnectionChanged(bool connected) {
        IsUsbConnected = connected;
        if (connected) {
            _notifications.Show("SyncLight Bridge", "Controller connected");
            _ = ApplyCurrentModeAsync();
        } else {
            _udpListener.Stop();
            _notifications.Show("SyncLight Bridge", "Controller disconnected");
        }
    }

    private async Task RemotePowerToggleWithToastAsync() {
        await ToggleServices();
        _notifications.Show("SyncLight Bridge", IsServicesEnabled ? "Ambilight resumed" : "Ambilight paused");
    }

    partial void OnIsServicesEnabledChanged(bool value) {
        OnPropertyChanged(nameof(SyncToggleMenuHeader));
        OnPropertyChanged(nameof(TrayToolTipText));
        OnPropertyChanged(nameof(TrayIconSource));
        OnPropertyChanged(nameof(IsManualMode));
    }

    public void SaveStartupPreference() {
        SettingsService.SaveSettings();
    }

    [RelayCommand]
    public async Task ActivateHyperHDR() {
        SettingsService.CurrentSettings.SelectedMode = AppMode.HyperHDRSync;
        await ApplyCurrentModeAsync();
        OnPropertyChanged(nameof(CurrentModeString));
    }

    [RelayCommand]
    public async Task ActivateHardwareMode() {
        SettingsService.CurrentSettings.SelectedMode = AppMode.HardwareEffect;
        await ApplyCurrentModeAsync();
        OnPropertyChanged(nameof(CurrentModeString));
    }

    [RelayCommand]
    public async Task ToggleServices() {
        IsServicesEnabled = !IsServicesEnabled;
        _usbController.IsHardwareOverridden = !IsServicesEnabled;
        ServicesStatusString = IsServicesEnabled ? "System Active" : "System Offline";
        ServicesStatusColor = IsServicesEnabled ? "#60A917" : "#E51400";
        
        if (!IsServicesEnabled) {
            await _usbController.SendPowerCommandAsync(false);
        } else {
            await _usbController.SendPowerCommandAsync(true, StaticColorR, StaticColorG, StaticColorB);
        }
        
        await ApplyCurrentModeAsync();
    }

    partial void OnHardwareEffectSpeedChanged(byte value) {
        if (CurrentMode == AppMode.HardwareEffect && IsServicesEnabled && SelectedHardwareEffect != null) {
             _effectManager.SetEffectSpeed(SelectedHardwareEffect, value);
        }
    }



    partial void OnStaticColorRChanged(byte value) { OnPropertyChanged(nameof(StaticColorMedia)); }
    partial void OnStaticColorGChanged(byte value) { OnPropertyChanged(nameof(StaticColorMedia)); }
    partial void OnStaticColorBChanged(byte value) { OnPropertyChanged(nameof(StaticColorMedia)); }

    private void SendStaticColorFrame() {
        int count = SettingsService.CurrentSettings.LedCount;
        byte[] frame = new byte[count * 3];
        for(int i=0; i < count; i++) {
            frame[i*3] = StaticColorR;
            frame[(i*3)+1] = StaticColorG;
            frame[(i*3)+2] = StaticColorB;
        }
        _usbController.EnqueueRawFrame(frame);
    }
    
    [RelayCommand]
    public void SetPredefinedColor(string colorName) {
        if (colorName == "Red") { StaticColorR = 255; StaticColorG = 0; StaticColorB = 0; }
        else if (colorName == "Green") { StaticColorR = 0; StaticColorG = 255; StaticColorB = 0; }
        else if (colorName == "Blue") { StaticColorR = 0; StaticColorG = 0; StaticColorB = 255; }
        else if (colorName == "Yellow") { StaticColorR = 255; StaticColorG = 255; StaticColorB = 0; }
        else if (colorName == "Purple") { StaticColorR = 128; StaticColorG = 0; StaticColorB = 128; }
        else if (colorName == "Cyan") { StaticColorR = 0; StaticColorG = 255; StaticColorB = 255; }
        else if (colorName == "White") { StaticColorR = 255; StaticColorG = 255; StaticColorB = 255; }
        else if (colorName == "Orange") { StaticColorR = 255; StaticColorG = 140; StaticColorB = 0; }
        
        if (CurrentMode == AppMode.StaticColor && IsServicesEnabled) {
            SendStaticColorFrame();
        }
    }
    

    public async Task ApplyCurrentModeAsync() {
        if (!IsServicesEnabled) {
            _udpListener.Stop();
            _pendingHyperHdrEnable = false;
            if (IsHyperHdrServerReachable)
                await _hyperHdrService.SetSyncStateAsync(false);
            await _usbController.SendPowerCommandAsync(false);
            OnPropertyChanged(nameof(CurrentModeString));
            return;
        }

        var mode = SettingsService.CurrentSettings.SelectedMode;
        if (mode == AppMode.HyperHDRSync) {
            if (IsHyperHdrServerReachable) {
                await _hyperHdrService.SetSyncStateAsync(true);
                _pendingHyperHdrEnable = false;
            } else {
                _pendingHyperHdrEnable = true;
            }
            _udpListener.Start();
        } else {
            _pendingHyperHdrEnable = false;
            if (IsHyperHdrServerReachable)
                await _hyperHdrService.SetSyncStateAsync(false);
            _udpListener.Stop();
            if (mode == AppMode.StaticColor) {
                SendStaticColorFrame();
            } else if (mode == AppMode.HardwareEffect && SelectedHardwareEffect != null) {
                _effectManager.SetHardwareEffect(SelectedHardwareEffect);
            }
        }

        SettingsService.SaveSettings();
        OnPropertyChanged(nameof(CurrentModeString));
    }

    private async Task OnHyperHdrHeartbeatAsync() {
        bool available = await _hyperHdrService.IsServerAvailableAsync();
        IsHyperHdrServerReachable = available;

        if (!IsServicesEnabled || CurrentMode != AppMode.HyperHDRSync)
            return;

        if (_pendingHyperHdrEnable && available) {
            await _hyperHdrService.SetSyncStateAsync(true);
            _pendingHyperHdrEnable = false;
            _udpListener.Start();
        }
    }

    partial void OnSelectedHardwareEffectChanged(HardwareEffect? value) {
        OnPropertyChanged(nameof(SliderLabel));
        if (SettingsService.CurrentSettings.SelectedMode == AppMode.HardwareEffect) {
            _ = ApplyCurrentModeAsync();
        }
    }

    [RelayCommand]
    public void SaveSettings() {
        SettingsService.SaveSettings();
        _usbController.UpdateLedCount(SettingsService.CurrentSettings.LedCount);

        _udpListener.Stop();
        _ = ApplyCurrentModeAsync();
    }
}
