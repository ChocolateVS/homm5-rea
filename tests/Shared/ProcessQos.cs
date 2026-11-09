using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace H5.TestSupport;

/// <summary>
/// Test-only: Windows 11 runs the windowless testhost as a background process and may give it EcoQoS
/// (power throttling), which parks its threads on the efficiency cores. With several suites running at once the
/// test threads then sit "Ready" most of the time (measured: ~15% of one core on a half-idle 20-thread machine;
/// opting out restored 100%). Opt this process out of execution-speed throttling (HighQoS). No effect elsewhere.
/// </summary>
static class ProcessQos
{
    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    const int ProcessPowerThrottling = 4;            // PROCESS_INFORMATION_CLASS
    const uint ExecutionSpeed = 1;                   // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

#pragma warning disable CA2255 // a module initializer is the point: it runs before any test in the assembly
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void OptOutOfPowerThrottling()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var s = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = 0 };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref s, Marshal.SizeOf<PowerThrottlingState>());
        }
        catch { /* older Windows: nothing to opt out of */ }
    }
}
