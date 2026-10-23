using System.Runtime.InteropServices;

namespace HrOsd;

/// <summary>集中放置所有 Win32 P/Invoke，避免散落各文件。</summary>
internal static class Native
{
    // ---- 窗口扩展样式 ----
    public const int GWL_EXSTYLE      = -20;
    public const int WS_EX_TOPMOST    = 0x00000008;
    public const int WS_EX_TRANSPARENT= 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED    = 0x00080000;

    public const int SWP_NOSIZE       = 0x0001;
    public const int SWP_NOMOVE       = 0x0002;
    public const int SWP_NOZORDER     = 0x0004;
    public const int SWP_NOACTIVATE   = 0x0010;
    public const int SWP_SHOWWINDOW   = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int X, int Y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOPMOST   = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // ---- 全局热键 ----
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT     = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT   = 0x0004;
    public const uint MOD_WIN     = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- CPU 时间 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FILETIME idleTime,
                                             out FILETIME kernelTime,
                                             out FILETIME userTime);

    // ---- 物理内存 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
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
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---- DLL 动态加载（NVML 不一定在 PATH 里）----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    // ---- 前台窗口，用于自动识别当前正在玩的游戏 ----
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- 取窗口所在的显示器工作区，用于多屏 / 分辨率变化时重新定位 ----
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
