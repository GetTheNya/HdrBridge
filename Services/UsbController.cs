using System.Diagnostics;
using System.Threading.Channels;
using HidLibrary;

namespace HdrBridge.Services;

public class UsbController : IDisposable {
    private const int VendorId = 0x1A86;
    private const int ProductId = 0xFE07;
    private HidDevice? _device;
    private int _ledCount;
    private byte _currentSeq = 0x01;

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    // ── Connection state ────────────────────────────────────────────────────────
    // Cached flag updated ONLY by MonitorLoopAsync every 2 s.
    // All hot-path code reads this bool instead of calling _device.IsConnected,
    // which was measured at 21 % CPU due to kernel transitions.
    private volatile bool _isConnectedFlag;

    // ── Frame channel ───────────────────────────────────────────────────────────
    // Capacity 1 + DropOldest: only the newest frame is kept; no backlog.
    private readonly Channel<byte[]> _frameChannel =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    // ── Pre-allocated send buffers ──────────────────────────────────────────────
    // Max packet: header(5) + ledCount*5 + 2. 2048 covers up to 408 LEDs.
    private byte[] _pktBuffer = new byte[2048];
    private readonly byte[] _usbReport = new byte[65]; // Report-ID + 64 payload bytes
    private readonly object _sendLock = new();

    private byte[] _lastSentFrame = null;

    public event EventHandler<bool>?              ConnectionChanged;
    public event EventHandler<RemoteButtonAction>? RemoteButtonPressed;

    /// <summary>Cached connection state. Updated every 2 s by MonitorLoopAsync.</summary>
    public bool IsConnected => _isConnectedFlag;
    public bool IsHardwareOverridden { get; set; } = false;

    public UsbController(int ledCount) {
        _ledCount = ledCount;

        _ = Task.Run(SenderLoopAsync);
        _ = Task.Run(MonitorLoopAsync);
        _ = Task.Run(ReadLoopAsync);

        TryConnect();
    }

    public void UpdateLedCount(int ledCount) {
        _ledCount = ledCount;
    }

