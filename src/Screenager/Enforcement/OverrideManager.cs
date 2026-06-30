using Screenager.Native;
using Screenager.Tracking;
using Screenager.Ui;

namespace Screenager.Enforcement;

/// <summary>
/// Registers the global parent-override hotkey and, on press, prompts for the PIN and grants
/// bonus minutes via the tracker.
/// </summary>
public sealed class OverrideManager : IDisposable
{
    private const int HotKeyId = 0xB001;

    private readonly IntPtr _hwnd;
    private readonly ActivityTracker _tracker;
    private readonly string _pin;
    private readonly int _graceSeconds;
    private bool _registered;
    private bool _dialogOpen;
    private DateTime _openedAt;

    /// <summary>True while the override dialog is open.</summary>
    public bool DialogOpen => _dialogOpen;

    /// <summary>
    /// True while locking should be suspended: the dialog is open AND we are still within the
    /// configured grace period since it opened. Bounded so the dialog can't be left open to
    /// indefinitely dodge a lock.
    /// </summary>
    public bool LockSuppressed => _dialogOpen && (DateTime.Now - _openedAt).TotalSeconds < _graceSeconds;

    public OverrideManager(IntPtr hwnd, ActivityTracker tracker, Config cfg)
    {
        _hwnd = hwnd;
        _tracker = tracker;
        _pin = cfg.OverridePin;
        _graceSeconds = cfg.OverrideGraceSeconds;

        if (TryParseHotkey(cfg.OverrideHotkey, out uint mods, out uint vk) && _pin.Length > 0)
            _registered = NativeMethods.RegisterHotKey(_hwnd, HotKeyId, mods | NativeMethods.MOD_NOREPEAT, vk);
    }

    public void OnHotKey(int id)
    {
        if (id != HotKeyId || _dialogOpen)
            return;

        _dialogOpen = true;
        _openedAt = DateTime.Now;
        try
        {
            using var dlg = new OverrideDialog(_pin, _tracker.GrantedBonusSeconds);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                if (dlg.Action == OverrideAction.Grant && dlg.GrantedMinutes > 0)
                    _tracker.AddBonusMinutes(dlg.GrantedMinutes);
                else if (dlg.Action == OverrideAction.Revoke)
                    _tracker.RevokeBonus();
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static bool TryParseHotkey(string spec, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (var partRaw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (partRaw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= NativeMethods.MOD_CONTROL; break;
                case "alt": mods |= NativeMethods.MOD_ALT; break;
                case "shift": mods |= NativeMethods.MOD_SHIFT; break;
                case "win" or "windows": mods |= NativeMethods.MOD_WIN; break;
                default:
                    if (Enum.TryParse<Keys>(partRaw, true, out var key))
                        vk = (uint)key;
                    break;
            }
        }
        return mods != 0 && vk != 0;
    }

    public void Dispose()
    {
        if (_registered)
            NativeMethods.UnregisterHotKey(_hwnd, HotKeyId);
    }
}
