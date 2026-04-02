using SyncLightBridge.Models;

namespace SyncLightBridge.Services;

public class EffectManager {
    private readonly UsbController _usbController;

    public List<HardwareEffect> AvailableEffects { get; } = new List<HardwareEffect> {
        new HardwareEffect { EffectId = 0x01, Name = "Rainbow" },
        new HardwareEffect { EffectId = 0x02, Name = "Breathing" },
        new HardwareEffect { EffectId = 0x03, Name = "Static Color" }
    };

    public EffectManager(UsbController usbController) {
        _usbController = usbController;
    }

    public void SetHardwareEffect(byte effectId, byte speed) {
        byte[] payload = new byte[16];
        payload[0] = 0x52;
        payload[1] = 0x42;
        payload[2] = 0x03;
        payload[3] = effectId;
        payload[4] = speed;

        _usbController.SendHardwareEffect(payload);
    }

    public void SetStaticColor(byte r, byte g, byte b) {
        byte[] payload = new byte[16];
        payload[0] = 0x52;
        payload[1] = 0x42;
        payload[2] = 0x03;
        payload[3] = 0x03; // Assuming 0x03 is static color
        payload[4] = 0x00; // Speed 0 for static
        payload[5] = r;
        payload[6] = g;
        payload[7] = b;

        _usbController.SendHardwareEffect(payload);
    }
}
