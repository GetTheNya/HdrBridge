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

    private byte _effectSequence = 0x00;

    public void SetHardwareEffect(byte effectId) {
        byte[] payload = new byte[8];
        payload[0] = 0x52; // Header R
        payload[1] = 0x42; // Header B
        payload[2] = 0x08; // Command: Start Effect
        payload[3] = _effectSequence; // Sequence ID
        payload[4] = 0x85; // Constant
        payload[5] = 0x02; // Constant
        payload[6] = effectId;

        // Checksum calculation (Sum of bytes 0 through 6)
        int checksum = 0;
        for (int i = 0; i < 7; i++) {
            checksum += payload[i];
        }
        payload[7] = (byte)(checksum & 0xFF);

        _effectSequence = (byte)((_effectSequence + 1) & 0xFF);

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
