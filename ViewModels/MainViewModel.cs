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

    public ObservableCollection<HardwareEffect> AvailableEffects { get; }

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

    public void ApplyCurrentMode() {
        if (!IsServicesEnabled) {
            _udpListener.Stop();
            _effectManager.SetStaticColor(0, 0, 0); // Turn LEDs off
            OnPropertyChanged(nameof(CurrentModeString));
            return;
        }

        var mode = SettingsService.CurrentSettings.SelectedMode;
        if (mode == AppMode.HyperHDRSync) {
            _udpListener.Start();
        } else {
            _udpListener.Stop();
            if (mode == AppMode.StaticColor) {
                _effectManager.SetStaticColor(255, 255, 255);
            } else if (mode == AppMode.HardwareEffect && SelectedHardwareEffect != null) {
                _effectManager.SetHardwareEffect(SelectedHardwareEffect.EffectId);
            }
        }

        SettingsService.SaveSettings();
        OnPropertyChanged(nameof(CurrentModeString));
    }

    partial void OnSelectedHardwareEffectChanged(HardwareEffect? value) {
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
