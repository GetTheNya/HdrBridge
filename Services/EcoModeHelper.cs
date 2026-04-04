using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HdrBridge.Services;

public static class EcoModeHelper {
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        uint processInformationSize);

    private const int ProcessPowerThrottling = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public static void EnableEfficiencyMode() {
        try {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle;

            var state = new PROCESS_POWER_THROTTLING_STATE {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
            };

            SetProcessInformation(
                Process.GetCurrentProcess().Handle,
                ProcessPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE))
            );
        } catch (Exception ex) {
            Debug.WriteLine($"Win11 Efficiency Mode doesn't support {ex.Message}");
        }
    }
}