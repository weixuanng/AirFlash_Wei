using AirFlash.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.ViewModels;
namespace AirFlash.App.Ui;

public partial class ControlPanel : Window
{
    public const double CompactWidth = 320;
    private readonly AppViewModel _viewModel;
    private bool _allowClose;
    private bool _placing;
    private DispatcherOperation? _placement;
    private DateTime _lastDismissed;
    public bool MenuOpen { get; set; }
    private void HideReceiver_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReceiverViewModel row }) _viewModel.HideReceiver(row);
    }
    private void ShowHidden_Click(object sender, RoutedEventArgs e) => _viewModel.ShowHiddenReceivers();
    // The volume pill has no thumb: the pointer sets the level anywhere along it,
    // and keeps setting it while the button is held.
    private void VolumeSlider_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var slider = (System.Windows.Controls.Slider)sender;
        var radius = slider.ActualHeight / 2;
        slider.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, slider.ActualWidth, slider.ActualHeight), radius, radius);
        FillVolume(slider, animate: false); // a resize is not a change of level
    }
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => FillVolume((System.Windows.Controls.Slider)sender);
    private void VolumeSlider_Loaded(object sender, RoutedEventArgs e) => FillVolume((System.Windows.Controls.Slider)sender, animate: false);
    /// <summary>The filled part of the pill, as wide as the level.</summary>
    internal static void FillVolume(System.Windows.Controls.Slider slider, bool animate = true)
    {
        slider.ApplyTemplate();
        if (slider.Template?.FindName("PART_Fill", slider) is not FrameworkElement fill) return;
        var range = slider.Maximum - slider.Minimum;
        var fraction = range > 0 ? Math.Clamp((slider.Value - slider.Minimum) / range, 0, 1) : 0;
        // Never narrower than the rounded end, or a low level is clipped away.
        // The fill follows on a short spring, so a drag or a key press reads as one motion.
        Motion.Glide(fill, WidthProperty, fraction > 0 ? Math.Max(slider.ActualHeight, slider.ActualWidth * fraction) : 0, animate);
    }
    private static void SetVolumeFromPointer(System.Windows.Controls.Slider slider, MouseEventArgs e)
    {
        if (slider.ActualWidth <= 0) return;
        var fraction = Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0, 1);
        slider.Value = Math.Round(slider.Minimum + fraction * (slider.Maximum - slider.Minimum));
    }
    private void VolumeSlider_Down(object sender, MouseButtonEventArgs e)
    {
        var slider = (System.Windows.Controls.Slider)sender;
        if (!slider.CaptureMouse()) return;
        SetVolumeFromPointer(slider, e);
        e.Handled = true;
    }
    private void VolumeSlider_Move(object sender, MouseEventArgs e)
    {
        var slider = (System.Windows.Controls.Slider)sender;
        if (slider.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) SetVolumeFromPointer(slider, e);
    }
    private void VolumeSlider_Up(object sender, MouseButtonEventArgs e)
    {
        var slider = (System.Windows.Controls.Slider)sender;
        if (!slider.IsMouseCaptured) return;
        SetVolumeFromPointer(slider, e);
        slider.ReleaseMouseCapture();
        e.Handled = true;
    }
    private void VolumeSlider_Wheel(object sender, MouseWheelEventArgs e)
    {
        var slider = (System.Windows.Controls.Slider)sender;
        slider.Value = Math.Clamp(slider.Value + Math.Sign(e.Delta) * 4, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }
    public TrayService? Tray { get; set; }
    public event Action? QuitRequested;
    public ControlPanel(AppViewModel viewModel)
    {
        InitializeComponent(); _viewModel = viewModel; DataContext = viewModel; WindowThemeService.Track(this); AppIconService.Track(this);
        viewModel.Receivers.CollectionChanged += OnReceiversChanged;
        IsVisibleChanged += (_, _) => viewModel.SetPanelVisible(IsVisible);
        SizeChanged += (_, _) => { if (!_placing) RequestPlacement(); if (_blurred) PanelBackdrop.Resize(this, CardRadius); };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(AppViewModel.PanelBlurEnabled)) return;
            if (viewModel.PanelBlurEnabled) BlurOn(); else BlurOff();
        };
        _blurTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(260) };
        _blurTimer.Tick += (_, _) => { _blurTimer.Stop(); BlurOn(); };
        DpiChanged += (_, _) => { RequestPlacement(); if (_blurred) Dispatcher.BeginInvoke(() => { if (_blurred) PanelBackdrop.Resize(this, CardRadius); }, DispatcherPriority.Loaded); };
    }
    // ---- Blur. Windows blurs the whole window rectangle, so it is switched on only
    // once the card has grown to fill it, and off before the card shrinks away.
    // Until then, and whenever Windows refuses, a solid backing keeps the old look.
    private const double CardRadius = 18;
    private readonly DispatcherTimer _blurTimer;
    private bool _blurred, _entering, _leaving;
    private void BlurOn()
    {
        if (_blurred || _leaving || !IsVisible || !_viewModel.PanelBlurEnabled || !PanelBackdrop.Apply(this, CardRadius)) return;
        _blurred = true;
        if (Motion.Active) Backing.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        else Backing.Opacity = 0;
    }
    private void BlurOff()
    {
        _blurTimer.Stop();
        if (!_blurred) return;
        _blurred = false;
        Backing.BeginAnimation(OpacityProperty, null); Backing.Opacity = 1;
        PanelBackdrop.Remove(this);
    }
    /// <summary>Where the panel was opened from, as a fraction of its own size: the
    /// pointer when it is on or beside the panel (a tray click), else the taskbar edge.</summary>
    private Point? OpenedFrom()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || PresentationSource.FromVisual(this) is null) return null;
        var pointer = PointFromScreen(NativeWindowPlacement.Cursor());
        var (x, y) = (pointer.X / ActualWidth, pointer.Y / ActualHeight);
        return x is < -0.6 or > 1.6 || y is < -0.6 or > 1.6 ? null : new(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }
    /// <summary>Close the panel: it shrinks back to where it opened from, then hides.</summary>
    public void Dismiss()
    {
        if (!IsVisible || _leaving) return;
        _entering = false;
        BlurOff();
        _leaving = true;
        Motion.LeavePanel(Card, () => { if (!_leaving) return false; _leaving = false; Hide(); return true; });
    }
    public void ToggleFromTray()
    {
        if (IsVisible) Dismiss();
        else if (DateTime.UtcNow - _lastDismissed > TimeSpan.FromMilliseconds(240)) ShowPanel();
    }
    public void ShowPanel()
    {
        Width = CompactWidth;
        // Reopened while shrinking away: that exit no longer hides the panel.
        var opening = !IsVisible || _leaving;
        _leaving = false;
        if (opening)
        {
            // Unseen until it has been placed; the entrance starts from there.
            _entering = true;
            Card.BeginAnimation(OpacityProperty, null); Card.Opacity = Motion.Active ? 0 : 1;
        }
        Show(); RequestPlacement(); Activate();
    }
    public void RequestPlacement()
    {
        if (!IsVisible || _placement is { Status: DispatcherOperationStatus.Pending }) return;
        _placement = Dispatcher.BeginInvoke(() => { _placement = null; Place(); }, DispatcherPriority.Loaded);
    }
    public void Place()
    {
        if (!IsVisible) return;
        _placing = true;
        try
        {
            UiPerformance.Count("panel.place");
            var area = Tray?.WorkArea() ?? NativeWindowPlacement.CursorWorkArea();
            var maxDip = area.Rect.Height * .7 / area.Scale;
            var constraintsChanged = MaxHeight != maxDip || Width != CompactWidth;
            Width = CompactWidth;
            DeviceScroll.MaxHeight = Math.Max(80, maxDip - 280);
            MaxHeight = maxDip;
            if (constraintsChanged || ActualHeight <= 0) UpdateLayout();
            // Native placement converts this DIP width to monitor pixels once.
            // Never reuse a width fed back by a native resize or a DPI transition.
            NativeWindowPlacement.Place(this, area, CompactWidth, ActualHeight);
        }
        finally { _placing = false; }
        if (!_entering) return;
        _entering = false;
        Motion.EnterPanel(Card, OpenedFrom());
        if (Motion.Active) _blurTimer.Start(); else BlurOn();
    }
    private void OnDeactivated(object sender, EventArgs args)
    {
        Dispatcher.BeginInvoke(() => { if (!IsActive && !MenuOpen) { _lastDismissed = DateTime.UtcNow; Dismiss(); } }, DispatcherPriority.Background);
    }
    private void OnActionHintUpdated(object sender, DataTransferEventArgs args)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (PairingActionHint.IsVisible && PairingActionHint.Text.Length > 0)
                (UIElementAutomationPeer.FromElement(PairingActionHint) ?? UIElementAutomationPeer.CreatePeerForElement(PairingActionHint))?
                    .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Loaded);
    }
    private void OnKeyDown(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) { Dismiss(); args.Handled = true; } }
    private void OnClosing(object? sender, CancelEventArgs args) { if (!_allowClose) { args.Cancel = true; Dismiss(); } }
    private void OnQuit(object sender, RoutedEventArgs args) => QuitRequested?.Invoke();
    private void OnReceiversChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => RequestPlacement();
    public void ShutdownPanel()
    {
        _viewModel.Receivers.CollectionChanged -= OnReceiversChanged;
        _placement?.Abort(); _blurTimer.Stop();
        _allowClose = true; Close();
    }
}
