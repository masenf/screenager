using Screenager.Native;

namespace Screenager.Tracking;

/// <summary>
/// A hidden top-level window that is the single sink for Win32 notifications we care about:
/// session lock/unlock (WTS), system suspend/resume (power broadcast), and global hotkeys.
/// It is a Form (never shown) because top-level windows reliably receive WM_POWERBROADCAST,
/// which message-only windows do not.
/// </summary>
public sealed class MessageWindow : Form
{
    public event Action? SessionLocked;
    public event Action? SessionUnlocked;
    public event Action? SystemSuspend;
    public event Action? SystemResume;
    public event Action<int>? HotKeyPressed;

    public MessageWindow()
    {
        // A handle-only message sink: never shown, kept off the taskbar/alt-tab.
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
    }

    protected override void SetVisibleCore(bool value)
    {
        // Force the handle to exist (so notifications register) but never actually show.
        if (!IsHandleCreated)
            CreateHandle();
        base.SetVisibleCore(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.WTSRegisterSessionNotification(Handle, NativeMethods.NOTIFY_FOR_THIS_SESSION);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.WTSUnRegisterSessionNotification(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_WTSSESSION_CHANGE:
                int evt = m.WParam.ToInt32();
                if (evt == NativeMethods.WTS_SESSION_LOCK)
                    SessionLocked?.Invoke();
                else if (evt == NativeMethods.WTS_SESSION_UNLOCK)
                    SessionUnlocked?.Invoke();
                break;

            case NativeMethods.WM_POWERBROADCAST:
                int code = m.WParam.ToInt32();
                if (code == NativeMethods.PBT_APMSUSPEND)
                    SystemSuspend?.Invoke();
                else if (code is NativeMethods.PBT_APMRESUMESUSPEND or NativeMethods.PBT_APMRESUMEAUTOMATIC)
                    SystemResume?.Invoke();
                break;

            case NativeMethods.WM_HOTKEY:
                HotKeyPressed?.Invoke(m.WParam.ToInt32());
                break;
        }

        base.WndProc(ref m);
    }
}
