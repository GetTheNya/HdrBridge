using System;
using System.Collections.ObjectModel;
using SyncLightBridge.Models;

namespace SyncLightBridge.Services;

public class EffectManager {
    private readonly UsbController _usbController;

    public ObservableCollection<HardwareEffect> AvailableEffects { get; } = new() {
        new HardwareEffect { Name = "Rainbow Cycle", EffectId = 0x00, Category = EffectCategory.Dynamic },
        new HardwareEffect { Name = "Breathing Pulse", EffectId = 0x01, Category = EffectCategory.Dynamic },
        new HardwareEffect { Name = "Sequential Color Wipe", EffectId = 0x02, Category = EffectCategory.Dynamic },
        new HardwareEffect { Name = "Twin Color Spin", EffectId = 0x03, Category = EffectCategory.Dynamic },
        new HardwareEffect { Name = "Dual Split Fill", EffectId = 0x04, Category = EffectCategory.Dynamic },
        new HardwareEffect { Name = "Comet Trail", EffectId = 0x05, Category = EffectCategory.Dynamic },
        new HardwareEffect { Name = "Tinted Scanner", EffectId = 0x06, Category = EffectCategory.Dynamic },
        new HardwareEffect { Name = "Drip Accumulation", EffectId = 0x07, Category = EffectCategory.Dynamic },
        new HardwareEffect { Name = "Dynamic VU-Meter", EffectId = 0x00, Category = EffectCategory.Rhythm },
        new HardwareEffect { Name = "Volume Pulse Glow", EffectId = 0x01, Category = EffectCategory.Rhythm },
        new HardwareEffect { Name = "Static VU-Meter", EffectId = 0x02, Category = EffectCategory.Rhythm },
        new HardwareEffect { Name = "Frequency Sparkle", EffectId = 0x03, Category = EffectCategory.Rhythm },
        new HardwareEffect { Name = "Red Embers & Sparks", EffectId = 0x04, Category = EffectCategory.Rhythm },
        new HardwareEffect { Name = "Center-Out VU-Meter", EffectId = 0x05, Category = EffectCategory.Rhythm },
        new HardwareEffect { Name = "Mirror Spectrum", EffectId = 0x06, Category = EffectCategory.Rhythm },
        new HardwareEffect { Name = "Sparkling Garland", EffectId = 0x07, Category = EffectCategory.Rhythm }
    };

    public EffectManager(UsbController usbController) {
        _usbController = usbController;
    }

    private byte _effectSequence = 0x00;

    public void SetHardwareEffect(HardwareEffect effect) {
        byte[] payload = new byte[64]; // MUST BE 64 BYTES
        payload[0] = 0x52; // Header R
        payload[1] = 0x42; // Header B
        payload[2] = 0x08; // Command: Start Effect
        payload[3] = _effectSequence; // Sequence ID
        payload[4] = 0x85; // Strict Constant
        payload[5] = effect.Category == EffectCategory.Rhythm ? (byte)0x03 : (byte)0x02;
        payload[6] = effect.EffectId;

        int checksum = 0;
        for (int i = 0; i < 7; i++) {
            checksum += payload[i];
        }
        payload[7] = (byte)(checksum & 0xFF);

        _effectSequence = (byte)((_effectSequence + 1) & 0xFF);

        _usbController.SendHardwareEffect(payload);
    }
    
    public void SetEffectSpeed(HardwareEffect effect, byte speed) {
        byte[] payload = new byte[64];
        payload[0] = 0x52;
        payload[1] = 0x42;
        payload[2] = 0x07; // Command: Adjust Speed
        payload[3] = _effectSequence;
        
        if (effect.Category == EffectCategory.Rhythm) {
            payload[4] = 0x8B;
            payload[5] = speed;
        } else {
            payload[4] = 0x8A;
            payload[5] = (byte)Math.Clamp(100 - speed, 0, 100);
        }

        int checksum = 0;
        for (int i = 0; i < 6; i++) {
            checksum += payload[i];
        }
        payload[6] = (byte)(checksum & 0xFF);

        _effectSequence = (byte)((_effectSequence + 1) & 0xFF);

        _usbController.SendHardwareEffect(payload);
    }


}
