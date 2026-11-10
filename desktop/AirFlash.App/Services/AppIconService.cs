using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace AirFlash.App.Services;

// The ICO is embedded separately from the executable icon so development hosts
// and single-file releases load the same frames, without relying on ProcessPath.
internal static class AppIconService
{
    private sealed record Frame(int Size, byte[] Data);
    private static readonly Lazy<Frame[]> Frames = new(ReadFrames);

    internal static NativeIcon Create(int pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixels);
        var frames = Frames.Value;
        var frame = frames.FirstOrDefault(frame => frame.Size >= pixels) ?? frames[^1];
        var handle = CreateIconFromResourceEx(frame.Data, (uint)frame.Data.Length, true, 0x00030000, pixels, pixels, 0);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the Airplay icon.");
        return new NativeIcon(handle);
    }

    /// <summary>
    /// The icon with a small state dot in its corner: green while streaming, amber
    /// while waiting for the speaker, red on an error. Any other state, or any
    /// failure to draw, gives the plain icon.
    /// </summary>
    internal static NativeIcon Create(int pixels, string tone)
    {
        System.Windows.Media.Color? badge = tone switch
        {
            "live" => System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59),
            "wait" or "busy" => System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A),
            "error" => System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A),
            _ => null
        };
        if (badge is not { } colour) return Create(pixels);
        try
        {
            using var stream = typeof(AppIconService).Assembly.GetManifestResourceStream("AirFlash.Icon.ico")
                ?? throw new InvalidDataException("The embedded Airplay icon is missing.");
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var source = decoder.Frames.OrderBy(frame => frame.PixelWidth).FirstOrDefault(frame => frame.PixelWidth >= pixels) ?? decoder.Frames.OrderBy(frame => frame.PixelWidth).Last();
            var visual = new System.Windows.Media.DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(source, new Rect(0, 0, pixels, pixels));
                var radius = pixels * 0.24;
                var centre = new Point(pixels - radius - 0.5, pixels - radius - 0.5);
                context.DrawEllipse(System.Windows.Media.Brushes.White, null, centre, radius + Math.Max(1, pixels / 16.0), radius + Math.Max(1, pixels / 16.0));
                context.DrawEllipse(new System.Windows.Media.SolidColorBrush(colour), null, centre, radius, radius);
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(pixels, pixels, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var bits = new byte[pixels * pixels * 4];
            // An icon's colour bitmap is read as straight alpha, not premultiplied.
            new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(bits, pixels * 4, 0);
            IntPtr colourBitmap = CreateBitmap(pixels, pixels, 1, 32, bits), mask = CreateBitmap(pixels, pixels, 1, 1, new byte[((pixels + 15) / 16 * 2) * pixels]);
            try
            {
                if (colourBitmap == IntPtr.Zero || mask == IntPtr.Zero) return Create(pixels);
                var info = new IconInfo { Icon = true, Mask = mask, Colour = colourBitmap };
                var handle = CreateIconIndirect(ref info);
                return handle == IntPtr.Zero ? Create(pixels) : new NativeIcon(handle);
            }
            finally
            {
                if (colourBitmap != IntPtr.Zero) DeleteObject(colourBitmap);
                if (mask != IntPtr.Zero) DeleteObject(mask);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppPaths.Log($"Tray state icon unavailable: {error.Message}");
            return Create(pixels);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public int HotspotX, HotspotY; public IntPtr Mask, Colour; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    private static Frame[] ReadFrames()
    {
        using var stream = typeof(AppIconService).Assembly.GetManifestResourceStream("AirFlash.Icon.ico")
            ?? throw new InvalidDataException("The embedded Airplay icon is missing.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length < 6 || BitConverter.ToUInt16(bytes, 0) != 0 || BitConverter.ToUInt16(bytes, 2) != 1)
            throw new InvalidDataException("Invalid Airplay ICO header.");
        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0 || bytes.Length < 6 + count * 16) throw new InvalidDataException("Invalid Airplay ICO directory.");
        var frames = new Frame[count];
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            var length = BitConverter.ToUInt32(bytes, entry + 8);
            var offset = BitConverter.ToUInt32(bytes, entry + 12);
            if (width != height || length == 0 || offset < 6 + count * 16 || (ulong)offset + length > (ulong)bytes.Length)
                throw new InvalidDataException("Invalid Airplay ICO frame.");
            frames[i] = new(width, bytes.AsSpan((int)offset, (int)length).ToArray());
        }
        return frames.OrderBy(frame => frame.Size).ToArray();
    }

    internal static int Pixels(uint dpi, bool small) => Math.Max(1, GetSystemMetricsForDpi(small ? 49 : 11, dpi));
    internal static uint WindowDpi(IntPtr window) => window != IntPtr.Zero ? GetDpiForWindow(window) : GetDpiForSystem();
    internal static uint TaskbarDpi() => WindowDpi(FindWindow("Shell_TrayWnd", null));

    internal static void Track(Window window)
    {
        NativeIcon? small = null, large = null;
        var currentDpi = 0u;
        void Refresh()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dpi = WindowDpi(handle);
            if (dpi == currentDpi) return;
            NativeIcon? nextSmall = null, nextLarge = null;
            try
            {
                nextSmall = Create(Pixels(dpi, true)); nextLarge = Create(Pixels(dpi, false));
                SendMessage(handle, 0x0080, IntPtr.Zero, nextSmall.DangerousGetHandle());
                SendMessage(handle, 0x0080, new(1), nextLarge.DangerousGetHandle());
                small?.Dispose(); large?.Dispose();
                small = nextSmall; large = nextLarge; nextSmall = null; nextLarge = null;
                currentDpi = dpi;
            }
            catch (Win32Exception error) { AppPaths.Log($"Window icon refresh failed: {error.Message}"); }
            finally { nextSmall?.Dispose(); nextLarge?.Dispose(); }
        }
        EventHandler initialized = (_, _) => Refresh();
        DpiChangedEventHandler dpiChanged = (_, _) => Refresh();
        EventHandler? closed = null;
        closed = (_, _) =>
        {
            window.SourceInitialized -= initialized; window.DpiChanged -= dpiChanged; window.Closed -= closed;
            small?.Dispose(); large?.Dispose();
        };
        window.SourceInitialized += initialized; window.DpiChanged += dpiChanged; window.Closed += closed;
        Refresh();
    }

    internal sealed class NativeIcon : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeIcon(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
