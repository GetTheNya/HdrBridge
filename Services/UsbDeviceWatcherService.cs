using System.Diagnostics;
using System.Management;

namespace SyncLightBridge.Services;

/// <summary>
/// WMI: USB PnP arrival/removal for a specific VID/PID (e.g. HID controller).
/// </summary>
public sealed class UsbDeviceWatcherService : IDisposable {
    private readonly int _vendorId;
    private readonly int _productId;
    private readonly Action _onInserted;
    private readonly Action _onRemoved;
    private ManagementEventWatcher? _insertWatcher;
    private ManagementEventWatcher? _removeWatcher;

    public UsbDeviceWatcherService(int vendorId, int productId, Action onInserted, Action onRemoved) {
        _vendorId = vendorId;
        _productId = productId;
        _onInserted = onInserted;
        _onRemoved = onRemoved;

        try {
            _insertWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'"));
            _insertWatcher.EventArrived += OnInsertArrived;
            _insertWatcher.Start();

            _removeWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'"));
            _removeWatcher.EventArrived += OnRemoveArrived;
            _removeWatcher.Start();
        } catch (Exception ex) {
            Debug.WriteLine($"USB WMI watcher failed to start: {ex.Message}");
            Dispose();
        }
    }

    private void OnInsertArrived(object sender, EventArrivedEventArgs e) {
        if (TryGetMatchingDeviceId(e, out _))
            _onInserted();
    }

    private void OnRemoveArrived(object sender, EventArrivedEventArgs e) {
        if (TryGetMatchingDeviceId(e, out _))
            _onRemoved();
    }

    private bool TryGetMatchingDeviceId(EventArrivedEventArgs e, out string? deviceId) {
        deviceId = null;
        try {
            if (e.NewEvent["TargetInstance"] is not ManagementBaseObject target)
                return false;
            deviceId = target["DeviceID"] as string;
            return !string.IsNullOrEmpty(deviceId) && MatchesVidPid(deviceId);
        } catch {
            return false;
        }
    }

    private bool MatchesVidPid(string deviceId) {
        string vid = $"VID_{_vendorId:X4}";
        string pid = $"PID_{_productId:X4}";
        return deviceId.Contains(vid, StringComparison.OrdinalIgnoreCase)
            && deviceId.Contains(pid, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() {
        try {
            _insertWatcher?.Stop();
            _insertWatcher?.Dispose();
            _insertWatcher = null;
            _removeWatcher?.Stop();
            _removeWatcher?.Dispose();
            _removeWatcher = null;
        } catch {
            // ignored
        }
    }
}
