using System.Runtime.InteropServices;
using System.Text;

namespace DesktopJanitor;

/// <summary>Win32 calls used by the janitor. Nothing here can kill a process.</summary>
internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint GW_OWNER = 4;
    public const uint WM_CLOSE = 0x0010;
    public const int DWMWA_CLOAKED = 14;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    // SHQueryUserNotificationState results that mean "something is fullscreen".
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    public readonly record struct ProcessSample(uint Pid, int SessionId, long PrivateWorkingSet, string ImageName);

    /// <summary>
    /// Every process with its private working set, in one call: the same figure as Task Manager's
    /// "Memory" column. (WorkingSet64 counts shared pages again in every process, which overstates
    /// multi-process apps such as browsers.) Empty if unavailable; callers fall back.
    /// </summary>
    public static List<ProcessSample> SnapshotProcesses()
    {
        const int SystemProcessInformation = 5;
        const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        if (IntPtr.Size != 8) return new(); // the field offsets below are for 64-bit Windows

        int size = 1 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 64 * 1024);
                    continue;
                }
                if (status != 0) return new();

                // SYSTEM_PROCESS_INFORMATION (x64): WorkingSetPrivateSize @0x08, ImageName @0x38
                // (UNICODE_STRING: Length, then Buffer @0x40), UniqueProcessId @0x50, SessionId @0x64.
                var samples = new List<ProcessSample>();
                for (int offset = 0; ;)
                {
                    IntPtr entry = buffer + offset;
                    int nameBytes = (ushort)Marshal.ReadInt16(entry, 0x38);
                    IntPtr namePtr = Marshal.ReadIntPtr(entry, 0x40);
                    samples.Add(new ProcessSample(
                        Pid: (uint)Marshal.ReadIntPtr(entry, 0x50).ToInt64(),
                        SessionId: Marshal.ReadInt32(entry, 0x64),
                        PrivateWorkingSet: Marshal.ReadInt64(entry, 0x08),
                        ImageName: namePtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(namePtr, nameBytes / 2)));

                    int next = Marshal.ReadInt32(entry, 0);
                    if (next == 0) return samples;
                    offset += next;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool K32EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    public static void AddExtendedStyle(IntPtr hWnd, long styles) => SetExtendedStyle(hWnd, styles, on: true);

    public static void SetExtendedStyle(IntPtr hWnd, long styles, bool on)
    {
        long current = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        long updated = on ? current | styles : current & ~styles;
        if (updated != current) SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(updated));
    }

    /// <summary>Total and available physical RAM in bytes.</summary>
    public static (long Total, long Available) PhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.ullTotalPhys, (long)status.ullAvailPhys) : (0, 0);
    }

    public static bool HasExtendedStyle(IntPtr hWnd, long style) =>
        (GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() & style) != 0;

    public static void KeepTopmost(IntPtr hWnd) =>
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static string GetWindowTitle(IntPtr hWnd)
    {
        int length = GetWindowTextLength(hWnd);
        if (length == 0) return string.Empty;
        var text = new StringBuilder(length + 1);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>Cloaked windows are hidden by DWM (e.g. suspended UWP apps, other virtual desktops).</summary>
    public static bool IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static TimeSpan UserIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    public static bool IsSomethingFullscreen()
    {
        if (SHQueryUserNotificationState(out int state) != 0) return false;
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    public static string? GetProcessPath(IntPtr process)
    {
        var name = new StringBuilder(1024);
        int size = name.Capacity;
        return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
    }

    /// <summary>True if elevated; null if the token can't be read (treat as untouchable).</summary>
    public static bool? IsElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;
        try
        {
            return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)
                ? elevated != 0
                : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public static long GetWorkingSet(IntPtr process)
    {
        uint size = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>();
        return K32GetProcessMemoryInfo(process, out var counters, size) ? (long)counters.WorkingSetSize : 0;
    }

    /// <summary>Asks Windows to page out a process's working set. The app keeps running; pages come back on demand.</summary>
    public static bool EmptyWorkingSet(IntPtr process) => K32EmptyWorkingSet(process);
}
