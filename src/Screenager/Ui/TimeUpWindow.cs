using Screenager.Native;

namespace Screenager.Ui;

/// <summary>
/// Brief topmost "screen time is up" message shown after the user unlocks once their time is
/// already spent. Purely visual: it auto-hides after a few seconds. The decision to re-lock is
/// owned by the controller's tick loop (so a parent override can cancel it).
/// </summary>
public sealed class TimeUpWindow : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;

    private readonly System.Windows.Forms.Timer _timer;

    public TimeUpWindow()
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

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
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

    /// <summary>Show the message for <paramref name="seconds"/>, then auto-hide.</summary>
    public void Flash(int seconds)
    {
        if (!Visible)
            Show();
        CenterToScreen();
        NativeMethods.ForceForeground(Handle);
        _timer.Stop();
        _timer.Interval = Math.Max(1, seconds) * 1000;
        _timer.Start();
    }

    /// <summary>Abort the message (e.g. the parent just granted more time).</summary>
    public void Cancel()
    {
        _timer.Stop();
        if (Visible)
            Hide();
    }
}
