using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace AirFlash.App.Services;

/// <summary>
/// Real blur of whatever is behind the panel, drawn by the Windows compositor.
/// The panel is a per-pixel transparent window, which the documented backdrop
/// materials do not support, so this uses the accent policy the shell's own
/// flyouts use. Windows blurs the whole window rectangle; a rounded window region
/// keeps the blur inside the card.
/// </summary>
internal static class PanelBackdrop
{
    /// <summary>Off for verification runs, which must not depend on the compositor.</summary>
    public static bool Enabled { get; set; } = true;
    private static bool _refused; // asked once; a refusal does not change while the app runs
    private const int AccentPolicyAttribute = 19, Disabled = 0, Blur = 3, Acrylic = 4;
    private const int CornerPreference = 33, Round = 2;

    /// <summary>Turn the blur on. False when Windows refused; the panel then keeps its solid look.</summary>
    public static bool Apply(Window window, double cornerRadius)
    {
        if (!Enabled || _refused || new WindowInteropHelper(window).Handle is var handle && handle == IntPtr.Zero) return false;
        try
        {
            // A near-transparent tint: the panel paints its own colour over the blur.
            // Acrylic ignores a tint with no alpha at all.
            var applied = SetAccent(handle, Acrylic, 0x01000000) || SetAccent(handle, Blur, 0);
            if (!applied) { _refused = true; AppPaths.Log("Panel blur unavailable: Windows refused the accent policy"); return false; }
            Clip(window, handle, cornerRadius);
            var round = Round;
            _ = DwmSetWindowAttribute(handle, CornerPreference, ref round, sizeof(int)); // Windows 11 only; the region covers the rest
            return true;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            _refused = true;
            AppPaths.Log($"Panel blur unavailable: {error.Message}");
            return false;
        }
    }
    /// <summary>Keep the blurred area matched to the card after the panel changes size.</summary>
    public static void Resize(Window window, double cornerRadius)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) Clip(window, handle, cornerRadius);
    }
    public static void Remove(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try { SetAccent(handle, Disabled, 0); _ = SetWindowRgn(handle, IntPtr.Zero, true); }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { }
    }

    private static void Clip(Window window, IntPtr handle, double cornerRadius)
    {
        if (!GetWindowRect(handle, out var rect)) return;
        var diameter = (int)Math.Round(2 * cornerRadius * VisualTreeHelper.GetDpi(window).DpiScaleX);
        // The region's right and bottom edges are exclusive, hence the extra pixel.
        var region = CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, diameter, diameter);
        // Windows owns the region once it is set; it is ours to free only on failure.
        if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
    }
    private static bool SetAccent(IntPtr handle, int state, uint tint)
    {
        var policy = new AccentPolicy { State = state, Flags = 0, Tint = tint, Animation = 0 };
        var size = Marshal.SizeOf<AccentPolicy>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionData { Attribute = AccentPolicyAttribute, Data = memory, Size = size };
            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags; public uint Tint; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public nint Size; } // cbData is a SIZE_T
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
