namespace HdrBridge.Models;

public class AppSettings {
    public int UdpPort { get; set; } = 19446;
    public int LedCount { get; set; } = 65;
    public AppMode SelectedMode { get; set; } = AppMode.HyperHDRSync;
    public string HyperHdrApiUrl { get; set; } = "http://localhost:8090/json-rpc";
    public bool StartWithWindows { get; set; } = true;
}
