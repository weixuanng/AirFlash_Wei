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
    // The volume pill has no thumb: the pointer sets the level anywhere along it,
    // and keeps setting it while the button is held.
    private void VolumeSlider_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var slider = (System.Windows.Controls.Slider)sender;
        var radius = slider.ActualHeight / 2;
        slider.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, slider.ActualWidth, slider.ActualHeight), radius, radius);
        FillVolume(slider);
    }
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => FillVolume((System.Windows.Controls.Slider)sender);
    private void VolumeSlider_Loaded(object sender, RoutedEventArgs e) => FillVolume((System.Windows.Controls.Slider)sender);
    /// <summary>The filled part of the pill, as wide as the level.</summary>
    internal static void FillVolume(System.Windows.Controls.Slider slider)
    {
        slider.ApplyTemplate();
        if (slider.Template?.FindName("PART_Fill", slider) is not FrameworkElement fill) return;
        var range = slider.Maximum - slider.Minimum;
        var fraction = range > 0 ? Math.Clamp((slider.Value - slider.Minimum) / range, 0, 1) : 0;
        fill.Width = Math.Max(0, slider.ActualWidth * fraction);
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
        SizeChanged += (_, _) => { if (!_placing) RequestPlacement(); };
        DpiChanged += (_, _) => RequestPlacement();
    }
    public void ToggleFromTray()
    {
        if (IsVisible) Hide();
        else if (DateTime.UtcNow - _lastDismissed > TimeSpan.FromMilliseconds(240)) ShowPanel();
    }
    public void ShowPanel()
    {
        Width = CompactWidth;
        var opening = !IsVisible;
        Show(); RequestPlacement(); Activate();
        if (opening) Motion.EnterPanel(Card);
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
    }
    private void OnDeactivated(object sender, EventArgs args)
    {
        Dispatcher.BeginInvoke(() => { if (!IsActive && !MenuOpen) { _lastDismissed = DateTime.UtcNow; Hide(); } }, DispatcherPriority.Background);
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
    private void OnKeyDown(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) { Hide(); args.Handled = true; } }
    private void OnClosing(object? sender, CancelEventArgs args) { if (!_allowClose) { args.Cancel = true; Hide(); } }
    private void OnQuit(object sender, RoutedEventArgs args) => QuitRequested?.Invoke();
    private void OnReceiversChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => RequestPlacement();
    public void ShutdownPanel()
    {
        _viewModel.Receivers.CollectionChanged -= OnReceiversChanged;
        _placement?.Abort();
        _allowClose = true; Close();
    }
}
