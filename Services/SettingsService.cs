using Microsoft.Win32;
using SyncLightBridge.Models;
using System.IO;
using System.Text.Json;

namespace SyncLightBridge.Services;

public class SettingsService {
    private const string AppName = "SyncLightBridge";
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _settingsFilePath;

    public AppSettings CurrentSettings { get; private set; } = new AppSettings();

    public SettingsService() {
        var appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        Directory.CreateDirectory(appDataFolder);
        _settingsFilePath = Path.Combine(appDataFolder, "settings.json");

        LoadSettings();
        ApplyStartupSetting();
    }

    public void LoadSettings() {
        if (File.Exists(_settingsFilePath)) {
            try {
                var json = File.ReadAllText(_settingsFilePath);
                CurrentSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch {
                CurrentSettings = new AppSettings();
            }
        } else {
            CurrentSettings = new AppSettings();
        }
    }

    public void SaveSettings() {
        try {
            var json = JsonSerializer.Serialize(CurrentSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
            ApplyStartupSetting();
        }
        catch {
        }
    }

    private void ApplyStartupSetting() {
        if (!OperatingSystem.IsWindows()) return;

        try {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key != null) {
                if (CurrentSettings.StartWithWindows) {
                    string exePath = Environment.ProcessPath ?? string.Empty;
                    key.SetValue(AppName, $"\"{exePath}\" -autostart");
                } else {
                    key.DeleteValue(AppName, false);
                }
            }
        }
        catch {
        }
    }
}
