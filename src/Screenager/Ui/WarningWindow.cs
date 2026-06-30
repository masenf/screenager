using System.Media;
using Screenager.Native;

namespace Screenager.Ui;

/// <summary>
/// Large topmost warning shown in the final seconds before a lock. Forces itself to the
/// foreground (works over borderless-fullscreen apps; exclusive-fullscreen games may suppress
/// it — the lock still fires regardless) and plays a sound so it is heard even if unseen.
/// </summary>
public sealed class WarningWindow : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;

    private readonly Label _label;
    private bool _soundPlayed;

    public WarningWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(140, 20, 20);
        Size = new Size(640, 220);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 26f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(_label);
    }

    protected override bool ShowWithoutActivation => false;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST;
            return cp;
        }
    }

    public void ShowWarning(int secondsRemaining)
    {
        _label.Text = $"⚠ Screen time is almost up!\n\nLocking in {Math.Max(0, secondsRemaining)} seconds.\n\nSave your work now.";

        if (!Visible)
        {
            Show();
            CenterToScreen();
            NativeMethods.ForceForeground(Handle); // grab focus once, over fullscreen apps
            SystemSounds.Exclamation.Play();
            _soundPlayed = true;
        }
        else
        {
            NativeMethods.KeepTopMost(Handle); // stay on top without repeatedly stealing focus
        }
    }

    public void HideWarning()
    {
        if (Visible)
            Hide();
        _soundPlayed = false;
    }
}
