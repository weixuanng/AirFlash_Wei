using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
namespace AirFlash.App.Services;

internal sealed record ThemePalette(
    bool IsDark,
    Color Canvas,
    Color Surface,
    Color Text,
    Color Muted,
    Color Line,
    Color Accent,
    Color Tint,
    Color Error,
    Color AccentText,
    Color AccentForeground,
    Color Caption,
    Color CaptionText,
    Color Success,
    Color Warning)
{
    // Apple system colours: grouped background, label, secondary label,
    // separator, systemBlue, systemRed, systemGreen and systemOrange.
    public static ThemePalette For(bool dark) => dark
        ? new(true, ColorOf("#1C1C1E"), ColorOf("#2C2C2E"), ColorOf("#F5F5F7"), ColorOf("#98989D"), ColorOf("#3A3A3C"), ColorOf("#0A84FF"), ColorOf("#0F2A47"), ColorOf("#FF453A"), ColorOf("#409CFF"), ColorOf("#FFFFFF"), ColorOf("#1C1C1E"), ColorOf("#F5F5F7"), ColorOf("#30D158"), ColorOf("#FF9F0A"))
        : new(false, ColorOf("#F5F5F7"), ColorOf("#FFFFFF"), ColorOf("#1D1D1F"), ColorOf("#6E6E73"), ColorOf("#E5E5EA"), ColorOf("#007AFF"), ColorOf("#E8F1FF"), ColorOf("#FF3B30"), ColorOf("#0066CC"), ColorOf("#FFFFFF"), ColorOf("#F5F5F7"), ColorOf("#1D1D1F"), ColorOf("#34C759"), ColorOf("#FF9500"));

    private static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);
}

public sealed class ThemeService : IDisposable
{
    internal static event Action<ThemePalette>? ThemeChanged;
    internal static ThemePalette CurrentPalette { get; private set; } = ThemePalette.For(false);
    private string _mode = "system";

    public ThemeService()
    {
        Refresh();
        SystemEvents.UserPreferenceChanged += Changed;
    }

    public void SetPreference(string mode)
    {
        _mode = mode;
        Refresh();
    }

    private void Changed(object sender, UserPreferenceChangedEventArgs args) { if (_mode == "system") Application.Current.Dispatcher.BeginInvoke(Refresh); }

