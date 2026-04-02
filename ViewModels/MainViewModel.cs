using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncLightBridge.Models;
using SyncLightBridge.Services;
using System.Collections.ObjectModel;

namespace SyncLightBridge.ViewModels;

public partial class MainViewModel : ObservableObject {
    private readonly UsbController _usbController;
    private readonly UdpListener _udpListener;
    public SettingsService SettingsService { get; }
    private readonly EffectManager _effectManager;

    [ObservableProperty] private bool _isUsbConnected;
    [ObservableProperty] private bool _isUdpListening;
    [ObservableProperty] private HardwareEffect? _selectedHardwareEffect;
    [ObservableProperty] private bool _isServicesEnabled = true;
    [ObservableProperty] private string _servicesStatusString = "🟢 System Active";

    [ObservableProperty] private byte _staticColorR = 255;
    [ObservableProperty] private byte _staticColorG = 255;
    [ObservableProperty] private byte _staticColorB = 255;
    [ObservableProperty] private byte _hardwareEffectSpeed = 127;
    [ObservableProperty] private string _lastSentEffectId = "None";
    
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

    public ObservableCollection<HardwareEffect> AvailableEffects { get; }

    public AppMode CurrentMode {
        get => SettingsService.CurrentSettings.SelectedMode;
        set {
            if (SettingsService.CurrentSettings.SelectedMode != value) {
                SettingsService.CurrentSettings.SelectedMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentModeString));
                ApplyCurrentMode();
            }
        }
    }

    public string CurrentModeString {
        get {
            if (!IsServicesEnabled) return "⚫ System Disabled";
            return SettingsService.CurrentSettings.SelectedMode switch {
                AppMode.HyperHDRSync => "🟢 Mode: HyperHDR Sync Active",
                AppMode.StaticColor => "🔵 Mode: Static Color Active",
                AppMode.HardwareEffect => "🟣 Mode: Hardware Effect Active",
                _ => "Unknown"
            };
        }
    }

    public MainViewModel(
        UsbController usbController,
        UdpListener udpListener,
        SettingsService settingsService,
        EffectManager effectManager) {
        _usbController = usbController;
        _udpListener = udpListener;
        SettingsService = settingsService;
        _effectManager = effectManager;

        AvailableEffects = new ObservableCollection<HardwareEffect>(_effectManager.AvailableEffects);
        SelectedHardwareEffect = AvailableEffects.FirstOrDefault();

        _usbController.ConnectionChanged += (s, connected) => { System.Windows.Application.Current?.Dispatcher?.Invoke(() => IsUsbConnected = connected); };

        _udpListener.ListenerStateChanged += (s, listening) => { System.Windows.Application.Current?.Dispatcher?.Invoke(() => IsUdpListening = listening); };

        _usbController.PhysicalButtonPressed += (s, e) => {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() => {
                ToggleServices();
            });
        };

        _staticColorTimer = new System.Timers.Timer(150); // ~6.6 FPS
        _staticColorTimer.Elapsed += (s, e) => {
            if (CurrentMode == AppMode.StaticColor && IsServicesEnabled) {
                SendStaticColorFrame();
            }
        };
        _staticColorTimer.Start();

        IsUsbConnected = _usbController.IsConnected;
        IsUdpListening = _udpListener.IsListening;
    }

    [RelayCommand]
    public void ActivateHyperHDR() {
        SettingsService.CurrentSettings.SelectedMode = AppMode.HyperHDRSync;
        ApplyCurrentMode();
        OnPropertyChanged(nameof(CurrentModeString));
    }

    [RelayCommand]
    public void ActivateHardwareMode() {
        SettingsService.CurrentSettings.SelectedMode = AppMode.HardwareEffect;
        ApplyCurrentMode();
        OnPropertyChanged(nameof(CurrentModeString));
    }

    [RelayCommand]
    public void ToggleServices() {
        IsServicesEnabled = !IsServicesEnabled;
        _usbController.IsHardwareOverridden = !IsServicesEnabled;
        ServicesStatusString = IsServicesEnabled ? "🟢 System Active" : "🔴 System Offline";
        
        if (!IsServicesEnabled) {
            _usbController.SendBlackFrame();
        }
        
        ApplyCurrentMode();
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
    

    public void ApplyCurrentMode() {
        if (!IsServicesEnabled) {
            _udpListener.Stop();
            _usbController.SendBlackFrame();
            OnPropertyChanged(nameof(CurrentModeString));
            return;
        }

        var mode = SettingsService.CurrentSettings.SelectedMode;
        if (mode == AppMode.HyperHDRSync) {
            _udpListener.Start();
        } else {
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

    partial void OnSelectedHardwareEffectChanged(HardwareEffect? value) {
        OnPropertyChanged(nameof(SliderLabel));
        if (SettingsService.CurrentSettings.SelectedMode == AppMode.HardwareEffect) {
            ApplyCurrentMode();
        }
    }

    [RelayCommand]
    public void SaveSettings() {
        SettingsService.SaveSettings();
        _usbController.UpdateLedCount(SettingsService.CurrentSettings.LedCount);

        _udpListener.Stop();
        _udpListener.Start();
    }
}
