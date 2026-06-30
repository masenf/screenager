using Screenager.Native;

namespace Screenager.Ui;

/// <summary>
/// Brief topmost "screen time is up" message shown after the user unlocks once their time
/// is already spent. Auto-closes after a few seconds and then raises <see cref="Elapsed"/>,
/// which the controller uses to re-lock.
/// </summary>
public sealed class TimeUpWindow : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;

    private readonly System.Windows.Forms.Timer _timer;

    /// <summary>Raised when the message has been shown long enough and the screen should re-lock.</summary>
    public event Action? Elapsed;

    public TimeUpWindow(int showSeconds = 4)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(120, 20, 20);
        Size = new Size(560, 180);

        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Screen time is up for today.\n\nLocking the screen…",
        });

        _timer = new System.Windows.Forms.Timer { Interval = showSeconds * 1000 };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
            Elapsed?.Invoke();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST;
            return cp;
        }
    }

    public void Flash()
    {
        if (!Visible)
            Show();
        CenterToScreen();
        NativeMethods.ForceForeground(Handle);
        _timer.Stop();
        _timer.Start();
    }
}
