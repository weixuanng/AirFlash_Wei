using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace AirFlash.App.Ui;

/// <summary>Subtle motion shared by the panel: one smooth ~0.5 s ease-out curve
/// and a staggered list entrance (110 ms base + 35 ms per row, capped at 8 rows).
/// Purely cosmetic; never changes layout, only opacity and a render offset.</summary>
public static class Motion
{
    /// <summary>Off for automated verification runs so layout checks see final
    /// positions, and whenever Windows turns client-area animations off.</summary>
    public static bool Enabled { get; set; } = true;
    private static bool Active => Enabled && SystemParameters.ClientAreaAnimation;
    private static readonly TimeSpan Smooth = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StaggerBase = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(35);
    private const int StaggerCap = 8;
    private const double RiseDip = 8;
    private static readonly IEasingFunction Ease = CreateEase();

    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnStaggerChanged));
    public static bool GetStagger(DependencyObject element) => (bool)element.GetValue(StaggerProperty);
    public static void SetStagger(DependencyObject element, bool value) => element.SetValue(StaggerProperty, value);

    private static IEasingFunction CreateEase()
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
    private static void OnStaggerChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ItemsControl items) return;
        items.IsVisibleChanged -= OnItemsVisibleChanged;
        if (args.NewValue is true) items.IsVisibleChanged += OnItemsVisibleChanged;
    }
    // Replays the entrance each time the list becomes visible (the panel opens).
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
        element.BeginAnimation(UIElement.OpacityProperty, Frames(0, 1, begin));
        offset.BeginAnimation(TranslateTransform.YProperty, Frames(RiseDip, 0, begin));
    }
    private static DoubleAnimationUsingKeyFrames Frames(double from, double to, TimeSpan begin)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = begin + Smooth, FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(begin)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(begin + Smooth), Ease));
        animation.Freeze();
        return animation;
    }
}
