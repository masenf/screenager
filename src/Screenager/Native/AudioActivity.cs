using System.Runtime.InteropServices;

namespace Screenager.Native;

/// <summary>
/// Detects whether audio is currently being rendered on the default output device, via the
/// Core Audio meter API. Used so that watching a video (which produces sound but no keyboard/mouse
/// input) counts as active use instead of tripping the idle timeout. A muted video is not detected.
/// The meter is cached and re-acquired if the default device changes.
/// </summary>
public sealed class AudioActivity : IDisposable
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private const uint CLSCTX_ALL = 0x17;

    private IMMDeviceEnumerator? _enumerator;
    private IAudioMeterInformation? _meter;

    /// <summary>True if the current output peak exceeds <paramref name="threshold"/> (0..1).</summary>
    public bool IsPlaying(float threshold)
    {
        try
        {
            _meter ??= AcquireMeter();
            if (_meter is null)
                return false;
            _meter.GetPeakValue(out float peak);
            return peak > threshold;
        }
        catch
        {
            // Default device changed or was removed — drop the cached meter and retry next tick.
            Release(ref _meter);
            return false;
        }
    }

    private IAudioMeterInformation? AcquireMeter()
    {
        _enumerator ??= (IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;

        // eRender = 0 (output), eConsole = 0 (games/movies/music role).
        _enumerator.GetDefaultAudioEndpoint(0, 0, out IMMDevice device);
        try
        {
            var iid = IID_IAudioMeterInformation;
            device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object meter);
            return (IAudioMeterInformation)meter;
        }
        finally
        {
            Release(ref device);
        }
    }

    private static void Release<T>(ref T? obj) where T : class
    {
        if (obj is not null && Marshal.IsComObject(obj))
            Marshal.ReleaseComObject(obj);
        obj = null;
    }

    public void Dispose()
    {
        Release(ref _meter);
        Release(ref _enumerator);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices); // unused; keeps vtable order
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        // remaining methods (GetDevice, Register/Unregister) omitted — not called
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate([In] ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        // remaining methods (OpenPropertyStore, GetId, GetState) omitted — not called
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
        // remaining methods omitted — not called
    }
}
