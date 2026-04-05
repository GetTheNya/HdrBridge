using System.Diagnostics;
using System.Threading.Channels;
using HidApi;

namespace HdrBridge.Services;

public class UsbController : IDisposable {
    private const ushort VendorId = 0x1A86;
    private const ushort ProductId = 0xFE07;
    private Device? _device;
    /// <summary>OS path of the open device; used with <see cref="Hid.Enumerate"/> to detect unplug without polling <c>IsConnected</c>.</summary>
    private string? _devicePath;
    private readonly byte[] _readBuffer = new byte[65];
    private int _ledCount;
    private byte _currentSeq = 0x01;

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    // ── Connection state ────────────────────────────────────────────────────────
    // Cached flag updated ONLY by MonitorLoopAsync every 2 s.
    // All hot-path code reads this bool instead of HIDAPI enumerate on the hot path.
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
    private readonly AutoResetEvent _frameReadyEvent = new(false);

    private byte[]? _lastSentFrame = null;
    /// <summary>Latest frame to send; only written from <see cref="SenderLoopAsync"/>, read from <see cref="UsbSenderThread"/> after <see cref="_frameReadyEvent"/>.</summary>
    private volatile byte[]? _latestFrame; 

    public event EventHandler<bool>?              ConnectionChanged;
    public event EventHandler<RemoteButtonAction>? RemoteButtonPressed;

    /// <summary>Cached connection state. Updated every 2 s by MonitorLoopAsync.</summary>
    public bool IsConnected => _isConnectedFlag;
    public bool IsHardwareOverridden { get; set; } = false;

