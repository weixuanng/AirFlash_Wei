using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace AirFlash.App.Ui;

/// <summary>On-screen level for the speaker's volume while the keyboard keys drive it.</summary>
public partial class VolumeHud : Window
{
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(220);
    private readonly DispatcherTimer _hide;
    private bool _shown;
    /// <summary>Width the fill is heading for, in device-independent pixels.</summary>
    internal double FillTarget { get; private set; }

    public VolumeHud()
    {
        InitializeComponent();
        _hide = new(DispatcherPriority.Background, Dispatcher) { Interval = Linger };
        _hide.Tick += (_, _) => Dismiss();
        SourceInitialized += (_, _) =>
        {
            // Never activated, never hit by the pointer, absent from Alt+Tab.
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, ExtendedStyle, new IntPtr(GetWindowLongPtr(handle, ExtendedStyle).ToInt64() | NoActivate | Transparent | ToolWindow));
        };
    }

    /// <summary>Show, or update while showing, the level on a 0 to 100 scale.</summary>
    public void Present(double volume)
    {
        var level = Math.Clamp(volume, 0, 100);
        Level.Text = Math.Round(level).ToString(System.Globalization.CultureInfo.CurrentCulture);
        if (!IsVisible) { Show(); UpdateLayout(); }
        Place();
        FillTarget = Math.Max(0, Track.ActualWidth) * level / 100;
        var entering = !_shown;
        _shown = true;
        _hide.Stop(); _hide.Start();
        if (!Motion.Active)
        {
            Stop(Capsule, OpacityProperty, 1); Stop(Fill, WidthProperty, FillTarget);
            Stop(CapsuleScale, ScaleTransform.ScaleXProperty, 1); Stop(CapsuleScale, ScaleTransform.ScaleYProperty, 1); Stop(CapsuleOffset, TranslateTransform.YProperty, 0);
            return;
        }
        // The fill glides to each new level, so a held key reads as one motion.
        Run(Fill, WidthProperty, FillTarget, Motion.SmoothTime, Motion.SmoothCurve, entering ? 0 : null);
        // Coming back while fading out, the capsule returns from wherever it is.
        Run(Capsule, OpacityProperty, 1, TimeSpan.FromMilliseconds(160), Motion.EaseOut);
        if (!entering) return;
        Run(CapsuleScale, ScaleTransform.ScaleXProperty, 1, Motion.Spring, Motion.SpringCurve, 0.86);
        Run(CapsuleScale, ScaleTransform.ScaleYProperty, 1, Motion.Spring, Motion.SpringCurve, 0.86);
        Run(CapsuleOffset, TranslateTransform.YProperty, 0, Motion.Spring, Motion.SpringCurve, 10);
    }

    private void Dismiss()
    {
        _hide.Stop();
        if (!_shown) return;
        _shown = false;
        if (!Motion.Active) { Stop(Capsule, OpacityProperty, 0); Hide(); return; }
        var fade = new DoubleAnimation(0, FadeOut) { EasingFunction = Motion.EaseOut };
        // A key pressed during the fade shows the capsule again; only a finished fade hides the window.
        fade.Completed += (_, _) => { if (!_shown) Hide(); };
        Capsule.BeginAnimation(OpacityProperty, fade);
        Run(CapsuleScale, ScaleTransform.ScaleXProperty, 0.94, FadeOut, Motion.EaseOut);
        Run(CapsuleScale, ScaleTransform.ScaleYProperty, 0.94, FadeOut, Motion.EaseOut);
    }

    /// <summary>Centred along the bottom of the work area of the primary screen.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 28;
    }

    private static void Run(IAnimatable target, DependencyProperty property, double to, TimeSpan duration, IEasingFunction ease, double? from = null)
    {
        var animation = new DoubleAnimation { To = to, Duration = duration, EasingFunction = ease };
        if (from is { } start) animation.From = start;
        animation.Freeze();
        target.BeginAnimation(property, animation);
    }
    private static void Stop(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    private const int ExtendedStyle = -20;
    private const long Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