    // ── Monitor loop ─────────────────────────────────────────────────────────────
    // Sole author of _isConnectedFlag. Runs every 2 s — no tight polling.
    private async Task MonitorLoopAsync() {
        try {
            while (!_cancellationTokenSource.Token.IsCancellationRequested) {
                bool physicallyConnected = _device != null && _device.IsConnected;

                if (!physicallyConnected && _isConnectedFlag) {
                    // Device just disconnected.
                    _isConnectedFlag = false;
                    Debug.WriteLine("Device disconnected.");
                    ConnectionChanged?.Invoke(this, false);
                    _device?.Dispose();
                    _device = null;
                }

                if (_device == null) {
                    TryConnect(); // sets _isConnectedFlag = true on success
                }

                await Task.Delay(2000, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Read loop ─────────────────────────────────────────────────────────────────
    // Blocks on a 500 ms synchronous read when connected; sleeps 500 ms otherwise.
    // Uses _isConnectedFlag to avoid calling _device.IsConnected in the hot path.
    private async Task ReadLoopAsync() {
        try {
            while (!_cancellationTokenSource.Token.IsCancellationRequested) {
                if (!_isConnectedFlag || _device == null) {
                    await Task.Delay(500, _cancellationTokenSource.Token);
                    continue;
                }

                // Synchronous read with 500 ms timeout — blocks this background thread only.
                var report = _device.ReadReport(500);
                if (report.ReadStatus == HidDeviceData.ReadStatus.Success) {
                    byte[] data = report.Data;
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
                        } else if (data[offset + 16] == 0x41 && data[offset + 24] == 0x32) {
                            RemoteButtonPressed?.Invoke(this, RemoteButtonAction.StaticColorForce);
                        } else if (data[offset + 16] == 0x41 && data[offset + 24] == 0x5A) {
                            RemoteButtonPressed?.Invoke(this, RemoteButtonAction.DynamicEffectForce);
                        } else {
                            RemoteButtonPressed?.Invoke(this, RemoteButtonAction.UnknownOverrideForce);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"Read error: {ex.Message}"); }
    }

    // ── Sender loop ───────────────────────────────────────────────────────────────
    // Reads only when the channel yields a frame; Delay(33) caps ~30 FPS and yields the HID driver.
    private async Task SenderLoopAsync() {
        try {
            await foreach (var frame in _frameChannel.Reader.ReadAllAsync(_cancellationTokenSource.Token)) {
                if (_lastSentFrame != null && frame.SequenceEqual(_lastSentFrame)) {
                    continue;
                }
                
                if (!IsHardwareOverridden && _isConnectedFlag) {
                    SendRawFrameInternal(frame);
                    _lastSentFrame = frame;
                }
                
                await Task.Delay(33, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Public API ────────────────────────────────────────────────────────────────

    public void TryConnect() {
        try {
            var device = HidDevices.Enumerate(VendorId, ProductId)
                .FirstOrDefault(d => d.DevicePath.Contains("mi_00", StringComparison.OrdinalIgnoreCase));

            if (device != null && device.IsConnected) {
                _device = device;
                InitializeDevice();
                _isConnectedFlag = true;
                ConnectionChanged?.Invoke(this, true);
            }
        }
        catch (Exception) { }
    }

    /// <param name="rgbData">Buffer containing RGB data. May be a shared reusable buffer.</param>
    /// <param name="length">Number of valid bytes in <paramref name="rgbData"/> to use.</param>
    public void EnqueueRawFrame(byte[] rgbData, int length) {
        if (!_isConnectedFlag || IsHardwareOverridden) return;

        var copy = new byte[length];
        Array.Copy(rgbData, copy, length);
        _frameChannel.Writer.TryWrite(copy);
    }

    public void SendHardwareEffect(byte[] payload) {
        if (!_isConnectedFlag || _device == null) return;

        byte[] report = new byte[65];
        report[0] = 0x00;
        Array.Copy(payload, 0, report, 1, Math.Min(payload.Length, 64));

        _device.Write(report);
        Debug.WriteLine("Sent hardware effect");
    }

    public void SendBlackFrame() {
        if (!_isConnectedFlag || IsHardwareOverridden) return;

        int len = _ledCount * 3;
        _frameChannel.Writer.TryWrite(new byte[len]);
    }

    public async Task SendPowerCommandAsync(bool turnOn, byte r = 255, byte g = 255, byte b = 255) {
        if (!_isConnectedFlag || _device == null) return;

        if (!turnOn) { r = 0; g = 0; b = 0; }

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
        _currentSeq = (byte)(_currentSeq == 255 ? 1 : _currentSeq + 1);

        byte[] report1 = new byte[65];
        report1[0] = 0x00;
        Array.Copy(pkt1, 0, report1, 1, 64);
        _device.Write(report1);

        await Task.Delay(50);

        // Packet 2: State Change
        byte[] pkt2 = new byte[64];
        pkt2[0]  = 0x52; pkt2[1]  = 0x42; pkt2[2]  = 0x10;
        pkt2[3]  = _currentSeq;
        pkt2[4]  = 0x86; pkt2[5]  = 0x01;
        pkt2[6]  = r;    pkt2[7]  = g;    pkt2[8]  = b;
        pkt2[9]  = 0x41; pkt2[10] = 0x42; pkt2[11] = 0x00;
        pkt2[12] = 0x00; pkt2[13] = 0x00; pkt2[14] = 0xFE;
        int crc2 = 0;
        for (int i = 0; i <= 14; i++) crc2 += pkt2[i];
        pkt2[15] = (byte)(crc2 & 0xFF);
        _currentSeq = (byte)(_currentSeq == 255 ? 1 : _currentSeq + 1);

        byte[] report2 = new byte[65];
        report2[0] = 0x00;
        Array.Copy(pkt2, 0, report2, 1, 64);
        _device.Write(report2);

        Debug.WriteLine($"Sent power command: {(turnOn ? "ON" : "OFF")}");
    }

    // ── Internal helpers ──────────────────────────────────────────────────────────

    private void InitializeDevice() {
        if (_device == null) return;

        byte[] init1 = new byte[65];
        byte[] init1Data = Convert.FromHexString("52420602821e");
        init1[0] = 0x00;
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

    private void SendRawFrameInternal(byte[] colorsArray) {
        if (_device == null) return;

        int colorsLength = colorsArray.Length;

        lock (_sendLock) {
            try {
                int packetLen = 2 + 2 + 1 + (_ledCount * 5) + 1 + 1;

                if (_pktBuffer.Length < packetLen)
                    _pktBuffer = new byte[packetLen];

                var pkt = _pktBuffer;
                pkt[0] = 0x53; // Header SC
                pkt[1] = 0x43;
                pkt[2] = (byte)((packetLen >> 8) & 0xFF);
                pkt[3] = (byte)(packetLen & 0xFF);
                pkt[4] = _currentSeq;

                int offset = 5;
                for (int i = 0; i < _ledCount; i++) {
                    pkt[offset++] = (byte)(i == 0 ? (i | 0x80) : i);
                    pkt[offset++] = (byte)i;

                    int colorIdx = i * 3;
                    if (colorIdx + 2 < colorsLength) {
                        pkt[offset++] = colorsArray[colorIdx];
                        pkt[offset++] = colorsArray[colorIdx + 1];
                        pkt[offset++] = colorsArray[colorIdx + 2];
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

                // Reuse pre-allocated report buffer for every chunk.
                _usbReport[0] = 0x00;
                for (int chunkStart = 0; chunkStart < packetLen; chunkStart += 64) {
                    int copyLen = Math.Min(64, packetLen - chunkStart);
                    if (copyLen < 64)
                        Array.Clear(_usbReport, 1 + copyLen, 64 - copyLen);
                    Array.Copy(pkt, chunkStart, _usbReport, 1, copyLen);
                    _device.Write(_usbReport);
                }

                _currentSeq = (byte)(_currentSeq == 255 ? 1 : _currentSeq + 1);
            }
            catch (Exception ex) {
                Debug.WriteLine($"USB error: {ex.Message}");
            }
        }
    }

    public void Dispose() {
        _cancellationTokenSource.Cancel();
        _frameChannel.Writer.TryComplete();
        _device?.Dispose();
    }
}

public enum RemoteButtonAction {
    PowerToggle,
    StaticColorForce,
    DynamicEffectForce,
    UnknownOverrideForce
}
