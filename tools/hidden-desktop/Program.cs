// hidden-desktop: run a command on a separate, never-shown Win32 desktop of the current session,
// so test windows (Godot, browsers) cannot flash, steal focus or show up in the taskbar.
//
//   hidden-desktop [--desktop NAME] [--timeout SECONDS] [--] COMMAND [ARGS...]
//   hidden-desktop --list-windows [NAME]
//
// The child gets this process's stdin/stdout/stderr, its exit code is returned, and the whole child
// process tree lives in a kill-on-close job: Ctrl+C, the timeout, or this launcher dying for any reason
// terminates it. The desktop is opened if it exists, otherwise created; it is never switched to.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HiddenDesktop;

internal static unsafe class Program
{
    private const string DefaultDesktop = "H5Test";
    private const int ExitUsage = 125;
    private const int ExitTimeout = 124;
    private const int ExitInterrupted = 130;

    private static IntPtr _job;

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("hidden-desktop: Windows only.");
            return ExitUsage;
        }

        string desktop = DefaultDesktop;
        double timeoutSec = 0;
        int i = 0;
        for (; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a == "--desktop" && i + 1 < args.Length) { desktop = args[++i]; continue; }
            if (a == "--timeout" && i + 1 < args.Length)
            {
                if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out timeoutSec) || timeoutSec < 0)
                    return Usage("bad --timeout value");
                continue;
            }
            if (a == "--list-windows")
                return ListWindows(i + 1 < args.Length ? args[i + 1] : DefaultDesktop);
            if (a == "-h" || a == "--help") return Usage(null);
            if (a.StartsWith("--", StringComparison.Ordinal)) return Usage("unknown option " + a);
            break;
        }
        if (i >= args.Length) return Usage("no command given");

        try
        {
            return Run(desktop, timeoutSec, args[i..]);
        }
        catch (Win32Exception e)
        {
            Console.Error.WriteLine("hidden-desktop: " + e.Message);
            return ExitUsage;
        }
    }

    private static int Usage(string? error)
    {
        if (error != null) Console.Error.WriteLine("hidden-desktop: " + error);
        Console.Error.WriteLine(
            "usage: hidden-desktop [--desktop NAME] [--timeout SECONDS] [--] COMMAND [ARGS...]\n" +
            "       hidden-desktop --list-windows [NAME]   (visible top-level windows on a desktop, e.g. Default)\n" +
            $"Default desktop: {DefaultDesktop}. Exit code: the command's; {ExitTimeout} on timeout, " +
            $"{ExitInterrupted} on Ctrl+C, {ExitUsage} on launcher errors.");
        return error == null ? 0 : ExitUsage;
    }

    private static int Run(string desktopName, double timeoutSec, string[] command)
    {
        // Keep our own handle open for the whole run: a desktop lives as long as someone holds it.
        IntPtr hDesk = OpenDesktopW(desktopName, 0, false, DESKTOP_ALL);
        if (hDesk == IntPtr.Zero)
            hDesk = CreateDesktopW(desktopName, null, IntPtr.Zero, 0, DESKTOP_ALL, IntPtr.Zero);
        if (hDesk == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop failed");

        string lpDesktop = WindowStationName() + "\\" + desktopName;

        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            TerminateJobObject(_job, ExitInterrupted);
            Console.Error.WriteLine("hidden-desktop: interrupted, child tree killed");
            Environment.Exit(ExitInterrupted);
        };

        IntPtr me = GetCurrentProcess();
        IntPtr stdIn = InheritableStd(me, STD_INPUT_HANDLE);
        IntPtr stdOut = InheritableStd(me, STD_OUTPUT_HANDLE);
        IntPtr stdErr = InheritableStd(me, STD_ERROR_HANDLE);

        fixed (char* pDesk = lpDesktop)
        {
            var si = new STARTUPINFOW
            {
                cb = (uint)sizeof(STARTUPINFOW),
                lpDesktop = pDesk,
                dwFlags = STARTF_USESTDHANDLES,
                hStdInput = stdIn,
                hStdOutput = stdOut,
                hStdError = stdErr,
            };
            uint flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT;
            // Without a console of our own a console child would get a fresh console window; suppress it.
            if (GetConsoleWindow() == IntPtr.Zero) flags |= CREATE_NO_WINDOW;

            var cmdLine = new StringBuilder(BuildCommandLine(command));
            cmdLine.Append('\0');
            char[] buf = cmdLine.ToString().ToCharArray();
            PROCESS_INFORMATION pi;
            fixed (char* pCmd = buf)
            {
                if (!CreateProcessW(null, pCmd, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, null, &si, &pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for: " + command[0]);
            }
            if (!AssignProcessToJobObject(_job, pi.hProcess))
            {
                int err = Marshal.GetLastWin32Error();
                TerminateProcess(pi.hProcess, ExitUsage);
                throw new Win32Exception(err, "AssignProcessToJobObject failed");
            }
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
            foreach (var h in new[] { stdIn, stdOut, stdErr })
                if (h != IntPtr.Zero) CloseHandle(h);

            uint waitMs = timeoutSec > 0 ? (uint)Math.Min(timeoutSec * 1000.0, 0xFFFFFFFEu) : INFINITE;
            uint w = WaitForSingleObject(pi.hProcess, waitMs);
            if (w == WAIT_TIMEOUT)
            {
                TerminateJobObject(_job, ExitTimeout);
                WaitForSingleObject(pi.hProcess, 5000);
                Console.Error.WriteLine($"hidden-desktop: timeout after {timeoutSec}s, child tree killed");
                return ExitTimeout;
            }
            GetExitCodeProcess(pi.hProcess, out uint code);
            CloseHandle(pi.hProcess);
            // Anything the child left running dies with the job (kill-on-close) when we exit.
            TerminateJobObject(_job, (uint)code);
            CloseDesktop(hDesk);
            return unchecked((int)code);
        }
    }

    private static IntPtr InheritableStd(IntPtr me, int which)
    {
        IntPtr h = GetStdHandle(which);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return IntPtr.Zero;
        return DuplicateHandle(me, h, me, out IntPtr dup, 0, true, DUPLICATE_SAME_ACCESS) ? dup : IntPtr.Zero;
    }

    private static string WindowStationName()
    {
        IntPtr ws = GetProcessWindowStation();
        char* buf = stackalloc char[256];
        if (ws != IntPtr.Zero && GetUserObjectInformationW(ws, UOI_NAME, buf, 512, out _))
            return new string(buf);
        return "WinSta0";
    }

    // Quote per the MSVC / CommandLineToArgvW rules so every argument reaches the child unchanged.
    internal static string BuildCommandLine(string[] argv)
    {
        var sb = new StringBuilder();
        foreach (string arg in argv)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) { sb.Append(arg); continue; }
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1);
                else sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
        }
        return sb.ToString();
    }

    private static int ListWindows(string desktopName)
    {
        IntPtr hDesk = OpenDesktopW(desktopName, 0, false, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
        if (hDesk == IntPtr.Zero)
        {
            Console.Error.WriteLine($"hidden-desktop: cannot open desktop {desktopName} (error {Marshal.GetLastWin32Error()})");
            return ExitUsage;
        }
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgPid);
        Console.WriteLine($"foreground\t0x{fg:X}\tpid={fgPid}\t{ProcName(fgPid)}");
        var rows = new List<string>();
        EnumWindowsProc cb = (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            var title = new char[256];
            int n;
            fixed (char* t = title) n = GetWindowTextW(hwnd, t, title.Length);
            GetWindowRect(hwnd, out RECT r);
            rows.Add($"0x{hwnd:X}\tpid={pid}\t{ProcName(pid)}\t[{r.Left},{r.Top},{r.Right},{r.Bottom}]\t{new string(title, 0, n)}");
            return true;
        };
        EnumDesktopWindows(hDesk, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        foreach (var row in rows) Console.WriteLine(row);
        CloseDesktop(hDesk);
        return 0;
    }

    private static string ProcName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return "?"; }
    }

    // ---- Win32 ----
    private const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_ENUMERATE = 0x0040;
    private const uint DESKTOP_ALL = 0x01FF | 0x000F0000; // all desktop rights + STANDARD_RIGHTS_REQUIRED
    private const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
    private const uint STARTF_USESTDHANDLES = 0x100;
    private const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
    private const uint DUPLICATE_SAME_ACCESS = 2, INFINITE = 0xFFFFFFFF, WAIT_TIMEOUT = 0x102;
    private const int UOI_NAME = 2, JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb; public char* lpReserved; public char* lpDesktop; public char* lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong R, W, O, RB, WB, OB; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string name, string? device, IntPtr devmode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr h, int index, char* info, uint len, out uint needed);
    [DllImport("user32.dll")]
    private static extern bool EnumDesktopWindows(IntPtr desk, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern int GetWindowTextW(IntPtr hwnd, char* text, int max);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreateProcessW(string? app, char* cmdLine, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? cwd, STARTUPINFOW* si, PROCESS_INFORMATION* pi);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr sa, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int cls, void* info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
