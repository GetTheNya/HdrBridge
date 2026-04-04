using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace HdrBridge.Services;

/// <summary>
/// Monitors session lock/unlock and monitor power state.
/// Raises <see cref="SystemSuspendChanged"/> when the system should suspend or resume LEDs.
/// </summary>
public class SystemPowerService : IDisposable {
    // WM_POWERBROADCAST constants
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    // GUID_MONITOR_POWER_ON  {02731015-4510-4526-99E6-E5A17EBD1AEA}
    // (Windows 8+) — reports 0 = off, 1 = on, 2 = dimmed
    private static readonly Guid GUID_MONITOR_POWER_ON =
        new("02731015-4510-4526-99E6-E5A17EBD1AEA");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING {
        public Guid PowerSetting;
        public uint DataLength;
        // Data follows immediately after — we read it manually
    }

    /// <summary>
    /// Fired when the system determines LEDs should be suspended (true) or resumed (false).
    /// </summary>
    public event EventHandler<bool>? SystemSuspendChanged;

    private HwndSource? _hwndSource;
    private IntPtr _powerNotifyHandle = IntPtr.Zero;
    private bool _disposed;

    public void Start() {
        // Subscribe to session lock/unlock
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // Create a hidden message-only window for WM_POWERBROADCAST
        var parameters = new HwndSourceParameters("HdrBridge_PowerMonitor") {
            Width = 0,
            Height = 0,
            WindowStyle = 0 // invisible
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        // Register for monitor power notifications
        var guid = GUID_MONITOR_POWER_ON;
        _powerNotifyHandle = RegisterPowerSettingNotification(_hwndSource.Handle, ref guid, 0);

        Debug.WriteLine("SystemPowerService: Started monitoring session & monitor state.");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) {
        switch (e.Reason) {
            case SessionSwitchReason.SessionLock:
                Debug.WriteLine("SystemPowerService: Session locked.");
                SystemSuspendChanged?.Invoke(this, true);
                break;
            case SessionSwitchReason.SessionUnlock:
                Debug.WriteLine("SystemPowerService: Session unlocked.");
                SystemSuspendChanged?.Invoke(this, false);
                break;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero) {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GUID_MONITOR_POWER_ON && setting.DataLength >= 4) {
                // Read the DWORD that follows the struct
                int dataOffset = Marshal.SizeOf<POWERBROADCAST_SETTING>();
                int monitorState = Marshal.ReadInt32(lParam + dataOffset);

                if (monitorState == 0) {
                    Debug.WriteLine("SystemPowerService: Monitor OFF.");
                    SystemSuspendChanged?.Invoke(this, true);
                } else if (monitorState == 1) {
                    Debug.WriteLine("SystemPowerService: Monitor ON.");
                    SystemSuspendChanged?.Invoke(this, false);
                }
                // monitorState == 2 is "dimmed", we ignore it
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.SessionSwitch -= OnSessionSwitch;

        if (_powerNotifyHandle != IntPtr.Zero) {
            UnregisterPowerSettingNotification(_powerNotifyHandle);
            _powerNotifyHandle = IntPtr.Zero;
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        _hwndSource = null;

        Debug.WriteLine("SystemPowerService: Disposed.");
    }
}
