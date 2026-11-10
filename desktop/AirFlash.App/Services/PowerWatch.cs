using System.Runtime.InteropServices;
namespace AirFlash.App.Services;

/// <summary>
/// Tells the app when the PC is about to sleep and when it has woken, on both
/// classic sleep and Modern Standby. Windows waits for the suspend callback to
/// return, which is what gives the stream time to let go of the speaker.
/// </summary>
internal sealed class PowerWatch : IDisposable
{
    private const uint Callback = 2, Suspend = 4, ResumeSuspend = 7, ResumeAutomatic = 18;
    private readonly Action _suspending, _resumed;
    // Kept in a field: Windows calls it for as long as the registration lives.
    private readonly Routine _routine;
    private IntPtr _registration;

    public PowerWatch(Action suspending, Action resumed)
    {
        _suspending = suspending; _resumed = resumed; _routine = OnPower;
        var recipient = new Recipient { Callback = _routine };
        try
        {
            var status = PowerRegisterSuspendResumeNotification(Callback, ref recipient, out _registration);
            AppPaths.Log($"Sleep watch registered={status == 0}");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { AppPaths.Log($"Sleep watch unavailable: {error.Message}"); }
    }
    private uint OnPower(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (type == Suspend) _suspending();
            else if (type is ResumeAutomatic or ResumeSuspend) _resumed();
        }
        catch (Exception error) { AppPaths.Log($"Sleep watch handler failed: {error.Message}"); }
        return 0;
    }
    public void Dispose()
    {
        var registration = _registration; _registration = IntPtr.Zero;
        if (registration != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(registration);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Routine(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] private struct Recipient { public Routine Callback; public IntPtr Context; }
    [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Recipient recipient, out IntPtr registration);
    [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
}
