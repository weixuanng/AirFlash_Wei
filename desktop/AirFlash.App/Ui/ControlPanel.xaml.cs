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
    public const double CompactWidth = 400;
    private readonly AppViewModel _viewModel;
    private bool _allowClose;
    private bool _placing;
    private DispatcherOperation? _placement;
    private DateTime _lastDismissed;
    public bool MenuOpen { get; set; }
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
