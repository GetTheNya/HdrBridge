using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HdrBridge.Models;
using HdrBridge.Services;

namespace HdrBridge.ViewModels;

public partial class MainViewModel : ObservableObject {
    private readonly UsbController _usbController;
    private readonly UdpListener _udpListener;
    private readonly NotificationService _notifications;
    public SettingsService SettingsService { get; }
    private readonly EffectManager _effectManager;
    private readonly HyperHdrService _hyperHdrService;
    private readonly SystemPowerService _systemPowerService;
    private readonly BitmapFrame _trayIconActive;
    private readonly BitmapFrame _trayIconPaused;
    private bool _isSuspendedBySystem;

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
    [ObservableProperty] private int _hyperHdrMaxBrightness = 100;

    public string SyncToggleMenuHeader => IsServicesEnabled ? "Disable Sync" : "Enable Sync";
    public string AppVersion {
        get {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(version)) return "v1.0.0-dev";
            if (version.Contains('+')) version = version.Split('+')[0]; // Remove git hash suffix
            return version.StartsWith("v") ? version : "v" + version;
        }
    }

    /// <summary>
    /// Label shown on the big toggle button.
    /// Three states: no controller → "Connect Strip", active → "Disable Sync", paused → "Enable Sync".
    /// </summary>
    public string ServicesButtonLabel =>
        !IsUsbConnected ? "Connect Strip" :
        IsServicesEnabled ? "Disable Sync" : "Enable Sync";

    public bool IsManualMode => !IsServicesEnabled;
    public string TrayToolTipText => IsServicesEnabled ? "HdrBridge — Syncing" : "HdrBridge — Paused";
    public BitmapFrame TrayIconSource => IsServicesEnabled ? _trayIconActive : _trayIconPaused;

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

    private bool _pendingHyperHdrEnable;
    private CancellationTokenSource? _brightnessDebounceCts;

    // Pre-allocated static color frame buffer — rebuilt only when LED count changes.
    private byte[] _staticColorFrame;
    private int _staticColorLedCount;

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
            if (!IsUsbConnected) return "No Controller";
            if (!IsServicesEnabled) return "System Disabled";
            return SettingsService.CurrentSettings.SelectedMode switch {
                AppMode.HyperHDRSync    => "Mode: HyperHDR Sync Active",
                AppMode.StaticColor     => "Mode: Static Color Active",
                AppMode.HardwareEffect  => "Mode: Hardware Effect Active",
                _                       => "Unknown"
            };
        }
    }

    public string CurrentModeColor {
        get {
            if (!IsUsbConnected) return "#E51400"; // Red — no strip
            if (!IsServicesEnabled) return "#888888";
            return SettingsService.CurrentSettings.SelectedMode switch {
                AppMode.HyperHDRSync   => "#60A917", // Green
                AppMode.StaticColor    => "#0050EF", // Blue
                AppMode.HardwareEffect => "#AA00FF", // Purple
                _                      => "#888888"
            };
        }
    }

    public MainViewModel(
        UsbController usbController,
        UdpListener udpListener,
        SettingsService settingsService,
        EffectManager effectManager,
        HyperHdrService hyperHdrService,
        NotificationService notificationService,
        SystemPowerService systemPowerService) {
        _usbController = usbController;
        _udpListener = udpListener;
        SettingsService = settingsService;
        _effectManager = effectManager;
        _hyperHdrService = hyperHdrService;
        _notifications = notificationService;
        _systemPowerService = systemPowerService;

        _trayIconActive = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/app.ico"));
        _trayIconPaused = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/tray_paused.ico"));

        AvailableEffects = new ObservableCollection<HardwareEffect>(_effectManager.AvailableEffects);
        SelectedHardwareEffect = AvailableEffects.FirstOrDefault(e => e.EffectId == SettingsService.CurrentSettings.SelectedEffectId)
                                 ?? AvailableEffects.FirstOrDefault();

        _usbController.ConnectionChanged += (s, connected) => {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() => OnUsbConnectionChanged(connected));
        };

        _udpListener.ListenerStateChanged += (s, listening) => { System.Windows.Application.Current?.Dispatcher?.Invoke<bool>(() => IsUdpListening = listening); };

        _usbController.RemoteButtonPressed += (s, action) => {
            System.Windows.Application.Current?.Dispatcher?.Invoke((Action)(() => {
                if (action == RemoteButtonAction.PowerToggle) {
                    _ = RemotePowerToggleWithToastAsync();
                } else if (action == RemoteButtonAction.StaticColorForce) {
                    if (IsServicesEnabled) _ = ToggleServices();
                    ServicesStatusString = "Manual: Static Color";
                    ServicesStatusColor = "#E51400";
                    _notifications.Show("HdrBridge", "Ambilight: static color");
                } else if (action == RemoteButtonAction.DynamicEffectForce) {
                    if (IsServicesEnabled) _ = ToggleServices();
                    ServicesStatusString = "Manual: Music Mode";
                    ServicesStatusColor = "#E51400";
                    _notifications.Show("HdrBridge", "Ambilight: music mode");
                } else if (action == RemoteButtonAction.UnknownOverrideForce) {
                    if (IsServicesEnabled) _ = ToggleServices();
                    ServicesStatusString = "Manual: Remote Override";
                    ServicesStatusColor = "#E51400";
                    _notifications.Show("HdrBridge", "Ambilight: remote override");
                }
            }));
        };


        var _hyperHdrHeartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _hyperHdrHeartbeatTimer.Tick += async (_, _) => await OnHyperHdrHeartbeatAsync();
        _hyperHdrHeartbeatTimer.Start();
        _ = OnHyperHdrHeartbeatAsync();

        IsUsbConnected = _usbController.IsConnected;
        IsUdpListening = _udpListener.IsListening;
        HyperHdrMaxBrightness = SettingsService.CurrentSettings.HyperHdrBrightness;
        StaticColorR = SettingsService.CurrentSettings.StaticColorR;
        StaticColorG = SettingsService.CurrentSettings.StaticColorG;
        StaticColorB = SettingsService.CurrentSettings.StaticColorB;
        HardwareEffectSpeed = SettingsService.CurrentSettings.HardwareEffectSpeed;

        // If no strip is connected at launch, start in a fully disabled state.
        // Services will be re-enabled automatically by OnUsbConnectionChanged when the strip is plugged in.
        if (!IsUsbConnected) {
            IsServicesEnabled = false;
            _usbController.IsHardwareOverridden = true;
            ServicesStatusString = "No Controller";
            ServicesStatusColor  = "#E51400";
        }

        // Initialize the static color frame buffer.
        _staticColorLedCount = SettingsService.CurrentSettings.LedCount;
        _staticColorFrame = new byte[_staticColorLedCount * 3];
        RebuildStaticColorFrame();

        _systemPowerService.SystemSuspendChanged += (_, suspended) => {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() => {
                if (suspended)
                    _ = SystemSuspendAsync();
                else
                    _ = SystemResumeAsync();
            });
        };
        _systemPowerService.Start();
    }

    private DateTime _lastConnectionToast = DateTime.MinValue;
    private bool _lastConnectionState;

    private async Task OnUsbDisconnectedAsync() {
        // Stop all active services and notify HyperHDR immediately.
        _udpListener.Stop();
        _pendingHyperHdrEnable = false;
        if (IsHyperHdrServerReachable)
            await _hyperHdrService.SetSyncStateAsync(false);

        // Mark system as disabled so UI reflects the no-strip state.
        if (IsServicesEnabled) {
            IsServicesEnabled = false;
            _usbController.IsHardwareOverridden = true;
            ServicesStatusString = "No Controller";
            ServicesStatusColor  = "#E51400";
        }

        _notifications.Show("HdrBridge", "Controller disconnected — sync stopped");
        OnPropertyChanged(nameof(CurrentModeString));
    }

    private void OnUsbConnectionChanged(bool connected) {
        IsUsbConnected = connected;

        // Debounce: USB devices often fire multiple connection events during enumeration.
        var now = DateTime.UtcNow;
        bool stateChanged = connected != _lastConnectionState;
        if (!stateChanged && (now - _lastConnectionToast).TotalSeconds < 2)
            return;

        _lastConnectionState = connected;
        _lastConnectionToast = now;

        if (connected) {
            // Re-enable services automatically when controller comes back.
            IsServicesEnabled = true;
            _usbController.IsHardwareOverridden = false;
            ServicesStatusString = "System Active";
            ServicesStatusColor  = "#60A917";
            _notifications.Show("HdrBridge", "Controller connected");
            _ = ApplyCurrentModeAsync();
        } else {
            _ = OnUsbDisconnectedAsync();
        }
    }

    private async Task RemotePowerToggleWithToastAsync() {
        await ToggleServices();
        _notifications.Show("HdrBridge", IsServicesEnabled ? "Ambilight resumed" : "Ambilight paused");
    }

    partial void OnIsServicesEnabledChanged(bool value) {
        OnPropertyChanged(nameof(SyncToggleMenuHeader));
        OnPropertyChanged(nameof(TrayToolTipText));
        OnPropertyChanged(nameof(TrayIconSource));
        OnPropertyChanged(nameof(IsManualMode));
        OnPropertyChanged(nameof(ServicesButtonLabel));
        OnPropertyChanged(nameof(CurrentModeString));
        OnPropertyChanged(nameof(CurrentModeColor));
    }

    partial void OnIsUsbConnectedChanged(bool value) {
        // Notify every property that changes meaning when hardware presence changes.
        OnPropertyChanged(nameof(ServicesButtonLabel));
        OnPropertyChanged(nameof(CurrentModeString));
        OnPropertyChanged(nameof(CurrentModeColor));
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
        // Cannot enable sync without a connected controller.
        if (!IsUsbConnected && !IsServicesEnabled) {
            MessageBox.Show("Please connect the LED strip first.", "HdrBridge");
            return;
        }

        IsServicesEnabled = !IsServicesEnabled;
        _usbController.IsHardwareOverridden = !IsServicesEnabled;
        ServicesStatusString = IsServicesEnabled ? "System Active" : "System Offline";
        ServicesStatusColor  = IsServicesEnabled ? "#60A917"      : "#E51400";

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
        SettingsService.CurrentSettings.HardwareEffectSpeed = value;
        SettingsService.SaveSettings();
    }



    partial void OnHyperHdrMaxBrightnessChanged(int value) {
        _brightnessDebounceCts?.Cancel();
        _brightnessDebounceCts = new CancellationTokenSource();
        var token = _brightnessDebounceCts.Token;
        _ = Task.Run(async () => {
            try {
                await Task.Delay(300, token);
                if (IsHyperHdrServerReachable)
                    await _hyperHdrService.SetBrightnessAsync(value);
                SettingsService.CurrentSettings.HyperHdrBrightness = value;
                SettingsService.SaveSettings();
            } catch (TaskCanceledException) { /* debounce: superseded by newer value */ }
        });
    }

    partial void OnStaticColorRChanged(byte value) { OnPropertyChanged(nameof(StaticColorMedia)); SaveStaticColor(); }
    partial void OnStaticColorGChanged(byte value) { OnPropertyChanged(nameof(StaticColorMedia)); SaveStaticColor(); }
    partial void OnStaticColorBChanged(byte value) { OnPropertyChanged(nameof(StaticColorMedia)); SaveStaticColor(); }

    private void SaveStaticColor() {
        SettingsService.CurrentSettings.StaticColorR = StaticColorR;
        SettingsService.CurrentSettings.StaticColorG = StaticColorG;
        SettingsService.CurrentSettings.StaticColorB = StaticColorB;
        SettingsService.SaveSettings();
        RebuildStaticColorFrame();
        // Push update immediately — no polling timer, so we must send here.
        if (CurrentMode == AppMode.StaticColor && IsServicesEnabled)
            SendStaticColorFrame();
    }

    /// <summary>Fills the shared static color buffer with the current RGB values. Call whenever R/G/B changes.</summary>
    private void RebuildStaticColorFrame() {
        int count = SettingsService.CurrentSettings.LedCount;
        // Reallocate only if LED count changed (rare).
        if (_staticColorLedCount != count || _staticColorFrame.Length != count * 3) {
            _staticColorLedCount = count;
            _staticColorFrame = new byte[count * 3];
        }
        for (int i = 0; i < count; i++) {
            _staticColorFrame[i * 3]     = StaticColorR;
            _staticColorFrame[i * 3 + 1] = StaticColorG;
            _staticColorFrame[i * 3 + 2] = StaticColorB;
        }
    }

    private void SendStaticColorFrame() {
        // Use the pre-built shared buffer; no allocation on the hot path.
        _usbController.EnqueueRawFrame(_staticColorFrame, _staticColorFrame.Length);
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
        // No hardware present — ensure everything is off and bail out.
        if (!IsUsbConnected) {
            _udpListener.Stop();
            _pendingHyperHdrEnable = false;
            if (IsHyperHdrServerReachable)
                await _hyperHdrService.SetSyncStateAsync(false);
            OnPropertyChanged(nameof(CurrentModeString));
            return;
        }

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
                await _hyperHdrService.SetBrightnessAsync(HyperHdrMaxBrightness);
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
            await _hyperHdrService.SetBrightnessAsync(HyperHdrMaxBrightness);
            _pendingHyperHdrEnable = false;
            _udpListener.Start();
        }
    }

    partial void OnSelectedHardwareEffectChanged(HardwareEffect? value) {
        OnPropertyChanged(nameof(SliderLabel));
        if (value != null) {
            SettingsService.CurrentSettings.SelectedEffectId = value.EffectId;
            SettingsService.SaveSettings();
        }
        if (SettingsService.CurrentSettings.SelectedMode == AppMode.HardwareEffect) {
            _ = ApplyCurrentModeAsync();
        }
    }

    [RelayCommand]
    public void SaveSettings() {
        SettingsService.SaveSettings();
        _usbController.UpdateLedCount(SettingsService.CurrentSettings.LedCount);
        RebuildStaticColorFrame(); // LED count may have changed

        _udpListener.Stop();
        _ = ApplyCurrentModeAsync();
    }

    // --- Stealth Mode: System suspend/resume ---

    private async Task SystemSuspendAsync() {
        if (!SettingsService.CurrentSettings.AutoOffOnLockSleep) return;
        if (!IsServicesEnabled) return; // already off, nothing to do
        if (_isSuspendedBySystem) return; // already suspended

        _isSuspendedBySystem = true;
        Debug.WriteLine("Stealth: suspending LEDs (lock/monitor off)");

        if (IsHyperHdrServerReachable)
            await _hyperHdrService.SetSyncStateAsync(false);
        _udpListener.Stop();
        await _usbController.SendPowerCommandAsync(false);
    }

    private async Task SystemResumeAsync() {
        if (!_isSuspendedBySystem) return; // we didn't suspend, don't resume
        _isSuspendedBySystem = false;
        Debug.WriteLine("Stealth: resuming LEDs (unlock/monitor on)");

        await _usbController.SendPowerCommandAsync(true, StaticColorR, StaticColorG, StaticColorB);
        await ApplyCurrentModeAsync();
    }

    public void Cleanup() {
        _systemPowerService.Dispose();
    }
}
