using Screenager.Native;
using Screenager.Tracking;

namespace Screenager.Ui;

/// <summary>
/// Small always-on-top countdown. Never takes focus (WS_EX_NOACTIVATE) and stays out of the
/// taskbar/alt-tab (WS_EX_TOOLWINDOW). Draggable anywhere (the whole window acts as a title bar);
/// its position is reported via <see cref="Moved"/> so the controller can persist it.
/// </summary>
public sealed class TimerWindow : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;

    private readonly int _warnSeconds;
    private readonly bool _countUp;
    private readonly Point? _initialLocation;
    private readonly Font _font = new("Segoe UI", 16f, FontStyle.Bold);

    private string _text = "--:--";
    private int _topmostThrottle;

    /// <summary>Raised when the user finishes dragging the window, with the new location.</summary>
    public event Action<Point>? Moved;

    public TimerWindow(int warnSeconds, bool countUp, Point? initialLocation)
    {
        _warnSeconds = warnSeconds;
        _countUp = countUp;
        _initialLocation = initialLocation;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 28);
        Size = new Size(120, 48);
        Cursor = Cursors.SizeAll; // hint that the whole window can be dragged
        DoubleBuffered = true;     // flicker-free text repaint
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // Treat clicks anywhere as a title-bar grab so the OS drags the window for us (reliable on
        // a borderless no-activate window). Persist the new location once the move finishes.
        if (m.Msg == WM_NCHITTEST && m.Result == (IntPtr)HTCLIENT)
            m.Result = (IntPtr)HTCAPTION;
        else if (m.Msg == WM_EXITSIZEMOVE)
            Moved?.Invoke(Location);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics, _text, _font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_initialLocation is { } loc && IsOnAnyScreen(loc))
            Location = loc;
        else
            PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    // Guard against a saved position that is off-screen (e.g. a monitor was unplugged).
    private bool IsOnAnyScreen(Point loc)
    {
        var rect = new Rectangle(loc, Size);
        foreach (var screen in Screen.AllScreens)
            if (screen.WorkingArea.IntersectsWith(rect))
                return true;
        return false;
    }

    public void UpdateState(TrackerSnapshot s)
    {
        string text;
        Color bg;
        if (s.Bedtime)
        {
            text = "BEDTIME";
            bg = Color.FromArgb(60, 20, 80);
        }
        else if (s.RemainingSeconds <= 0)
        {
            text = "TIME UP";
            bg = Color.FromArgb(120, 20, 20);
        }
        else
        {
            // Count up shows elapsed used time; count down shows time remaining.
            var ts = TimeSpan.FromSeconds(_countUp ? s.ActiveSeconds : s.RemainingSeconds);
            text = ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes:00}:{ts.Seconds:00}";
            // Colour still reflects urgency (time remaining), regardless of display direction.
            bg = s.RemainingSeconds <= _warnSeconds ? Color.FromArgb(150, 30, 30)
               : s.Paused ? Color.FromArgb(40, 40, 48)
               : Color.FromArgb(24, 24, 28);
        }

        if (s.Paused && s.RemainingSeconds > 0 && !s.Bedtime)
            text = "⏸ " + text;

        // Only repaint when something actually changed (nothing happens while paused/idle).
        bool changed = false;
        if (text != _text)
        {
            _text = text;
            changed = true;
        }
        if (bg != BackColor)
        {
            BackColor = bg; // also invalidates
            changed = true;
        }
        if (changed)
            Invalidate();

        // Re-assert topmost occasionally rather than every tick.
        if (++_topmostThrottle >= 10)
        {
            _topmostThrottle = 0;
            NativeMethods.KeepTopMost(Handle);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _font.Dispose();
        base.Dispose(disposing);
    }
}
