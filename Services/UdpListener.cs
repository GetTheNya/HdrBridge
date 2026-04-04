using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HdrBridge.Models;

namespace HdrBridge.Services;

public class UdpListener : IDisposable {
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly UsbController _usbController;
    private readonly SettingsService _settingsService;

    // Reusable receive buffer — avoids allocating a new byte[] per packet (up to 60Hz).
    // Max HyperHDR UDP payload: header(4) + 300 LEDs * 3 bytes = 904 bytes. 4096 is a safe upper bound.
    private byte[] _receiveBuffer = new byte[4096];

    // Throttle: forward a frame to the USB controller at most once every 100ms (10Hz cap on processing).
    private readonly Stopwatch _frameThrottle = Stopwatch.StartNew();
    private const long FrameThrottleMs = 16; // ~60fps max — let SenderLoop's 33ms TryTake be the real cap

    public event EventHandler<bool>? ListenerStateChanged;

    public bool IsListening => _udpClient != null;

    public UdpListener(UsbController usbController, SettingsService settingsService) {
        _usbController = usbController;
        _settingsService = settingsService;
    }

    public void Start() {
        if (IsListening) return;

        try {
            var port = _settingsService.CurrentSettings.UdpPort;
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _cancellationTokenSource = new CancellationTokenSource();

            _ = Task.Run(ReceiveLoopAsync, _cancellationTokenSource.Token);
            ListenerStateChanged?.Invoke(this, true);
        }
        catch (Exception ex) {
            Debug.WriteLine($"Failed to start UDP listener: {ex.Message}");
            ListenerStateChanged?.Invoke(this, false);
        }
    }

    public void Stop() {
        _cancellationTokenSource?.Cancel();
        _udpClient?.Close();
        _udpClient = null;
        ListenerStateChanged?.Invoke(this, false);
    }

    private async Task ReceiveLoopAsync() {
        try {
            while (_cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested && _udpClient != null) {
                var receiveResult = await _udpClient.ReceiveAsync(_cancellationTokenSource.Token);

                if (_settingsService.CurrentSettings.SelectedMode == AppMode.HyperHDRSync) {
                    ProcessUdpPacket(receiveResult.Buffer.AsMemory());
                }
            }
        }
        catch (OperationCanceledException) {
        }
        catch (Exception ex) {
            Debug.WriteLine($"UDP Receive error: {ex.Message}");
            Stop();
        }
    }

    private void ProcessUdpPacket(ReadOnlyMemory<byte> data) {
        // Throttle: drop frames that arrive too fast to keep CPU load minimal.
        if (_frameThrottle.ElapsedMilliseconds < FrameThrottleMs) return;
        _frameThrottle.Restart();

        var ledCount = _settingsService.CurrentSettings.LedCount;
        int needed = ledCount * 3;

        // Grow the reusable buffer only if needed (rare: LED count changed).
        if (_receiveBuffer.Length < needed)
            _receiveBuffer = new byte[needed];

        // Zero out any portion beyond what the packet covers, then copy.
        var span = _receiveBuffer.AsSpan(0, needed);
        int bytesToCopy = Math.Min(data.Length, needed);
        data.Span[..bytesToCopy].CopyTo(span);
        if (bytesToCopy < needed)
            span[bytesToCopy..].Clear();

        _usbController.EnqueueRawFrame(_receiveBuffer, needed);
    }

    public void Dispose() {
        Stop();
    }
}
