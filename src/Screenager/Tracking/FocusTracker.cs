using System.Diagnostics;
using Screenager.Data;
using Screenager.Native;

namespace Screenager.Tracking;

/// <summary>
/// Attributes active foreground time to (process, title) using a WinEvent hook for
/// foreground changes plus a periodic flush (to capture long-focused windows and title
/// changes such as browser tab switches). Time is only credited while <see cref="ShouldCount"/>
/// returns true, so locked/idle periods are excluded.
/// </summary>
public sealed class FocusTracker : IDisposable
{
    private readonly Database _db;
    private readonly LogicalClock _clock;
    private readonly NativeMethods.WinEventDelegate _proc; // keep alive: GC must not collect this
    private readonly System.Windows.Forms.Timer _flushTimer;
    private readonly Stopwatch _stopwatch = new();
    private IntPtr _hook;

    private string _process = "";
    private string _title = "";

    /// <summary>Predicate gating whether elapsed time should be credited (false while paused/locked).</summary>
    public Func<bool> ShouldCount { get; set; } = () => true;

    public FocusTracker(Database db, LogicalClock clock)
    {
        _db = db;
        _clock = clock;
        _proc = OnForegroundChanged;
        _flushTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        _flushTimer.Tick += (_, _) => Flush(restart: true);
    }

    public void Start()
    {
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        Capture(NativeMethods.GetForegroundWindow());
        _stopwatch.Restart();
        _flushTimer.Start();
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
    {
        Flush(restart: false);
        Capture(hwnd);
        _stopwatch.Restart();
    }

    private void Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            _process = "";
            _title = "";
            return;
        }
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        _process = NativeMethods.GetProcessName(pid);
        _title = NativeMethods.GetWindowTitle(hwnd);
    }

    private void Flush(bool restart)
    {
        int seconds = (int)Math.Round(_stopwatch.Elapsed.TotalSeconds);
        if (restart)
            _stopwatch.Restart();

        if (seconds > 0 && _process.Length > 0 && ShouldCount())
            _db.AddFocusSeconds(_clock.Today(), _process, Sanitize(_title), seconds);
    }

    private static string Sanitize(string title) => title.Length > 200 ? title[..200] : title;

    public void Dispose()
    {
        _flushTimer.Stop();
        _flushTimer.Dispose();
        Flush(restart: false);
        if (_hook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_hook);
    }
}
