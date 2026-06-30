using Screenager.Native;
using Screenager.Tracking;

namespace Screenager.Ui;

/// <summary>
/// Small always-on-top countdown. Never takes focus (WS_EX_NOACTIVATE) and stays out of the
/// taskbar/alt-tab (WS_EX_TOOLWINDOW). Re-asserts topmost on every update.
/// </summary>
public sealed class TimerWindow : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Label _label;
    private readonly int _warnSeconds;
    private readonly Point? _initialLocation;

    private bool _dragging;
    private Point _dragStartScreen;
    private Point _dragStartWindow;

    /// <summary>Raised when the user finishes dragging the window, with the new location.</summary>
    public event Action<Point>? Moved;

    public TimerWindow(int warnSeconds, Point? initialLocation)
    {
        _warnSeconds = warnSeconds;
        _initialLocation = initialLocation;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 28);
        Size = new Size(120, 48);
        Cursor = Cursors.SizeAll; // hint that it can be dragged

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "--:--",
            Cursor = Cursors.SizeAll,
        };
        Controls.Add(_label);

        // Drag anywhere on the window to move it (it never takes focus, so we move it manually).
        _label.MouseDown += OnDragStart;
        _label.MouseMove += OnDragMove;
        _label.MouseUp += OnDragEnd;
    }

    private void OnDragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStartScreen = Cursor.Position;
        _dragStartWindow = Location;
    }

    private void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = Cursor.Position;
        Location = new Point(
            _dragStartWindow.X + (now.X - _dragStartScreen.X),
            _dragStartWindow.Y + (now.Y - _dragStartScreen.Y));
    }

    private void OnDragEnd(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Moved?.Invoke(Location);
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
            var ts = TimeSpan.FromSeconds(s.RemainingSeconds);
            text = ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes:00}:{ts.Seconds:00}";
            bg = s.RemainingSeconds <= _warnSeconds ? Color.FromArgb(150, 30, 30)
               : s.Paused ? Color.FromArgb(40, 40, 48)
               : Color.FromArgb(24, 24, 28);
        }

        if (s.Paused && s.RemainingSeconds > 0 && !s.Bedtime)
            text = "⏸ " + text;

        _label.Text = text;
        BackColor = bg;
        NativeMethods.KeepTopMost(Handle);
    }
}
