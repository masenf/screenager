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
    private int _warnSeconds;

    public TimerWindow(int warnSeconds)
    {
        _warnSeconds = warnSeconds;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 28);
        Size = new Size(120, 48);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "--:--",
        };
        Controls.Add(_label);

        // Let the parent drag it around if they want.
        _label.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        };
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
        PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
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
