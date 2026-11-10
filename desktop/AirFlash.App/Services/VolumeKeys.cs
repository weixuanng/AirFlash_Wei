using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AirFlash.App.ViewModels;
namespace AirFlash.App.Services;

/// <summary>
/// While Airplay is streaming to a speaker that reports its volume, the keyboard's
/// volume-up and volume-down keys change the speaker instead of the PC. At any other
/// time, and for the mute key, the keys are left to Windows.
/// </summary>
internal sealed class VolumeKeys : IDisposable
{
    private const int KeyboardLowLevel = 13, KeyDown = 0x0100, SystemKeyDown = 0x0104, VolumeDown = 0xAE, VolumeUp = 0xAF;
    /// <summary>Change per key press, in the speaker's 0 to 100 scale.</summary>
    internal const int Step = 4;
    private readonly AppViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    // Kept in a field: Windows calls it for as long as the hook is installed.
    private readonly HookProcedure _procedure;
    private IntPtr _hook;
    private volatile bool _active;

    public VolumeKeys(AppViewModel viewModel, Dispatcher dispatcher)
    {
        _viewModel = viewModel; _dispatcher = dispatcher; _procedure = OnKey;
        _viewModel.PropertyChanged += OnPropertyChanged;
        Refresh();
    }
    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppViewModel.VolumeAvailable) or nameof(AppViewModel.VolumeKeysEnabled)) Refresh();
    }
    /// <summary>The hook exists only while there is a speaker volume to change.</summary>
    private void Refresh()
    {
        _active = _viewModel.VolumeAvailable && _viewModel.VolumeKeysEnabled;
        if (_active && _hook == IntPtr.Zero)
        {
            using var process = Process.GetCurrentProcess();
            _hook = SetWindowsHookEx(KeyboardLowLevel, _procedure, GetModuleHandle(process.MainModule?.ModuleName), 0);
            AppPaths.Log($"Volume keys hook installed={_hook != IntPtr.Zero}");
        }
        else if (!_active && _hook != IntPtr.Zero) Remove();
    }
    private void Remove()
    {
        var hook = _hook; _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }
    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        // Must return quickly: the change itself is posted to the UI thread.
        if (code >= 0 && _active && (wParam == KeyDown || wParam == SystemKeyDown))
        {
            var key = Marshal.ReadInt32(lParam);
            if (key is VolumeUp or VolumeDown)
            {
                var step = key == VolumeUp ? Step : -Step;
                _dispatcher.BeginInvoke(() => { if (_viewModel.VolumeAvailable) _viewModel.DeviceVolume += step; });
                return 1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnPropertyChanged;
        _active = false;
        Remove();
    }
    private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProcedure procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
