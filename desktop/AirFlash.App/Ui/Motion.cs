using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace AirFlash.App.Ui;

/// <summary>A spring described the way Apple describes one: a duration and a
/// bounce (WWDC23 "Animate with springs"). Stiffness is (2π / duration)² and the
/// damping ratio is 1 − bounce. The curve is solved exactly, so it is the same
/// shape at 60, 120 or 144 frames a second. The animation that uses it must last
/// <see cref="Settle"/>, the time the spring takes to come within 0.1 % of rest.</summary>
public sealed class SpringEase : EasingFunctionBase
{
    private readonly double _omega, _zeta, _settle;
    public SpringEase() : this(0.5, 0.15) { }
    public SpringEase(double duration, double bounce)
    {
        _omega = 2 * Math.PI / duration;
        _zeta = Math.Clamp(1 - bounce, 0.05, 1);
        // Under-damped: the envelope e^(-ζωt) reaches 0.001. Critically damped: (1 + ωt)e^(-ωt) does at ωt ≈ 9.23.
        _settle = _zeta < 1 ? Math.Log(1000) / (_zeta * _omega) : 9.2334 / _omega;
        EasingMode = EasingMode.EaseIn; // EaseInCore is already the final shape.
    }
    public TimeSpan Settle => TimeSpan.FromSeconds(_settle);

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var time = t * _settle;
        if (_zeta >= 1) return 1 - Math.Exp(-_omega * time) * (1 + _omega * time);
        var damped = _omega * Math.Sqrt(1 - _zeta * _zeta);
        return 1 - Math.Exp(-_zeta * _omega * time) * (Math.Cos(damped * time) + _zeta * _omega / damped * Math.Sin(damped * time));
    }
    protected override Freezable CreateInstanceCore() => new SpringEase(2 * Math.PI / _omega, 1 - _zeta);
}

/// <summary>Subtle motion shared by the panel. Everything here is render-only
/// (opacity and render transforms), never layout. Looping animations run only
/// while their element is visible, so a hidden panel costs no CPU.</summary>
public static class Motion
{
    /// <summary>Off for automated verification runs so layout checks see final
    /// positions, and whenever Windows turns client-area animations off.</summary>
    public static bool Enabled { get; set; } = true;
    internal static bool Active => Enabled && SystemParameters.ClientAreaAnimation;
    // SwiftUI's presets: snappy (0.5 s, bounce 0.15) moves things, bouncy (0.5 s, 0.3) answers a press.
    private static readonly SpringEase Snappy = new(0.5, 0.15);
    private static readonly SpringEase Bouncy = new(0.5, 0.3);
    // No overshoot: for a level that must never read past its target.
    private static readonly SpringEase Smooth = new(0.35, 0);
    internal static readonly TimeSpan SmoothTime = Smooth.Settle;
    internal static readonly IEasingFunction SmoothCurve = Frozen(Smooth);
    internal static readonly TimeSpan Spring = Snappy.Settle;
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StaggerBase = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(35);
    private const int StaggerCap = 8;
    private const double RiseDip = 8;
    internal static readonly IEasingFunction SpringCurve = Frozen(Snappy);
    private static readonly IEasingFunction BouncyCurve = Frozen(Bouncy);
    internal static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Sine = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static IEasingFunction Frozen(EasingFunctionBase ease) { ease.Freeze(); return ease; }

