using HidLibrary;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SyncLightBridge.Services;

public class UsbController : IDisposable {
    private const int VendorId = 0x1A86;
    private const int ProductId = 0xFE07;
    private HidDevice? _device;
    private int _ledCount;
    private byte _currentSeq = 0x01;

    private CancellationTokenSource _cancellationTokenSource;
    private readonly BlockingCollection<byte[]> _frameQueue;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<RemoteButtonAction>? RemoteButtonPressed;
    public bool IsConnected => _device != null && _device.IsConnected;
    public bool IsHardwareOverridden { get; set; } = false;

    public UsbController(int ledCount) {
        _ledCount = ledCount;
        _frameQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 2);

        _cancellationTokenSource = new CancellationTokenSource();

        _ = Task.Run(SenderLoopAsync, _cancellationTokenSource.Token);
        _ = Task.Run(MonitorLoopAsync, _cancellationTokenSource.Token);
        _ = Task.Run(ReadLoopAsync, _cancellationTokenSource.Token);

        TryConnect();
    }

    public void UpdateLedCount(int ledCount) {
        _ledCount = ledCount;
    }

    private async Task ReadLoopAsync() {
        try {
            while (!_cancellationTokenSource.Token.IsCancellationRequested) {
                if (_device != null && _device.IsConnected) {
                    // Using synchronous read with timeout inside Task.Run avoids PlatformNotSupportedException for async I/O
                    var report = _device.ReadReport(500); 
                    if (report.ReadStatus == HidDeviceData.ReadStatus.Success) {
                        byte[] data = report.Data;
                        // Remove leading Report ID byte dynamically if present (HidLibrary usually strips it, but some forks don't, check index offsets if needed)
                        // Assuming data contains the payload directly starting at index 0 or 1. If 64 bytes, index 0 might be Report ID 0x00. Let's gracefully check both offsets.
                        int offset = (data.Length == 65) ? 1 : 0;
                        
                        // Check packet signature: 52 42 08 00 F1 00
                        if (data.Length >= offset + 25 && 
                            data[offset + 0] == 0x52 && 
                            data[offset + 1] == 0x42 && 
                            data[offset + 2] == 0x08 && 
                            data[offset + 3] == 0x00 && 
                            data[offset + 4] == 0xF1 && 
                            data[offset + 5] == 0x00) {
                            
                            if (data[offset + 16] == 0x04) {
                                RemoteButtonPressed?.Invoke(this, RemoteButtonAction.PowerToggle);
                            } else {
                                if (data[offset + 16] == 0x41 && data[offset + 24] == 0x32) {
                                    RemoteButtonPressed?.Invoke(this, RemoteButtonAction.StaticColorForce);
                                } else if (data[offset + 16] == 0x41 && data[offset + 24] == 0x5A) {
                                    RemoteButtonPressed?.Invoke(this, RemoteButtonAction.DynamicEffectForce);
                                } else {
                                    RemoteButtonPressed?.Invoke(this, RemoteButtonAction.UnknownOverrideForce);
                                }
                            }
                        }
                    }
                } else {
                    await Task.Delay(500, _cancellationTokenSource.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"Read error: {ex.Message}"); }
    }

    private async Task MonitorLoopAsync() {
        try {
            while (!_cancellationTokenSource.Token.IsCancellationRequested) {
                if (_device != null && !_device.IsConnected) {
                    Debug.WriteLine("Device disconnected.");
                    ConnectionChanged?.Invoke(this, false);
                    _device.Dispose();
                    _device = null;
                }

                if (_device == null) {
                    TryConnect();
                }

                await Task.Delay(2000, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) {
        }
    }

    private void TryConnect() {
        try {
            var device = HidDevices.Enumerate(VendorId, ProductId)
                .FirstOrDefault(d => d.DevicePath.Contains("mi_00", StringComparison.OrdinalIgnoreCase));

            if (device != null && device.IsConnected) {
                _device = device;
                InitializeDevice();
                ConnectionChanged?.Invoke(this, true);
            }
        }
        catch (Exception) {
        }
    }

    private void InitializeDevice() {
        if (_device == null) return;

        byte[] init1 = new byte[65];
        byte[] init1Data = Convert.FromHexString("52420602821e");
        init1[0] = 0x00; // Report ID
        Array.Copy(init1Data, 0, init1, 1, init1Data.Length);

        byte[] init2 = new byte[65];
        byte[] init2Data = Convert.FromHexString("52420703930031");
        init2[0] = 0x00;
        Array.Copy(init2Data, 0, init2, 1, init2Data.Length);

        _device.Write(init1);
        Thread.Sleep(50);
        _device.Write(init2);
        Debug.WriteLine("Initialization success!");
    }

    public void SendHardwareEffect(byte[] payload) {
        if (!IsConnected || _device == null) return;

        byte[] report = new byte[65];
        report[0] = 0x00;
        Array.Copy(payload, 0, report, 1, Math.Min(payload.Length, 64));

        _device.Write(report);
        Debug.WriteLine("Sent hardware effect");
    }

    public void EnqueueRawFrame(byte[] rgbData) {
        if (!IsConnected || IsHardwareOverridden) return;

        while (_frameQueue.Count > 0) {
            _frameQueue.TryTake(out _);
        }

        try {
            _frameQueue.TryAdd(rgbData);
        }
        catch {
        }
    }

    private async Task SenderLoopAsync() {
        try {
            while (!_cancellationTokenSource.Token.IsCancellationRequested) {
                if (_frameQueue.TryTake(out byte[]? rgbData, 33, _cancellationTokenSource.Token)) {
                    if (IsHardwareOverridden) continue;
                    if (rgbData != null)
                        SendRawFrameInternal(rgbData);
                }
            }
        }
        catch (OperationCanceledException) {
        }
    }

    private void SendRawFrameInternal(byte[] colorsArray) {
        if (_device == null || !_device.IsConnected) return;

        try {
            int packetLen = 2 + 2 + 1 + (_ledCount * 5) + 1 + 1;
            byte[] pkt = new byte[packetLen];

            pkt[0] = 0x53; // Header SC
            pkt[1] = 0x43;
            pkt[2] = (byte)((packetLen >> 8) & 0xFF);
            pkt[3] = (byte)(packetLen & 0xFF);
            pkt[4] = _currentSeq;

            int offset = 5;
            for (int i = 0; i < _ledCount; i++) {
                pkt[offset++] = (byte)(i == 0 ? (i | 0x80) : i); // Start
                pkt[offset++] = (byte)i; // End

                int colorIdx = i * 3;
                if (colorIdx + 2 < colorsArray.Length) {
                    pkt[offset++] = colorsArray[colorIdx]; // r
                    pkt[offset++] = colorsArray[colorIdx + 1]; // g
                    pkt[offset++] = colorsArray[colorIdx + 2]; // b
                } else {
                    pkt[offset++] = 0;
                    pkt[offset++] = 0;
                    pkt[offset++] = 0;
                }
            }

            pkt[offset++] = (byte)_ledCount; // Commit byte

            long sum = 0;
            for (int k = 0; k < offset; k++) sum += pkt[k];
            pkt[offset] = (byte)(sum & 0xFF);

            for (int chunkStart = 0; chunkStart < packetLen; chunkStart += 64) {
                byte[] usbReport = new byte[65];
                usbReport[0] = 0x00; // Report ID
                int copyLen = Math.Min(64, packetLen - chunkStart);
                Array.Copy(pkt, chunkStart, usbReport, 1, copyLen);
                _device.Write(usbReport);
            }

            _currentSeq = (byte)((_currentSeq + 1) & 0xFF);
            if (_currentSeq == 0) _currentSeq = 1;
        }
        catch (Exception ex) {
            Debug.WriteLine($"USB error: {ex.Message}");
        }
    }

    public void SendBlackFrame() {
        byte[] black = new byte[_ledCount * 3];
        SendRawFrameInternal(black);
    }

    public async Task SendPowerCommandAsync(bool turnOn, byte r = 255, byte g = 255, byte b = 255) {
        if (!IsConnected || _device == null) return;

        if (!turnOn) {
            r = 0; g = 0; b = 0;
        }

        // Packet 1: Handshake
        byte[] pkt1 = new byte[64];
        pkt1[0] = 0x52;
        pkt1[1] = 0x42;
        pkt1[2] = 0x06;
        pkt1[3] = _currentSeq;
        pkt1[4] = 0x97;
        
        int crc1 = 0;
        for (int i = 0; i <= 4; i++) crc1 += pkt1[i];
        pkt1[5] = (byte)(crc1 & 0xFF);
        
        _currentSeq = (byte)((_currentSeq + 1) & 0xFF);
        if (_currentSeq == 0) _currentSeq = 1;

        byte[] report1 = new byte[65];
        report1[0] = 0x00;
        Array.Copy(pkt1, 0, report1, 1, 64);
        _device.Write(report1);

        await Task.Delay(50); // Delay for hardware processing

        // Packet 2: State Change
        byte[] pkt2 = new byte[64];
        pkt2[0] = 0x52;
        pkt2[1] = 0x42;
        pkt2[2] = 0x10;
        pkt2[3] = _currentSeq;
        pkt2[4] = 0x86;
        pkt2[5] = 0x01;
        pkt2[6] = r;
        pkt2[7] = g;
        pkt2[8] = b;
        pkt2[9] = 0x41;
        pkt2[10] = 0x42;
        pkt2[11] = 0x00;
        pkt2[12] = 0x00;
        pkt2[13] = 0x00;
        pkt2[14] = 0xFE;
        
        int crc2 = 0;
        for (int i = 0; i <= 14; i++) crc2 += pkt2[i];
        pkt2[15] = (byte)(crc2 & 0xFF);
        
        _currentSeq = (byte)((_currentSeq + 1) & 0xFF);
        if (_currentSeq == 0) _currentSeq = 1;

        byte[] report2 = new byte[65];
        report2[0] = 0x00;
        Array.Copy(pkt2, 0, report2, 1, 64);
        _device.Write(report2);
        
        Debug.WriteLine($"Sent power command: {(turnOn ? "ON" : "OFF")}");
    }

    public void Dispose() {
        _cancellationTokenSource.Cancel();
        _device?.Dispose();
    }
}

public enum RemoteButtonAction {
    PowerToggle,
    StaticColorForce,
    DynamicEffectForce,
    UnknownOverrideForce
}
