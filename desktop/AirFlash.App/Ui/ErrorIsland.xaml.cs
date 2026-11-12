using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace AirFlash.App.Ui;

/// <summary>The panel's status island, shaped like the iPhone Dynamic Island. It
/// appears when <see cref="IsOpen"/> turns true as a compact pill sized to its
/// text. An error (<see cref="Tone"/> "error") springs open with its message and
/// settles back after a few seconds; any state opens and closes on click, and the
/// pill shrinks away when there is nothing to show.
/// Only sizes, opacity and a render scale animate; nothing runs while closed.</summary>
public partial class ErrorIsland : UserControl
{
    private const double CompactHeight = 32, MinCompact = 56, MaxCompact = 112;
    // The panel's content is 290 DIP wide and the island is centred in it. Open, it
    // spans 70 to 220: over the title, as the iPhone's covers its status bar, but short
    // of the settings button at 222, which must stay reachable under a lasting error.
    private const double ExpandedWidth = 150, ExpandedHeight = 88;
    /// <summary>The compact pill hugs its mark and text.</summary>
    private double CompactWidth
    {
        get
        {
            Compact.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Clamp(Math.Ceiling(Compact.DesiredSize.Width), MinCompact, MaxCompact);
        }
    }
    private bool Alarming => Tone == "error";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(4.5);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(220);
    private readonly DispatcherTimer _settle;
    private bool _expanded;
    private bool _syncing;

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(ErrorIsland), new PropertyMetadata(false, (d, _) => ((ErrorIsland)d).Sync(replay: false)));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ErrorIsland), new PropertyMetadata("", (d, e) => ((ErrorIsland)d).TitleChanged((string?)e.OldValue)));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(string), typeof(ErrorIsland), new PropertyMetadata("", (d, e) => ((ErrorIsland)d).ToneChanged(!Equals(e.OldValue, e.NewValue))));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(ErrorIsland), new PropertyMetadata("", (d, _) => ((ErrorIsland)d).UpdateText()));
    /// <summary>Animatable corner radius; CornerRadius itself is not a double.</summary>
    private static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        "Radius", typeof(double), typeof(ErrorIsland), new PropertyMetadata(CompactHeight / 2, (d, e) => ((ErrorIsland)d).Pill.CornerRadius = new CornerRadius((double)e.NewValue)));

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    /// <summary>error, wait, busy, live or standby: picks the mark, and whether the island opens by itself.</summary>
    public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    public ErrorIsland()
    {
        InitializeComponent();
        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = Settle };
        _settle.Tick += (_, _) => { _settle.Stop(); if (IsOpen) Morph(expanded: false); };
        // Replay the full entrance whenever the panel is reopened with an error showing.
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) Sync(replay: true);
            else _settle.Stop();
        };
    }

    private void TitleChanged(string? previous)
    {
        UpdateText();
        Restate(changed: !string.Equals(previous, Title, StringComparison.Ordinal));
    }
    /// <summary>The wording changed while showing: a new error opens the island
    /// again, anything else re-fits the pill (or stays open, if opened by hand).</summary>
    private void Restate(bool changed)
    {
        if (changed && IsOpen && IsVisible) Morph(expanded: Alarming || _expanded);
    }
    /// <summary>A different state: only an error is open by itself. An island left
    /// open by an earlier error closes, so it cannot sit open over the header for good.</summary>
    private void ToneChanged(bool changed)
    {
        if (changed && IsOpen && IsVisible) Morph(expanded: Alarming);
    }
    private void UpdateText()
    {
        CompactText.Text = Title;
        TitleText.Text = Title;
        MessageText.Text = Message;
        // The pill trims long wording; the tooltip always has all of it.
        Pill.ToolTip = Message.Length > 0 ? $"{Title}\n{Message}" : Title.Length > 0 ? Title : null;
        AutomationProperties.SetName(this, Message.Length > 0 ? $"{Title}. {Message}" : Title);
        if (IsOpen) Announce();
    }
    // Screen readers hear each new error once.
    private void Announce()
    {
        if (!IsVisible || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        Dispatcher.BeginInvoke(() =>
            (UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this))?
                .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged), DispatcherPriority.Loaded);
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnGotKeyboardFocus(args);
        Pill.BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)); Pill.BorderThickness = new Thickness(2);
    }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnLostKeyboardFocus(args);
        Pill.BorderBrush = null; Pill.BorderThickness = new Thickness(0);
    }
    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.Key is Key.Enter or Key.Space) { args.Handled = true; Morph(!_expanded); return; }
        base.OnKeyDown(args);
    }

    private void Sync(bool replay)
    {
        // Making the island visible raises IsVisibleChanged, which calls back here.
        if (_syncing) return;
        _syncing = true;
        try { SyncCore(replay); } finally { _syncing = false; }
    }
    private void SyncCore(bool replay)
    {
        if (IsOpen)
        {
            if (Visibility != Visibility.Visible || replay)
            {
                Visibility = Visibility.Visible;
                Snap(MinCompact, CompactHeight - 6, compact: 0, expanded: 0, scale: Motion.Active ? 0.6 : 1, opacity: Motion.Active ? 0 : 1);
            }
            // Also reverses a close that is still fading out.
            if (Motion.Active) Animate(Pill, OpacityProperty, 1, Quick, Motion.EaseOut);
            else { Pill.BeginAnimation(OpacityProperty, null); Pill.Opacity = 1; }
            AnimateScale(1);
            Morph(expanded: Alarming);
            Announce();
        }
        else if (Visibility == Visibility.Visible) Close();
    }

    private void Morph(bool expanded)
    {
        _expanded = expanded;
        _settle.Stop();
        // Only an error closes itself again; a state opened by hand stays until clicked.
        if (expanded && Alarming && IsVisible) _settle.Start();
        var width = expanded ? ExpandedWidth : CompactWidth;
        var height = expanded ? ExpandedHeight : CompactHeight;
        if (!Motion.Active) { Snap(width, height, expanded ? 0 : 1, expanded ? 1 : 0, 1, 1); return; }
        Animate(Pill, WidthProperty, width, Motion.IslandTime, Motion.IslandCurve);
        Animate(Pill, HeightProperty, height, Motion.IslandTime, Motion.IslandCurve);
        Animate(this, RadiusProperty, expanded ? 24 : CompactHeight / 2, Motion.IslandTime, Motion.IslandCurve);
        // Content crossfades: the outgoing layer leaves fast, the incoming one waits a beat.
        Animate(Compact, OpacityProperty, expanded ? 0 : 1, Quick, Motion.EaseOut, expanded ? TimeSpan.Zero : Quick);
        Animate(Expanded, OpacityProperty, expanded ? 1 : 0, Quick, Motion.EaseOut, expanded ? Quick : TimeSpan.Zero);
    }

    private void Close()
    {
        _settle.Stop();
        _expanded = false;
        if (!Motion.Active) { Visibility = Visibility.Collapsed; return; }
        Animate(Pill, WidthProperty, MinCompact, Motion.Spring, Motion.SpringCurve);
        Animate(Pill, HeightProperty, CompactHeight - 6, Motion.Spring, Motion.SpringCurve);
        Animate(this, RadiusProperty, (CompactHeight - 6) / 2, Motion.Spring, Motion.SpringCurve);
        Animate(Compact, OpacityProperty, 0, Quick, Motion.EaseOut);
        Animate(Expanded, OpacityProperty, 0, Quick, Motion.EaseOut);
        AnimateScale(0.6);
        var fade = new DoubleAnimation(0, Quick) { EasingFunction = Motion.EaseOut };
        fade.Completed += (_, _) => { if (!IsOpen) Visibility = Visibility.Collapsed; };
        Pill.BeginAnimation(OpacityProperty, fade);
    }

    private void OnClick(object sender, MouseButtonEventArgs args)
    {
        args.Handled = true;
        Morph(!_expanded);
    }

    private void Snap(double width, double height, double compact, double expanded, double scale, double opacity)
    {
        Pill.BeginAnimation(WidthProperty, null); Pill.Width = width;
        Pill.BeginAnimation(HeightProperty, null); Pill.Height = height;
        BeginAnimation(RadiusProperty, null); SetValue(RadiusProperty, Math.Min(height / 2, 24));
        Compact.BeginAnimation(OpacityProperty, null); Compact.Opacity = compact;
        Expanded.BeginAnimation(OpacityProperty, null); Expanded.Opacity = expanded;
        Pill.BeginAnimation(OpacityProperty, null); Pill.Opacity = opacity;
        var transform = (ScaleTransform)Pill.RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null); transform.ScaleX = scale;
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null); transform.ScaleY = scale;
    }
    private void AnimateScale(double to)
    {
        var transform = (ScaleTransform)Pill.RenderTransform;
        if (!Motion.Active) { transform.ScaleX = transform.ScaleY = to; return; }
        Animate(transform, ScaleTransform.ScaleXProperty, to, Motion.Spring, Motion.SpringCurve);
        Animate(transform, ScaleTransform.ScaleYProperty, to, Motion.Spring, Motion.SpringCurve);
    }
    // Animations start from the current (possibly mid-flight) value and hold the end value.
    private static void Animate(DependencyObject target, DependencyProperty property, double to, TimeSpan duration, IEasingFunction ease, TimeSpan delay = default)
    {
        AnimationTimeline animation;
        if (delay > TimeSpan.Zero)
        {
            // Hold the current on-screen value through the delay, then ease to the target.
            var current = (double)target.GetValue(property);
            var frames = new DoubleAnimationUsingKeyFrames { Duration = delay + duration };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(delay)));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration), ease));
            animation = frames;
        }
        else animation = new DoubleAnimation(to, duration) { EasingFunction = ease };
        animation.Freeze();
        ((IAnimatable)target).BeginAnimation(property, animation);
    }
}
