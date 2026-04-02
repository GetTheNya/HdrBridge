using SyncLightBridge.Models;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SyncLightBridge.Services;

public class UdpListener : IDisposable {
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly UsbController _usbController;
    private readonly SettingsService _settingsService;

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
                    ProcessUdpPacket(receiveResult.Buffer);
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

    private void ProcessUdpPacket(byte[] data) {
        var ledCount = _settingsService.CurrentSettings.LedCount;
        byte[] rgbData = new byte[ledCount * 3];

        // Copy received RGB sequence, leave remaining as zero.
        int bytesToCopy = Math.Min(data.Length, rgbData.Length);
        Array.Copy(data, rgbData, bytesToCopy);

        _usbController.EnqueueRawFrame(rgbData);
    }

    public void Dispose() {
        Stop();
    }
}
