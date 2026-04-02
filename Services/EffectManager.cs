using SyncLightBridge.Models;

namespace SyncLightBridge.Services;

public class EffectManager {
    private readonly UsbController _usbController;

    public List<HardwareEffect> AvailableEffects { get; } = new() {
        new HardwareEffect { Name = "Rainbow Cycle", EffectId = 0x00 },
        new HardwareEffect { Name = "Breathing Pulse", EffectId = 0x01 },
        new HardwareEffect { Name = "Sequential Color Wipe", EffectId = 0x02 },
        new HardwareEffect { Name = "Twin Color Spin", EffectId = 0x03 },
        new HardwareEffect { Name = "Dual Split Fill", EffectId = 0x04 },
        new HardwareEffect { Name = "Comet Trail", EffectId = 0x05 },
        new HardwareEffect { Name = "Tinted Scanner", EffectId = 0x06 },
        new HardwareEffect { Name = "Drip Accumulation", EffectId = 0x07 }
    };

    public EffectManager(UsbController usbController) {
        _usbController = usbController;
    }

    private byte _effectSequence = 0x00;

    public void SetHardwareEffect(byte effectId) {
        byte[] payload = new byte[64]; // MUST BE 64 BYTES
        payload[0] = 0x52; // Header R
        payload[1] = 0x42; // Header B
        payload[2] = 0x08; // Command: Start Effect
        payload[3] = _effectSequence; // Sequence ID
        payload[4] = 0x85; // Strict Constant
        payload[5] = 0x02; // Strict Constant
        payload[6] = effectId;

        int checksum = 0;
        for (int i = 0; i < 7; i++) {
            checksum += payload[i];
        }
        payload[7] = (byte)(checksum & 0xFF);

        _effectSequence = (byte)((_effectSequence + 1) & 0xFF);

        _usbController.SendHardwareEffect(payload);
    }
    
    public void SetEffectSpeed(byte speed) {
        // Hardware treats value as a Delay (0 = Fast, 100 = Slow)
        // We invert our UI speed (0 = Slow, 100 = Fast) before sending it to the controller
        byte invertedSpeed = (byte)Math.Clamp(100 - speed, 0, 100);

        byte[] payload = new byte[64];
        payload[0] = 0x52;
        payload[1] = 0x42;
        payload[2] = 0x07; // Command: Adjust Speed
        payload[3] = _effectSequence;
        payload[4] = 0x8A; // Constant magic byte for speed
        payload[5] = invertedSpeed;

        int checksum = 0;
        for (int i = 0; i < 6; i++) {
            checksum += payload[i];
        }
        payload[6] = (byte)(checksum & 0xFF);

        _effectSequence = (byte)((_effectSequence + 1) & 0xFF);

        _usbController.SendHardwareEffect(payload);
    }


}
