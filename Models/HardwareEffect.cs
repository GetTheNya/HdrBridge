namespace SyncLightBridge.Models;

public enum AppMode {
    HyperHDRSync,
    StaticColor,
    HardwareEffect
}

public class HardwareEffect {
    public byte EffectId { get; set; }
    public string Name { get; set; } = string.Empty;
}
