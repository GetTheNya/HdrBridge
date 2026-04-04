namespace HdrBridge.Models;

public class AppSettings {
    public int UdpPort { get; set; } = 19446;
    public int LedCount { get; set; } = 65;
    public AppMode SelectedMode { get; set; } = AppMode.HyperHDRSync;
    public string HyperHdrApiUrl { get; set; } = "http://localhost:8090/json-rpc";
    public bool StartWithWindows { get; set; } = true;
    public int HyperHdrBrightness { get; set; } = 100;
    public byte StaticColorR { get; set; } = 255;
    public byte StaticColorG { get; set; } = 255;
    public byte StaticColorB { get; set; } = 255;
    public byte HardwareEffectSpeed { get; set; } = 127;
    public byte SelectedEffectId { get; set; } = 0;
    public bool AutoOffOnLockSleep { get; set; } = true;
}
