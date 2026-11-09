using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace AirFlash.App.Services;

internal static class WindowThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Track(Window window)
    {
        EventHandler sourceInitialized = (_, _) => Apply(window, ThemeService.CurrentPalette);
        Action<ThemePalette> themeChanged = palette => Apply(window, palette);
        EventHandler closed = (_, _) =>
        {
            window.SourceInitialized -= sourceInitialized;
            ThemeService.ThemeChanged -= themeChanged;
        };

        window.SourceInitialized += sourceInitialized;
        ThemeService.ThemeChanged += themeChanged;
        window.Closed += closed;
        Apply(window, ThemeService.CurrentPalette);
    }

    public static void Apply(Window window, ThemePalette palette)
    {
        if (!WindowsCapabilities.Current.WindowColors) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            ApplyAttributes(WindowsCapabilities.Current, palette, (attribute, value) =>
                DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)), AppPaths.Log);
        }
        catch (DllNotFoundException error) { AppPaths.Log($"Optional DWM unavailable: {error.Message}"); }
        catch (EntryPointNotFoundException error) { AppPaths.Log($"Optional DWM unavailable: {error.Message}"); }
    }

    internal static void ApplyAttributes(WindowsCapabilities capabilities, ThemePalette palette, Func<int, int, int> set, Action<string> log)
    {
        if (!capabilities.WindowColors) return;
        foreach (var (attribute, value) in new[] { (DwmwaUseImmersiveDarkMode, palette.IsDark ? 1 : 0),
            (DwmwaCaptionColor, ToColorRef(palette.Caption)), (DwmwaTextColor, ToColorRef(palette.CaptionText)) })
        {
            var result = set(attribute, value);
            if (result < 0) log($"Optional DWM attribute={attribute} failed hresult=0x{result:X8}");
        }
    }

    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2; // Mica
    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE is honoured from Windows 11 22H2.</summary>
    internal const int MicaMinimumBuild = 22621;
    internal static bool SupportsMica(WindowsCapabilities capabilities)
        => capabilities.WindowColors && capabilities.OsVersion.Build >= MicaMinimumBuild;

    /// <summary>Asks DWM for the Mica backdrop behind the whole window. Returns
    /// false, leaving the window opaque, when unsupported, in high contrast, or
    /// refused; the caller only makes its background transparent on success.</summary>
    public static bool TryApplyMica(Window window)
    {
        if (!SupportsMica(WindowsCapabilities.Current) || SystemParameters.HighContrast) return false;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target) return false;
        try
        {
            var backdrop = DwmsbtMainWindow;
            var result = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            if (result < 0) { AppPaths.Log($"Optional DWM attribute={DwmwaSystemBackdropType} failed hresult=0x{result:X8}"); return false; }
            // The backdrop only shows through client pixels when the frame covers them.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            result = DwmExtendFrameIntoClientArea(handle, ref margins);
            if (result < 0) { AppPaths.Log($"Optional DWM frame extension failed hresult=0x{result:X8}"); return false; }
            target.BackgroundColor = Colors.Transparent;
            return true;
        }
        catch (DllNotFoundException error) { AppPaths.Log($"Optional DWM unavailable: {error.Message}"); return false; }
        catch (EntryPointNotFoundException error) { AppPaths.Log($"Optional DWM unavailable: {error.Message}"); return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
