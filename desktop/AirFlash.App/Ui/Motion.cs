using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace AirFlash.App.Ui;

/// <summary>Critically shaped spring curve (damped second-order step response)
/// normalised so it settles within the animation's duration. Damping 0.8
/// matches the default SwiftUI spring: a fast start, ~1.5 % overshoot, soft landing.</summary>
public sealed class SpringEase : EasingFunctionBase
{
    private const double Damping = 0.8;
    // Envelope e^(-ζωt) reaches 0.001 at t = 1, so the curve has settled.
    private static readonly double Omega = Math.Log(1000) / Damping;
    private static readonly double OmegaD = Omega * Math.Sqrt(1 - Damping * Damping);

    public SpringEase() => EasingMode = EasingMode.EaseIn; // EaseInCore is already the final shape.

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var decay = Math.Exp(-Damping * Omega * t);
        return 1 - decay * (Math.Cos(OmegaD * t) + Damping * Omega / OmegaD * Math.Sin(OmegaD * t));
    }
    protected override Freezable CreateInstanceCore() => new SpringEase();
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
    internal static readonly TimeSpan Spring = TimeSpan.FromMilliseconds(520);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StaggerBase = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(35);
    private const int StaggerCap = 8;
    private const double RiseDip = 8;
    internal static readonly IEasingFunction SpringCurve = Frozen(new SpringEase());
    internal static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Sine = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static IEasingFunction Frozen(EasingFunctionBase ease) { ease.Freeze(); return ease; }

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
        if (target is Panel bars)
            for (var i = 0; i < bars.Children.Count; i++)
            {
                var bar = bars.Children[i];
                if (bar.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
                {
                    bar.RenderTransformOrigin = new Point(0.5, 1);
                    bar.RenderTransform = scale = new ScaleTransform(1, BarRest);
                }
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