    private void Refresh()
    {
        var dark = _mode == "dark";
        if (!dark && _mode == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        ApplyPalette(dark);
    }

    public static void ApplyPalette(bool dark)
    {
        var palette = ThemePalette.For(dark);
        CurrentPalette = palette;

        SetBrush("CanvasBrush", palette.Canvas);
        SetBrush("SurfaceBrush", palette.Surface);
        SetBrush("TextBrush", palette.Text);
        SetBrush("MutedBrush", palette.Muted);
        SetBrush("LineBrush", palette.Line);
        SetBrush("AccentBrush", palette.Accent);
        SetBrush("TintBrush", palette.Tint);
        SetBrush("ErrorBrush", palette.Error);
        SetBrush("AccentForegroundBrush", palette.AccentForeground);
        SetBrush("CaptionBrush", palette.Caption);
        SetBrush("CaptionTextBrush", palette.CaptionText);
        SetBrush("SuccessBrush", palette.Success);
        SetBrush("WarningBrush", palette.Warning);
        // Glass: the panel lets a little of the desktop through, lit from the top
        // edge, and the cards on it are thin frosted layers, not solid tiles.
        var light = palette.IsDark ? Colors.White : Colors.White;
        var body = palette.Canvas;
        SetGradient("GlassBrush", (WithAlpha(Lift(body, palette.IsDark ? 34 : 0), 0xC7), 0), (WithAlpha(body, 0xCC), 0.45), (WithAlpha(body, 0xDB), 1));
        // What the card is tinted with once Windows is blurring what lies behind it.
        SetGradient("GlassClearBrush", (WithAlpha(Lift(body, palette.IsDark ? 34 : 0), 0x59), 0), (WithAlpha(body, 0x6B), 0.45), (WithAlpha(body, 0x85), 1));
        SetGradient("GlassEdgeBrush", (WithAlpha(light, palette.IsDark ? (byte)0xA6 : (byte)0xFF), 0), (WithAlpha(light, palette.IsDark ? (byte)0x26 : (byte)0x99), 0.5), (WithAlpha(light, palette.IsDark ? (byte)0x52 : (byte)0xD9), 1));
        SetGradient("GlassCardBrush", (WithAlpha(light, palette.IsDark ? (byte)0x30 : (byte)0xC4), 0), (WithAlpha(light, palette.IsDark ? (byte)0x12 : (byte)0x80), 1));
        SetGradient("GlassActiveBrush", (WithAlpha(palette.Accent, palette.IsDark ? (byte)0x57 : (byte)0x38), 0), (WithAlpha(palette.Accent, palette.IsDark ? (byte)0x2E : (byte)0x1F), 1));
        SetGradient("GlassSheenBrush", (WithAlpha(light, palette.IsDark ? (byte)0x3D : (byte)0x99), 0), (WithAlpha(light, palette.IsDark ? (byte)0x0F : (byte)0x26), 0.4), (WithAlpha(light, 0), 1));

        ApplyFluentResources(palette);
        ThemeChanged?.Invoke(palette);
    }

    public static void SetForVerification(bool dark)
    {
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        ApplyPalette(dark);
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= Changed;

    private static void ApplyFluentResources(ThemePalette palette)
    {
        SetColorAndBrush("SystemColorWindowColor", palette.Canvas);
        SetColorAndBrush("SystemColorWindowTextColor", palette.Text);
        SetColorAndBrush("SystemColorButtonFaceColor", palette.Surface);
        SetColorAndBrush("SystemColorButtonTextColor", palette.Text);
        SetColorAndBrush("SystemColorHighlightColor", palette.Accent);
        SetColorAndBrush("SystemColorHighlightTextColor", palette.AccentForeground);
        SetColorAndBrush("SystemColorGrayTextColor", palette.Muted);
        SetColorAndBrush("SystemColorHotlightColor", palette.AccentText);

        foreach (var key in new[]
        {
            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "AccentFillColorSelectedTextBackgroundBrush", "AccentButtonBackground", "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed", "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver",
            "AccentButtonBorderBrushPressed", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundFillIndeterminatePointerOver",
            "CheckBoxCheckBackgroundFillIndeterminatePressed", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeCheckedPressed", "CheckBoxCheckBackgroundStrokeIndeterminate", "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver",
            "CheckBoxCheckBackgroundStrokeIndeterminatePressed", "RadioButtonOuterEllipseCheckedFill", "RadioButtonCheckOuterEllipseCheckedFillPointerOver",
            "RadioButtonCheckOuterEllipseCheckedFillPressed", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver",
            "ToggleButtonBackgroundCheckedPressed", "SliderThumbBackground", "SliderThumbBackgroundPointerOver",
            "SliderThumbBackgroundPressed", "SliderOuterThumbBackground", "CalendarViewSelectedBackground",
            "CalendarViewSelectedBorderBrush", "ProgressBarForeground", "ProgressBarIndicatorAnimatedFill", "AccentControlElevationBorderBrush"
        }) SetBrush(key, palette.Accent);

        foreach (var key in new[]
        {
            "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
            "ListBoxItemSelectedForegroundThemeBrush", "CheckBoxCheckGlyphForeground", "RadioButtonCheckGlyphFill"
        }) SetBrush(key, palette.AccentText);

        foreach (var key in new[]
        {
            "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
            "CheckBoxCheckGlyphForegroundPressed", "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPressed"
        }) SetBrush(key, palette.AccentForeground);

        foreach (var key in new[]
        {
            "AccentFillColorDisabledBrush", "AccentButtonBackgroundDisabled", "AccentButtonBorderBrushDisabled",
            "CheckBoxCheckGlyphForegroundDisabled", "ToggleButtonBackgroundCheckedDisabled", "ToggleButtonBorderBrushCheckedDisabled",
            "RadioButtonOuterEllipseCheckedFillDisabled"
        }) SetBrush(key, WithAlpha(palette.Accent, (byte)(palette.IsDark ? 0x28 : 0x37)));

        foreach (var key in new[]
        {
            "AccentTextFillColorDisabledBrush", "AccentButtonForegroundDisabled", "ToggleButtonForegroundCheckedDisabled"
        }) SetBrush(key, WithAlpha(palette.AccentForeground, (byte)(palette.IsDark ? 0x87 : 0x5C)));

        SetColorAndBrush("AccentFillColorDisabled", WithAlpha(palette.Accent, (byte)(palette.IsDark ? 0x28 : 0x37)));
        SetColorAndBrush("AccentTextFillColorDisabled", WithAlpha(palette.AccentForeground, (byte)(palette.IsDark ? 0x87 : 0x5C)));
        SetBrush("TextControlSelectionHighlightColor", palette.Accent);
        SetBrush("ListBoxItemSelectedBackgroundThemeBrush", palette.Tint);
        SetBrush("ListBoxItemSelectedBackgroundPointerOverThemeBrush", palette.Tint);
        SetBrush("ListBoxItemSelectedBackgroundPressedThemeBrush", palette.Tint);
        SetBrush("ProgressBarBackground", WithAlpha(palette.Accent, (byte)(palette.IsDark ? 0x8B : 0x37)));
        SetBrush("ProgressBarIndeterminateBackground", palette.Tint);
    }

    private static void SetColorAndBrush(string key, Color color)
    {
        Application.Current.Resources[key] = color;
        SetBrush(key + "Brush", color);
    }

    private static void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static Color Lift(Color color, int amount) => Color.FromArgb(color.A, (byte)Math.Min(255, color.R + amount), (byte)Math.Min(255, color.G + amount), (byte)Math.Min(255, color.B + amount));
    /// <summary>A top-to-bottom gradient resource.</summary>
    private static void SetGradient(string key, params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new(color, offset));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
