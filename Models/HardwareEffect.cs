namespace SyncLightBridge.Models;

public enum AppMode {
    HyperHDRSync,
    StaticColor,
    HardwareEffect
}

public enum EffectCategory {
    Dynamic,
    Rhythm
}

public class HardwareEffect {
    public EffectCategory Category { get; set; } = EffectCategory.Dynamic;
    public byte EffectId { get; set; }
    public string Name { get; set; } = string.Empty;
}
