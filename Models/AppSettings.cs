namespace SyncLightBridge.Models;

public class AppSettings {
    public int UdpPort { get; set; } = 19446;
    public int LedCount { get; set; } = 65;
    public AppMode SelectedMode { get; set; } = AppMode.HyperHDRSync;
    public bool StartWithWindows { get; set; } = true;
}
