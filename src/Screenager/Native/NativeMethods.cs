using System.Runtime.InteropServices;
using System.Text;

namespace Screenager.Native;

/// <summary>
/// P/Invoke surface. All Win32 interop lives here so the rest of the app stays readable.
/// </summary>
internal static partial class NativeMethods
{
    // ---------------- Idle detection ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [LibraryImport("kernel32.dll")]
    public static partial ulong GetTickCount64();

    /// <summary>Seconds since the last keyboard/mouse input in this session.</summary>
    public static double GetIdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
            return 0;
        ulong now = GetTickCount64();
        // dwTime is a 32-bit GetTickCount value; compute the low-32 difference to survive wrap.
        uint last = lii.dwTime;
        uint nowLow = unchecked((uint)now);
        uint diffMs = unchecked(nowLow - last);
        return diffMs / 1000.0;
    }

    // ---------------- Locking ----------------
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();

    // ---------------- Session (lock/unlock) notifications ----------------
    public const int NOTIFY_FOR_THIS_SESSION = 0;
    public const int WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // ---------------- Power (sleep/resume) notifications ----------------
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_APMSUSPEND = 0x4;
    public const int PBT_APMRESUMESUSPEND = 0x7;
    public const int PBT_APMRESUMEAUTOMATIC = 0x12;

    // ---------------- Foreground window / focus tracking ----------------
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(IntPtr hWinEventHook);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        int len = GetWindowTextW(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    // ---------------- Process name ----------------
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    public static string GetProcessName(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return string.Empty;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size))
            {
                try { return Path.GetFileNameWithoutExtension(sb.ToString()); }
                catch { return sb.ToString(); }
            }
            return string.Empty;
        }
        finally { CloseHandle(h); }
    }

    // ---------------- Topmost / foreground forcing ----------------
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    public static void KeepTopMost(IntPtr hWnd)
        => SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>
    /// Force a window to the foreground even when another app (e.g. a game) holds focus,
    /// by briefly attaching to the foreground thread's input queue.
    /// </summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();

        if (fgThread != 0 && fgThread != thisThread)
            AttachThreadInput(fgThread, thisThread, true);

        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);

        if (fgThread != 0 && fgThread != thisThread)
            AttachThreadInput(fgThread, thisThread, false);
    }

    // ---------------- Global hotkey ----------------
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