    // ---- Frame rate. WPF steps its animations 60 times a second unless told
    // otherwise, which shows as judder on a 120 Hz screen. Ask for the fastest
    // attached display's rate instead. Must run before the first animation exists.
    public static int FrameRate { get; private set; } = 60;
    public static void MatchDisplayRefreshRate()
    {
        int rate;
        try { rate = DisplayRate.Fastest(); } catch (Exception) { return; }
        ApplyFrameRate(Math.Clamp(rate, 60, 240));
    }
    /// <summary>False when WPF refuses; animations then stay at 60, which is cosmetic.</summary>
    internal static bool ApplyFrameRate(int rate)
    {
        if (rate <= 60 || FrameRate != 60) return FrameRate == rate;
        try { Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(rate)); }
        catch (Exception) { return false; }
        FrameRate = rate;
        return true;
    }

    // ---- Press: a row sinks to 97 % under the pointer and springs back on release.
    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPressChanged));
    public static bool GetPress(DependencyObject element) => (bool)element.GetValue(PressProperty);
    public static void SetPress(DependencyObject element, bool value) => element.SetValue(PressProperty, value);

    private static void OnPressChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement target) return;
        target.PreviewMouseLeftButtonDown -= OnPressDown;
        target.PreviewMouseLeftButtonUp -= OnPressUp;
        target.MouseLeave -= OnPressUp;
        if (args.NewValue is not true) return;
        target.PreviewMouseLeftButtonDown += OnPressDown;
        target.PreviewMouseLeftButtonUp += OnPressUp;
        target.MouseLeave += OnPressUp;
    }
    private static ScaleTransform PressScale(UIElement target)
    {
        if (target.RenderTransform is ScaleTransform { IsFrozen: false } scale) return scale;
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        target.RenderTransform = scale = new ScaleTransform();
        return scale;
    }
    private static void OnPressDown(object sender, System.Windows.Input.MouseButtonEventArgs args)
    {
        if (sender is UIElement target && Active) Scale(PressScale(target), 0.97, TimeSpan.FromMilliseconds(120), EaseOut);
    }
    private static void OnPressUp(object sender, System.Windows.Input.MouseEventArgs args)
    {
        if (sender is not UIElement { RenderTransform: ScaleTransform { IsFrozen: false } scale }) return;
        if (!Active) { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); return; }
        if (scale.ScaleX < 1) Scale(scale, 1, Bouncy.Settle, BouncyCurve);
    }
    private static void Scale(ScaleTransform scale, double to, TimeSpan duration, IEasingFunction ease)
    {
        // No From: a release mid-press starts from wherever the row has got to.
        var animation = new DoubleAnimation(to, duration) { EasingFunction = ease };
        animation.Freeze();
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    // ---- Panel entrance: rises 10 DIP from the taskbar, scales up from 96 % and fades in.
    public static void EnterPanel(UIElement panel)
    {
        if (!Active) { panel.BeginAnimation(UIElement.OpacityProperty, null); return; }
        var (scale, offset) = Transforms(panel);
        panel.BeginAnimation(UIElement.OpacityProperty, To(0, 1, Fade, EaseOut));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, To(0.96, 1, Spring, SpringCurve));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, To(0.96, 1, Spring, SpringCurve));
        offset.BeginAnimation(TranslateTransform.YProperty, To(10, 0, Spring, SpringCurve));
    }
    private static (ScaleTransform, TranslateTransform) Transforms(UIElement element)
    {
        if (element.RenderTransform is TransformGroup { IsFrozen: false } group && group.Children.Count == 2
            && group.Children[0] is ScaleTransform scale && group.Children[1] is TranslateTransform offset) return (scale, offset);
        scale = new ScaleTransform(); offset = new TranslateTransform();
        element.RenderTransformOrigin = new Point(0.5, 1);
        element.RenderTransform = new TransformGroup { Children = { scale, offset } };
        return (scale, offset);
    }
    private static DoubleAnimation To(double from, double to, TimeSpan duration, IEasingFunction ease)
    {
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        animation.Freeze();
        return animation;
    }

    // ---- Staggered list entrance, replayed each time the list becomes visible.
    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnStaggerChanged));
    public static bool GetStagger(DependencyObject element) => (bool)element.GetValue(StaggerProperty);
    public static void SetStagger(DependencyObject element, bool value) => element.SetValue(StaggerProperty, value);

    private static void OnStaggerChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ItemsControl items) return;
        items.IsVisibleChanged -= OnItemsVisibleChanged;
        if (args.NewValue is true) items.IsVisibleChanged += OnItemsVisibleChanged;
    }
    private static void OnItemsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not true || !Active || sender is not ItemsControl items) return;
        for (var i = 0; i < items.Items.Count; i++)
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is UIElement row) Enter(row, i);
    }
    private static void Enter(UIElement element, int index)
    {
        var begin = StaggerBase + StaggerStep * Math.Min(index, StaggerCap);
        if (element.RenderTransform is not TranslateTransform offset || offset.IsFrozen)
            element.RenderTransform = offset = new TranslateTransform();
        // Hold the start value through the stagger delay so rows never flash in
        // before their turn; FillBehavior.Stop hands control back to the base value.
        element.BeginAnimation(UIElement.OpacityProperty, Frames(0, 1, begin, Fade + Fade, EaseOut));
        offset.BeginAnimation(TranslateTransform.YProperty, Frames(RiseDip, 0, begin, Spring, SpringCurve));
    }
    private static DoubleAnimationUsingKeyFrames Frames(double from, double to, TimeSpan begin, TimeSpan length, IEasingFunction ease)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = begin + length, FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(begin)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(begin + length), ease));
        animation.Freeze();
        return animation;
    }

    // ---- Now-playing level bars: each child of the panel bounces on its own period.
    public static readonly DependencyProperty PlayingProperty = DependencyProperty.RegisterAttached(
        "Playing", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLoopChanged));
    public static bool GetPlaying(DependencyObject element) => (bool)element.GetValue(PlayingProperty);
    public static void SetPlaying(DependencyObject element, bool value) => element.SetValue(PlayingProperty, value);

    // ---- Breathing: a slow opacity pulse while something is in progress.
    public static readonly DependencyProperty BreatheProperty = DependencyProperty.RegisterAttached(
        "Breathe", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLoopChanged));
    public static bool GetBreathe(DependencyObject element) => (bool)element.GetValue(BreatheProperty);
    public static void SetBreathe(DependencyObject element, bool value) => element.SetValue(BreatheProperty, value);

    private static readonly double[] BarPeriods = [0.46, 0.62, 0.38, 0.54];
    // Still bars (animations off) keep a varied, readable shape.
    private static readonly double[] BarStill = [0.55, 1, 0.7, 0.85];
    private const double BarRest = 0.35;

    private static void OnLoopChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement target) return;
        target.IsVisibleChanged -= OnLoopVisibleChanged;
        if (GetPlaying(target) || GetBreathe(target)) target.IsVisibleChanged += OnLoopVisibleChanged;
        UpdateLoops(target);
    }
    private static void OnLoopVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is UIElement target) UpdateLoops(target);
    }
    private static void UpdateLoops(UIElement target)
    {
        var run = Active && target.IsVisible;
        if (GetPlaying(target) && target is Panel bars)
            for (var i = 0; i < bars.Children.Count; i++)
            {
                var bar = bars.Children[i];
                if (bar.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
                {
                    bar.RenderTransformOrigin = new Point(0.5, 1);
                    bar.RenderTransform = scale = new ScaleTransform();
                }
                scale.ScaleY = Active ? BarRest : BarStill[i % BarStill.Length];
                if (run && GetPlaying(target))
                {
                    var bounce = new DoubleAnimation(BarRest, 1, TimeSpan.FromSeconds(BarPeriods[i % BarPeriods.Length]))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Sine };
                    bounce.Freeze();
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
                }
                else scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        if (run && GetBreathe(target))
        {
            var pulse = new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(850))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Sine };
            pulse.Freeze();
            target.BeginAnimation(UIElement.OpacityProperty, pulse);
        }
        else target.BeginAnimation(UIElement.OpacityProperty, null);
    }

    // ---- Crossfade: text eases in whenever its binding delivers a new value.
    // The binding needs NotifyOnTargetUpdated=True.
    public static readonly DependencyProperty FadeOnUpdateProperty = DependencyProperty.RegisterAttached(
        "FadeOnUpdate", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeChanged));
    public static bool GetFadeOnUpdate(DependencyObject element) => (bool)element.GetValue(FadeOnUpdateProperty);
    public static void SetFadeOnUpdate(DependencyObject element, bool value) => element.SetValue(FadeOnUpdateProperty, value);

    private static void OnFadeChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement target) return;
        target.RemoveHandler(Binding.TargetUpdatedEvent, (EventHandler<DataTransferEventArgs>)OnTargetUpdated);
        if (args.NewValue is true) target.AddHandler(Binding.TargetUpdatedEvent, (EventHandler<DataTransferEventArgs>)OnTargetUpdated);
    }
    private static void OnTargetUpdated(object? sender, DataTransferEventArgs args)
    {
        if (sender is UIElement target && Active && target.IsVisible)
            target.BeginAnimation(UIElement.OpacityProperty, To(0.15, 1, Fade + Fade, EaseOut));
    }
}

/// <summary>Refresh rate of the fastest display attached to the desktop.</summary>
internal static class DisplayRate
{
    public static int Fastest()
    {
        var best = 0;
        var device = new DisplayDevice { Size = System.Runtime.InteropServices.Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & 1) == 0) continue; // not part of the desktop
            var mode = new DevMode { Size = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(device.Name, -1, ref mode)) best = Math.Max(best, (int)mode.DisplayFrequency);
        }
        return best;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string Text;
        public uint StateFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DevMode
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode info);
}
