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
    public bool IsConnected => _device != null && _device.IsConnected;

    public UsbController(int ledCount) {
        _ledCount = ledCount;
        _frameQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 2);

        _cancellationTokenSource = new CancellationTokenSource();

        _ = Task.Run(SenderLoopAsync, _cancellationTokenSource.Token);
        _ = Task.Run(MonitorLoopAsync, _cancellationTokenSource.Token);

        TryConnect();
    }

    public void UpdateLedCount(int ledCount) {
        _ledCount = ledCount;
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
        if (!IsConnected) return;

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

    public void Dispose() {
        _cancellationTokenSource.Cancel();
        _device?.Dispose();
    }
}