    public UsbController(int ledCount) {
        _ledCount = ledCount;

        try {
            Hid.Init();
        }
        catch (HidException ex) {
            Debug.WriteLine($"Hid.Init: {ex.Message}");
        }

        _ = Task.Run(SenderLoopAsync);
        _ = Task.Run(MonitorLoopAsync);
        _ = Task.Run(ReadLoopAsync);

        var usbThread = new Thread(UsbSenderThread) {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        usbThread.Start();

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
                bool deviceLost = false;
                if (_device != null && _devicePath != null && _isConnectedFlag) {
                    try {
                        deviceLost = !Hid.Enumerate(VendorId, ProductId).Any(d =>
                            d.Path.Equals(_devicePath, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception ex) {
                        // Do not tear down on a transient enumerate failure (would stop auto-reconnect forever).
                        Debug.WriteLine($"Monitor Hid.Enumerate: {ex.Message}");
                    }
                }

                if (deviceLost && _isConnectedFlag) {
                    _isConnectedFlag = false;
                    Debug.WriteLine("Device disconnected.");
                    ConnectionChanged?.Invoke(this, false);
                    _device?.Dispose();
                    _device = null;
                    _devicePath = null;
                }

                if (_device == null) {
                    TryConnect();
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

                int n = _device.ReadTimeout(_readBuffer, 500);
                if (n <= 0)
                    continue;

                int offset = (n == 65) ? 1 : 0;

                // Check packet signature: 52 42 08 00 F1 00
                if (n >= offset + 25 &&
                    _readBuffer[offset + 0] == 0x52 &&
                    _readBuffer[offset + 1] == 0x42 &&
                    _readBuffer[offset + 2] == 0x08 &&
                    _readBuffer[offset + 3] == 0x00 &&
                    _readBuffer[offset + 4] == 0xF1 &&
                    _readBuffer[offset + 5] == 0x00) {

                    if (_readBuffer[offset + 16] == 0x04) {
                        RemoteButtonPressed?.Invoke(this, RemoteButtonAction.PowerToggle);
                    } else if (_readBuffer[offset + 16] == 0x41 && _readBuffer[offset + 24] == 0x32) {
                        RemoteButtonPressed?.Invoke(this, RemoteButtonAction.StaticColorForce);
                    } else if (_readBuffer[offset + 16] == 0x41 && _readBuffer[offset + 24] == 0x5A) {
                        RemoteButtonPressed?.Invoke(this, RemoteButtonAction.DynamicEffectForce);
                    } else {
                        RemoteButtonPressed?.Invoke(this, RemoteButtonAction.UnknownOverrideForce);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"Read error: {ex.Message}"); }
    }

    // ── Sender loop ───────────────────────────────────────────────────────────────
    private async Task SenderLoopAsync() {
        while (await _frameChannel.Reader.WaitToReadAsync(_cancellationTokenSource.Token)) {
            byte[]? frame = null;
            while (_frameChannel.Reader.TryRead(out var f))
                frame = f;

            if (frame == null || !_isConnectedFlag || IsHardwareOverridden)
                continue;

            if (_lastSentFrame != null && frame.SequenceEqual(_lastSentFrame))
                continue;

            _latestFrame = frame;
            _lastSentFrame = frame;
            _frameReadyEvent.Set();
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────────

    public void TryConnect() {
        try {
            List<DeviceInfo> list;
            try {
                list = Hid.Enumerate(VendorId, ProductId).ToList();
            }
            catch (Exception ex) {
                Debug.WriteLine($"TryConnect Hid.Enumerate: {ex.Message}");
                return;
            }

            var info = SelectDeviceInfo(list);
            if (info == null) {
                Debug.WriteLine("TryConnect: no device for VID/PID (enumeration empty).");
                return;
            }

            _device?.Dispose();
            _device = info.ConnectToDevice();
            _devicePath = info.Path;
            InitializeDevice();
            _isConnectedFlag = true;
            ConnectionChanged?.Invoke(this, true);
        }
        catch (Exception ex) {
            Debug.WriteLine($"TryConnect: {ex.Message}");
            _device?.Dispose();
            _device = null;
            _devicePath = null;
        }
    }

    /// <summary>
    /// Prefer the same interface as the old HidLibrary filter (composite device, first HID interface).
    /// HIDAPI path strings vary by OS/build; if <c>mi_00</c> is missing, use interface number or first match.
    /// </summary>
    private static DeviceInfo? SelectDeviceInfo(IReadOnlyList<DeviceInfo> devices) {
        if (devices.Count == 0) return null;

        var byPath = devices.FirstOrDefault(d => d.Path.Contains("mi_00", StringComparison.OrdinalIgnoreCase));
        if (byPath != null) return byPath;

        var iface0 = devices.FirstOrDefault(d => d.InterfaceNumber == 0);
        if (iface0 != null) return iface0;

        Debug.WriteLine($"SelectDeviceInfo: using first of {devices.Count} HID paths (no mi_00 / iface 0).");
        return devices[0];
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

        lock (_sendLock) {
            if (_device == null) return;
            byte[] report = new byte[65];
            report[0] = 0x00;
            Array.Copy(payload, 0, report, 1, Math.Min(payload.Length, 64));
            _device.Write(report);
        }
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

        lock (_sendLock) {
            if (_device == null) return;
            byte[] report1 = new byte[65];
            report1[0] = 0x00;
            Array.Copy(pkt1, 0, report1, 1, 64);
            _device.Write(report1);
        }

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

        lock (_sendLock) {
            if (_device == null) return;
            byte[] report2 = new byte[65];
            report2[0] = 0x00;
            Array.Copy(pkt2, 0, report2, 1, 64);
            _device.Write(report2);
        }

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

        lock (_sendLock) {
            if (_device == null) return;
            _device.Write(init1);
            Thread.Sleep(50);
            _device.Write(init2);
        }
        Debug.WriteLine("Initialization success!");
    }

    /// <summary>
    /// Dedicated thread: blocks on <see cref="_frameReadyEvent"/> (no busy-wait) until a frame is ready or shutdown.
    /// </summary>
    private void UsbSenderThread() {
        var token = _cancellationTokenSource.Token;
        var handles = new WaitHandle[] { _frameReadyEvent, token.WaitHandle };

        while (true) {
            // 0 = frame signal, 1 = dispose / cancellation — avoids blocking forever after Dispose
            int signaled = WaitHandle.WaitAny(handles);
            if (signaled == 1 || token.IsCancellationRequested)
                return;

            var frameToSend = _latestFrame;
            if (frameToSend == null || !_isConnectedFlag || IsHardwareOverridden)
                continue;

            SendRawFrameInternal(frameToSend);

            Thread.Sleep(16);
        }
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
        _frameReadyEvent.Set(); // unblock UsbSenderThread if waiting
        _frameChannel.Writer.TryComplete();
        _device?.Dispose();
        _device = null;
        _devicePath = null;
        try {
            Hid.Exit();
        }
        catch (HidException ex) {
            Debug.WriteLine($"Hid.Exit: {ex.Message}");
        }
    }
}

public enum RemoteButtonAction {
    PowerToggle,
    StaticColorForce,
    DynamicEffectForce,
    UnknownOverrideForce
}
