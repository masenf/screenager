using System.Diagnostics;
using Screenager.Native;

namespace Screenager.Enforcement;

/// <summary>
/// Locks the workstation (never logs out — open programs survive), rate-limited so a
/// pathological state can't spin in a tight lock loop.
/// </summary>
public sealed class Enforcer
{
    private readonly Stopwatch _sinceLock = new();
    private readonly int _minIntervalMs;

    public Enforcer(int minIntervalMs = 4000) => _minIntervalMs = minIntervalMs;

    public bool Lock()
    {
        if (_sinceLock.IsRunning && _sinceLock.ElapsedMilliseconds < _minIntervalMs)
            return false;

        bool ok = NativeMethods.LockWorkStation();
        _sinceLock.Restart();
        return ok;
    }
}
