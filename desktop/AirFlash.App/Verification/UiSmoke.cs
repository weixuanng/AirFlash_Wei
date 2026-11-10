using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App.Verification;
// Opt-in, isolated UI verification. Never constructs real discovery, audio or autostart services.
internal static class UiSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--output");
        var report = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Path.GetTempPath(), "airplay-ui-smoke", "report.json");
        var directory = Path.GetDirectoryName(report)!; Directory.CreateDirectory(directory);
        AppPaths.DataDirectory = Path.Combine(directory, "isolated-data");
        var checks = new List<string>();
        Check(!Program.NeedsDiagnosticIsolation([]) && !Program.NeedsDiagnosticIsolation(["--startup"]),
            "ordinary startup arguments preserve the application's normal data directory", checks);
        Check(!Program.NeedsDiagnosticIsolation(["--other-check"])
            && !Program.NeedsDiagnosticIsolation(["--output", "synthetic-output-check"]),
            "unknown check switches and check-suffixed option values do not redirect normal startup", checks);
        Check(new[] { "--discovery-perf", "--update-check", "--self-check", "--stability-check", "--compat-check", "--discovery-check", "--audio-check",
            "--ui-smoke", "--tray-smoke" }.All(mode => Program.NeedsDiagnosticIsolation([mode])),
            "every dispatched diagnostic mode selects isolated startup data without invoking its real service", checks);
        Check(string.Equals(Program.DiagnosticStartupDataDirectory, AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase),
            "startup diagnostic data uses the smoke output directory before the harness starts", checks);
        var startupLogs = Directory.GetFiles(Path.Combine(AppPaths.DataDirectory, "logs"), "wpf-*.log");
        var startupText = string.Join("\n", startupLogs.Select(File.ReadAllText));
        Check(startupText.Contains("Startup stage=platform", StringComparison.Ordinal)
            && startupText.Contains("Startup stage=wpf-resources ready", StringComparison.Ordinal),
            "both startup stages are recorded in isolated smoke logs", checks);
        if (args.Contains("--ui-ownership-smoke"))
        {
            try
            {
                await UiOwnershipRecovery.RunAsync(checks);
                App.WriteOutput(args, new { ok = true, checks, note = "Receiver ownership protection with simulated engine, audio and discovery." });
                return 0;
            }
            catch (Exception error) { App.WriteOutput(args, new { ok = false, checks, error = error.ToString() }); return 1; }
        }
        object? iconEnvironment = null;
        var store = new MemoryStore(); var engine = new MockFactory();
        await using var app = new AppViewModel(store, new MockDiscovery(), new MockAutostart(), new MockAudio(), engine, Application.Current.Dispatcher) { EngineVersion = "0.1.0（模拟）" };
        var panel = new ControlPanel(app);
        // Verification must never bind the production GUID to a test executable path.
        var guidIndex = Array.IndexOf(args, "--tray-guid");
        if (guidIndex >= 0 && guidIndex + 1 >= args.Length) throw new ArgumentException("--tray-guid requires an isolated GUID.");
        var trayGuid = guidIndex >= 0 ? Guid.Parse(args[guidIndex + 1]) : Guid.NewGuid();
        if (trayGuid == new Guid("f3f63f27-a6df-4a27-a942-b448e17dcd12")) throw new ArgumentException("Use an isolated verification GUID.");
        var checkUpdatesRequested = false; var aboutRequested = false; var quitRequestedFromTray = false;
        var windowIdentityForVerification = args.Contains("--tray-window-id");
        using var tray = new TrayService(panel, app, () => checkUpdatesRequested = true, () => aboutRequested = true, () => quitRequestedFromTray = true, trayGuid, windowIdentityForVerification); panel.Tray = tray;
        try
        {
            var instanceName = "verification-" + Guid.NewGuid().ToString("N");
            await using (var firstInstance = new SingleInstance(instanceName))
            await using (var secondInstance = new SingleInstance(instanceName))
            {
                var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                firstInstance.Listen(() => activated.TrySetResult());
                Check(firstInstance.IsOwner && !secondInstance.IsOwner, "single instance mutex ownership", checks);
                await secondInstance.NotifyExistingAsync(); await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
                checks.Add("second instance activation via current-user pipe");
            }
            ThemeService.SetForVerification(false); app.Start(); panel.ShowPanel(); await Pump();
            await Until(() => tray.IsRegistered && tray.TryGetRectangle(out _));
            Check(tray.TryGetRectangle(out var iconBounds) && iconBounds.Right > iconBounds.Left && iconBounds.Bottom > iconBounds.Top, "real tray registration has an icon rectangle", checks);
            Check(app.Receivers.Count == 2 && app.Receivers.All(r => r.Receiver.Online), "offline receivers excluded", checks);
            var area = tray.WorkArea(); GetWindowRect(new System.Windows.Interop.WindowInteropHelper(panel).Handle, out var bounds);
            Check(Math.Abs(bounds.Right - (area.Rect.Right - Math.Round(12 * area.Scale))) <= 2 && Math.Abs(bounds.Bottom - (area.Rect.Bottom - Math.Round(12 * area.Scale))) <= 2, "native panel aligns to monitor work area", checks);
            var clickParam = tray.UsesVersion4 ? new IntPtr(0x10400) : new IntPtr(0x202);
            var clickId = tray.UsesVersion4 ? IntPtr.Zero : new IntPtr(1);
            SendMessage(tray.WindowHandle, 0x8001, clickId, clickParam); await Pump();
            Check(!panel.IsVisible, "tray activation hides panel", checks);
            await Task.Delay(280); SendMessage(tray.WindowHandle, 0x8001, clickId, clickParam); await Pump();
            Check(panel.IsVisible, "tray activation opens panel", checks);
            if (args.Contains("--tray-smoke"))
            {
                var initialIdentity = tray.Identity.ToString();
                await VerifyTrayLifecycleAsync(tray, app, trayGuid, () => checkUpdatesRequested, () => aboutRequested, () => quitRequestedFromTray, checks, windowIdentityForVerification);
                App.WriteOutput(args, new { ok = true, checks, tray_guid = trayGuid, identity = initialIdentity, window_identity_for_verification = windowIdentityForVerification, path = Environment.ProcessPath, note = "Isolated tray verification; engine/audio/discovery/autostart services are simulated." });
                return 0;
            }
            var primary = Descendants(panel).OfType<Button>().Single(b => ReferenceEquals(b.Command, app.PrimaryCommand));
            Check(primary.Focus() && primary.IsKeyboardFocused, "primary connect button remains keyboard focusable", checks);
            Check(!app.PrimaryIsStop && app.PrimaryLabel == L.Format("Connect to {0}", app.Receivers[0].Receiver.Name) && primary.IsEnabled, "main button offers Connect when idle", checks);
            CheckPanelIcons(panel, app, checks);
            // The volume pill draws its own fill: half the level must be half the width.
            var pill = new System.Windows.Controls.Slider { Style = (Style)Application.Current.FindResource("VolumeSlider"), Minimum = 0, Maximum = 100, Value = 50 };
            pill.Measure(new Size(200, 26)); pill.Arrange(new Rect(0, 0, 200, 26));
            ControlPanel.FillVolume(pill);
            Check(pill.Template.FindName("PART_Fill", pill) is FrameworkElement { Width: > 99 and < 101 }, "volume pill fills in proportion to the level", checks);
            Render(panel, Path.Combine(directory, "panel-icons-idle.png"), 1);
            Invoke(primary);
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            CheckPanelIcons(panel, app, checks);
            Check(engine.Commands.Any(c => c.Text("command") == "start"), "panel start reaches engine", checks);
            Check(app.PrimaryIsStop && app.PrimaryLabel == L.Get("Disconnect") && primary.IsEnabled, "main button offers Disconnect while playing", checks);
            Invoke(primary);
            await Until(() => app.Snapshot.State == PlaybackState.Idle);
            CheckPanelIcons(panel, app, checks);
            Check(!app.PrimaryIsStop && app.PrimaryLabel == L.Format("Connect to {0}", app.Receivers[0].Receiver.Name) && primary.IsEnabled, "main button offers Connect when idle again", checks);
            Invoke(primary);
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            CheckPanelIcons(panel, app, checks);
            checks.Add("primary button completes connect/disconnect/connect with localized accessible labels");
            iconEnvironment = IconVerification.Run(panel, tray, checks, directory);
            foreach (var message in new[] { 0x001a, 0x007e })
            {
                SendMessage(tray.WindowHandle, (uint)message, IntPtr.Zero, IntPtr.Zero); await Pump();
            }
            GetWindowRect(tray.WindowHandle, out var trayBounds);
            var rectanglePointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeWindowPlacement.Rectangle>());
            try
            {
                Marshal.StructureToPtr(trayBounds, rectanglePointer, false);
                var trayDpi = AppIconService.WindowDpi(tray.WindowHandle);
                SendMessage(tray.WindowHandle, 0x02e0, new((long)(trayDpi | trayDpi << 16)), rectanglePointer);
                await Pump();
            }
            finally { Marshal.FreeHGlobal(rectanglePointer); }
            Check(tray.IconHandle != IntPtr.Zero, "display/settings/DPI notifications preserve tray icon", checks);
            tray.RemoveForVerification();
            SendMessage(tray.WindowHandle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); await Pump();
            Check(tray.IsRegisteredForVerification, "Explorer recreation registers tray icon again", checks);
            SendMessage(tray.WindowHandle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); await Pump();
            Check(tray.IsRegisteredForVerification, "repeated Explorer notification retains one registered tray icon", checks);
            foreach (var dark in new[] { false, true })
            {
                ThemeService.SetForVerification(dark); await Pump();
                CheckThemeColors(dark, checks);
                panel.ShowPanel(); await Pump();
                CheckPanelIcons(panel, app, checks);
                foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2d }) Render(panel, Path.Combine(directory, $"panel-{(dark ? "dark" : "light")}-{scale * 100}.png"), scale);
                var quit = Descendants(panel).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == L.Get("Quit"));
                var quitRequested = false;
                void OnQuit() => quitRequested = true;
                panel.QuitRequested += OnQuit;
                Invoke(quit); await Pump();
                panel.QuitRequested -= OnQuit;
                Check(quitRequested && quit.ContextMenu is null, "exit icon requests quit directly", checks);
            }
            checks.Add("panel rendered in light/dark at 100/125/150/175/200 percent");
            await app.StopAsync(); await Until(() => app.Snapshot.State == PlaybackState.Idle);
            panel.ShowPanel(); await Pump();
            CheckPanelIcons(panel, app, checks);
            Render(panel, Path.Combine(directory, "panel-icons-stopped.png"), 1);
            Check(engine.DisposedCount == engine.CreatedCount, "stop releases mock engine", checks);
            await app.StopAsync(); await Until(() => app.Snapshot.State == PlaybackState.Idle);
            panel.ShowPanel(); await Pump();
            CheckPanelIcons(panel, app, checks);
            Render(panel, Path.Combine(directory, "panel-icons-stopped.png"), 1);
            Check(engine.DisposedCount == engine.CreatedCount, "stop releases mock engine", checks);
            await VerifyStartupFailureAsync(checks, directory);
            await VerifyTrayLifecycleAsync(tray, app, trayGuid, () => checkUpdatesRequested, () => aboutRequested, () => quitRequestedFromTray, checks);
            App.WriteOutput(args, new { ok = true, checks, icon_environment = iconEnvironment, note = "All engine/audio/discovery/autostart services are simulated. DPI renders do not replace physical multimonitor QA." });
            return 0;
        }
        catch (Exception error) { App.WriteOutput(args, new { ok = false, checks, error = error.ToString() }); return 1; }
        finally { panel.ShutdownPanel(); }
    }
    private static async Task VerifyStartupFailureAsync(List<string> checks, string directory)
    {
        var attempts = 0;
        await using var engine = new EngineInitialization(_ => ++attempts == 1
            ? Task.FromException<string>(new UnauthorizedAccessException("simulated cache access denied"))
            : Task.FromResult("0.1.0"));
        var factory = new MockFactory();
        await using var app = new AppViewModel(new MemoryStore(), new MockDiscovery(), new MockAutostart(), new MockAudio(), factory, Application.Current.Dispatcher, engine);
        var panel = new ControlPanel(app);
        try
        {
            panel.ShowPanel(); app.Start(); await app.Startup; await Pump();
            var receiver = app.Receivers.First(r => r.Receiver.Online && r.Receiver.Complete);
            Check(panel.IsVisible && !receiver.CanPlay, "shell is visible before engine initialization", checks);
            await app.ToggleAsync(receiver.Receiver);
            Check(factory.CreatedCount == 0, "initializing engine rejects playback without spawning a process", checks);
            await engine.InitializeAsync(); await Pump();
            Check(app.EngineFailed && !receiver.CanPlay, "failed engine disables playback", checks);
            Check(app.RetryEngineCommand.CanExecute(null), "failed engine preserves retry", checks);
            using var missingDns = new WindowsDiscovery(dnsAvailable: () => false);
            string? failure = null; missingDns.Failed += value => failure = value; missingDns.Start();
            Check(failure is not null, "missing DNS-SD produces a recoverable diagnostic", checks);
            app.ShowError(new IOException("simulated discovery failure")); await Pump();
            Render(panel, Path.Combine(directory, "startup-failure.png"), 1);
            var messages = Descendants(panel).OfType<TextBlock>().Where(t => t.Text == app.Notice || t.Text == app.EngineMessage).ToArray();
            Check(messages.Length == 2 && messages[0].TranslatePoint(new(0, messages[0].ActualHeight), panel).Y <= messages[1].TranslatePoint(new(0, 0), panel).Y,
                "engine and discovery error banners remain readable without overlap", checks);
            var calls = 0; var dwmErrors = new List<string>();
            foreach (var build in new[] { 17763, 19044, 19045 })
                WindowThemeService.ApplyAttributes(new(new(10, 0, build), Architecture.X64, Architecture.X64, true), ThemeService.CurrentPalette,
                    (_, _) => { calls++; return 0; }, dwmErrors.Add);
            Check(calls == 0, "Windows 10 skips Windows 11 DWM attributes", checks);
            WindowThemeService.ApplyAttributes(new(new(10, 0, 22000), Architecture.X64, Architecture.X64, true), ThemeService.CurrentPalette,
                (_, _) => { calls++; return unchecked((int)0x80070057); }, dwmErrors.Add);
            Check(calls == 3 && dwmErrors.Count == 3 && panel.IsVisible, "failed optional DWM attributes are logged without closing the shell", checks);
            await engine.InitializeAsync(); await Pump();
            Check(app.EngineReady && receiver.CanPlay, "successful retry restores playback", checks);
        }
        finally { panel.ShutdownPanel(); }
        var store = new MemoryStore(); var preferences = store.Load(); preferences.StartAtLogin = true; store.Save(preferences);
        var startup = new MockAutostart();
        await using var audioFailure = new AppViewModel(store, new MockDiscovery(), startup, new MockAudio { FailDefault = true }, new MockFactory(), Application.Current.Dispatcher);
        audioFailure.SetPanelVisible(true); audioFailure.Start(); await audioFailure.Startup; await Pump();
        Check(startup.Enabled && audioFailure.Receivers.Count > 0 && audioFailure.Notice.Contains("mock audio failed", StringComparison.Ordinal),
            "audio initialization failure preserves autostart and discovery initialization", checks);
        await using var startupFailure = new AppViewModel(store, new MockDiscovery(), new MockAutostart { FailEnable = true }, new MockAudio(), new MockFactory(), Application.Current.Dispatcher);
        startupFailure.SetPanelVisible(true); startupFailure.Start(); await startupFailure.Startup; await Pump();
        Check(startupFailure.Receivers.Count > 0 && startupFailure.Notice.Contains("mock startup failed", StringComparison.Ordinal),
            "autostart initialization failure preserves discovery and the shell model", checks);
    }
    private static async Task VerifyTrayLifecycleAsync(TrayService tray, AppViewModel app, Guid guid, Func<bool> checkUpdatesRequested, Func<bool> aboutRequested, Func<bool> quitRequested, List<string> checks, bool windowIdentityForVerification = false)
    {
        // Simulate a shell rebuild without restarting the user's Explorer process.
        tray.RemoveForVerification();
        SendMessage(tray.WindowHandle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); await Pump();
        await Until(() => tray.IsRegistered && tray.TryGetRectangle(out _));
        Check(tray.TryGetRectangle(out _), "Explorer recreation restores tray geometry", checks);
        SendMessage(tray.WindowHandle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); await Pump();
        Check(tray.IsRegistered && tray.TryGetRectangle(out _), "repeated Explorer notification retains registered tray geometry", checks);
        SendMessage(tray.WindowHandle, 0x8001, tray.UsesVersion4 ? IntPtr.Zero : new(1), tray.UsesVersion4 ? new(0x1007b) : new(0x205));
        await Pump();
        var menu = tray.ActiveMenu ?? throw new InvalidOperationException("The tray context menu did not open.");
        var items = menu.Items.OfType<MenuItem>().ToArray();
        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(checkUpdatesRequested(), "tray menu checks for updates", checks);
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(aboutRequested(), "tray menu opens About", checks);
        items[^1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(quitRequested(), "tray menu requests quit", checks);
        menu.IsOpen = false; await Pump();
        var window = tray.WindowHandle;
        tray.Dispose();
        Check(!tray.IsRegistered && !tray.TryGetRectangle(out _), "disposed tray stops registration", checks);
        Check(!TrayRectangleExists(guid, tray.Identity, window), "disposed tray is removed from shell", checks);
        var originallyChinese = L.IsChinese;
        try
        {
            L.Initialize([originallyChinese ? "en-US" : "zh-CN"]);
            var replacement = new ControlPanel(app);
            using var rebuilt = new TrayService(replacement, app, () => { }, () => { }, () => { }, guid, windowIdentityForVerification);
            try
            {
                await Until(() => rebuilt.IsRegistered && rebuilt.TryGetRectangle(out _));
                Check(rebuilt.TryGetRectangle(out _), "tray is registered after language window rebuild", checks);
            }
            finally { rebuilt.Dispose(); replacement.ShutdownPanel(); }
        }
        finally { L.Initialize([originallyChinese ? "zh-CN" : "en-US"]); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrayIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    private static bool TrayRectangleExists(Guid guid, TrayIdentity identity, IntPtr window)
    {
        var identifier = new TrayIdentifier { Size = (uint)Marshal.SizeOf<TrayIdentifier>(), Window = window, Id = 1, Guid = identity == TrayIdentity.Guid ? guid : Guid.Empty };
        return Shell_NotifyIconGetRect(ref identifier, out _) == 0;
    }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref TrayIdentifier identifier, out NativeWindowPlacement.Rectangle rectangle);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeWindowPlacement.Rectangle rectangle);
    private static void Check(bool value, string message, List<string> checks) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
    private static void CheckPanelIcons(ControlPanel panel, AppViewModel app, List<string> checks)
    {
        var icons = Descendants(panel).OfType<System.Windows.Shapes.Path>()
            .Where(p => p.Name is "ReceiverIcon" or "PrimaryIcon").ToArray();
        Check(icons.Count(p => p.Name == "ReceiverIcon") == app.Receivers.Count
            && icons.Count(p => p.Name == "PrimaryIcon") == 1,
            "each receiver displays its own speaker vector and the panel has one primary icon", checks);
        foreach (var icon in icons)
        {
            var key = icon.Name switch
            {
                "ReceiverIcon" => "ReceiverIconGeometry",
                _ => app.PrimaryIsStop ? "StopIconGeometry" : "PlayIconGeometry"
            };
            Check(ReferenceEquals(icon.Data, panel.FindResource(key)) && !icon.Data.IsEmpty(),
                $"{icon.Name} uses its expected built-in {key}", checks);
            var pen = icon.Stroke is null ? null : new Pen(icon.Stroke, icon.StrokeThickness)
            {
                StartLineCap = icon.StrokeStartLineCap, EndLineCap = icon.StrokeEndLineCap, LineJoin = icon.StrokeLineJoin
            };
            var bounds = icon.RenderedGeometry.GetRenderBounds(pen);
            Check(icon.IsVisible && icon.ActualWidth > 0 && icon.ActualHeight > 0 && !bounds.IsEmpty
                && new Rect(0, 0, icon.ActualWidth, icon.ActualHeight).Contains(bounds),
                $"{icon.Name} is visible and fits inside its design canvas", checks);
            var bitmap = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(icon);
            var pixels = new byte[24 * 24 * 4]; bitmap.CopyPixels(pixels, 24 * 4, 0);
            Check(Enumerable.Range(0, 24 * 24).Any(i => pixels[i * 4 + 3] != 0),
                $"{icon.Name} renders nontransparent pixels without glyph lookup", checks);
            var brush = (icon.Stroke ?? icon.Fill) as SolidColorBrush;
            // Both glyphs sit on accent fills: the primary button and the receiver's accent tile.
            Check(brush?.Color == ResourceColor("AccentForegroundBrush"), $"{icon.Name} follows the current theme color", checks);
            if (icon.Name == "ReceiverIcon")
            {
                DependencyObject? parent = icon;
                do parent = VisualTreeHelper.GetParent(parent); while (parent is not null and not Border);
                Check(parent is Border { Background: SolidColorBrush tile } && tile.Color == ResourceColor("AccentBrush"),
                    "ReceiverIcon sits on an accent-coloured tile", checks);
            }
            if (icon.Name == "PrimaryIcon")
            {
                var button = Descendants(panel).OfType<Button>().Single(b => ReferenceEquals(b.Command, app.PrimaryCommand));
                Check(System.Windows.Automation.AutomationProperties.GetName(button) == app.PrimaryLabel
                    && (string?)button.ToolTip == app.PrimaryLabel,
                    "primary icon retains the accessible name and tooltip for its current state", checks);
            }
        }
    }
    private static void CheckThemeColors(bool dark, List<string> checks)
    {
        // Apple systemBlue: #0A84FF in dark mode, #007AFF in light mode.
        var expected = dark ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromRgb(0x00, 0x7A, 0xFF);
        Check(ResourceColor("AccentBrush") == expected, $"{(dark ? "dark" : "light")} custom accent is blue", checks);
        Check(ResourceColor("AccentFillColorDefaultBrush") == expected, $"{(dark ? "dark" : "light")} Fluent accent is blue", checks);
        Check(ResourceColor("SystemColorHighlightColorBrush") == expected, $"{(dark ? "dark" : "light")} highlight is blue", checks);
        Check(ResourceColor("ProgressBarForeground") == expected, $"{(dark ? "dark" : "light")} progress accent is blue", checks);
        Check(ResourceColor("SliderThumbBackground") == expected, $"{(dark ? "dark" : "light")} slider accent is blue", checks);
        Check(ResourceColor("SystemColorWindowColorBrush") != Colors.Magenta, $"{(dark ? "dark" : "light")} window color is not magenta", checks);
        Check(ResourceColor("SystemColorHighlightTextColorBrush") == Colors.White, $"{(dark ? "dark" : "light")} accent text is readable", checks);
    }
    private static Color ResourceColor(string key) => Application.Current.Resources[key] switch
    {
        SolidColorBrush brush => brush.Color,
        Color color => color,
        _ => throw new InvalidOperationException($"Resource '{key}' is not a color or brush.")
    };
    private static async Task Pump() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(70); }
    private static async Task Until(Func<bool> predicate)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!predicate()) await Task.Delay(20, limit.Token);
        await Pump();
    }
    private static void Invoke(Button button)
    {
        var peer = new ButtonAutomationPeer(button); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    internal static void Render(FrameworkElement window, string file, double scale)
    {
        window.UpdateLayout();
        var visual = window is Window host ? (FrameworkElement)host.Content : window;
        var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(file); encoder.Save(output);
    }
    internal sealed class MemoryStore : ISettingsStore
    {
        public int Saves;
        public int Resets;
        public TaskCompletionSource? SaveGate { get; set; }
        public TaskCompletionSource? SaveEntered { get; set; }
        public AppSettings Saved => _settings.Clone();
        // These fixtures were written for the upstream defaults; pin them so the
        // UI checks keep testing the explicit paths. The seamless defaults
        // (login item, forced reconnect, last-receiver auto-connect, takeover
        // resume) have their own unit tests.
        private AppSettings _settings = new() { AutoConnectOnDiscover = false, MuteWhileStreaming = false, ShowWiderVolume = false,
            StartAtLogin = false, ForceReconnect = false, MaxReconnectAttempts = 5, AutoConnectLastReceiver = false, ResumeAfterTakeover = false };
        public AppSettings Load() => _settings.Clone();
        public Task ResetAsync() { Resets++; _settings = new(); return Task.CompletedTask; }
        public void Save(AppSettings settings) { UiPerformance.Count("settings.save"); _settings = settings.Clone(); }
        public async Task SaveAsync(AppSettings settings)
        {
            Interlocked.Increment(ref Saves); SaveEntered?.TrySetResult();
            if (SaveGate is { } gate) await gate.Task;
            await Task.Run(() => Save(settings));
        }
    }
    internal sealed class MockAutostart : IAutostart
    {
        public bool FailEnable { get; set; }
        public bool FailDisable { get; set; }
        public bool Enabled { get; private set; }
        public void Set(bool enabled)
        {
            if (enabled ? FailEnable : FailDisable) throw new IOException("mock startup failed");
            Enabled = enabled;
        }
    }
    internal sealed class MockAudio : IAudioService
    {
        public event Action? EndpointsChanged;
        public int Enumerations;
        public int ActiveEnumerations, MaxEnumerations;
        public TaskCompletionSource? EnumerationGate { get; set; }
        public TaskCompletionSource? EnumerationEntered { get; set; }
        public IReadOnlyList<AudioEndpoint> Items = [new("endpoint-a", "扬声器 · Realtek Audio"), new("endpoint-b", "虚拟声卡 · VB-CABLE"), new("endpoint-c", "Voicemeeter Output", AudioEndpointKind.Recording)];
        public void NotifyEndpoints() => EndpointsChanged?.Invoke();
        public string? DefaultEndpoint { get; set; } = "endpoint-a";
        public bool FailDefault { get; set; }
        public Task<string?> GetDefaultEndpointIdAsync() => FailDefault ? Task.FromException<string?>(new IOException("mock audio failed")) : Task.FromResult(DefaultEndpoint);
        public async Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync()
        {
            UiPerformance.Count("audio.enumerate"); Enumerations++;
            ActiveEnumerations++; MaxEnumerations = Math.Max(MaxEnumerations, ActiveEnumerations);
            var items = Items.ToArray(); EnumerationEntered?.TrySetResult();
            try { if (EnumerationGate is { } gate) await gate.Task; return items; }
            finally { ActiveEnumerations--; }
        }
        public bool FailMute { get; set; }
        public int Restores;
        public Task MuteAsync(string? endpointId) => FailMute ? Task.FromException(new IOException("mock mute failed")) : Task.CompletedTask;
        public Task RestoreAsync() { Restores++; return Task.CompletedTask; }
    }
    internal sealed class MockDiscovery : IDiscoveryService
    {
        public event Action<bool>? RunningChanged;
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public event Action<IReadOnlyList<Receiver>>? Changed;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { if (Running) return; Running = true; Starts++; RunningChanged?.Invoke(true); Publish(); }
        public void Stop() { if (!Running) return; Running = false; Stops++; RunningChanged?.Invoke(false); }
        public string SelectedInterface { get; private set; } = "";
        public void SetInterface(string id) { SelectedInterface = id; Changed?.Invoke(id.Length == 0 ? Items : []); }
        public IReadOnlyList<Receiver> Items { get; set; } = [
            new("a", "家庭影院", "127.0.0.1") { Model = "HomePod" },
            new("b", "客厅立体声", "127.0.0.2") { StereoId = "pair", Members = [new("left", "左侧", "127.0.0.2"), new("right", "右侧", "127.0.0.3")] },
            new("c", "卧室 HomePod mini", "127.0.0.4") { Online = false, Model = "HomePod mini" }
        ];
        public void Publish() => Changed?.Invoke(Items);
        public void Dispose() => Stop();
    }
    internal sealed class MockFactory : IEngineFactory
    {
        public TaskCompletionSource? StopEntered, StopGate;
        public bool AutoReady = true;
        public Action<object>? EmitEvent;
        public ConcurrentQueue<JsonElement> Commands { get; } = new();
        public int CreatedCount, DisposedCount;
        public IEngineConnection Open()
        {
            UiPerformance.Count("engine.open"); Interlocked.Increment(ref CreatedCount);
            return new MockConnection(this);
        }
    }
    private sealed class MockConnection(MockFactory factory) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private CancellationTokenSource? _metrics;
        private string _host = "";
        private readonly Dictionary<string, int> _volumes = [];
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            factory.Commands.Enqueue(JsonSerializer.SerializeToElement(new { command, @params = parameters }));
            if (command == "hello") Emit(session, new { @event = "hello", capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "member_volume", "receiver_password", "standby_release", "receiver_ownership_guard", "application_capture", "forced_latency" } });
            if (command == "pair") factory.EmitEvent = payload => Emit(session, payload);
            if (command == "start")
            {
                _host = JsonSerializer.SerializeToElement(parameters).GetProperty("peers")[0].Text("host");
                var data = JsonSerializer.SerializeToElement(parameters);
                foreach (var peer in data.GetProperty("peers").EnumerateArray())
                    Emit(session, new { @event = "negotiated", receiver_id = peer.Text("receiver_id"), host = peer.Text("host"), port = peer.Integer("port"), codec = "alac", transport = "realtime", timing = "ptp", clock_role = peer.GetProperty("profile").Boolean("follow_receiver_clock") == true ? "follow" : "sender", sample_rate = data.Integer("sample_rate") });
                factory.EmitEvent = payload => Emit(session, payload);
                if (factory.AutoReady)
                    Emit(session, new { @event = "streaming" });
                var hosts = data.Text("group_mode") == "software_stereo" ? data.GetProperty("peers").EnumerateArray().Select(p => p.Text("host")).ToArray() : [_host];
                foreach (var host in hosts)
                {
                    _volumes[host] = host == _host ? 28 : 45;
                    Emit(session, new { @event = "device_volume", host, volume = _volumes[host], sequence = 0, status = "confirmed", available = true });
                }
                _metrics = new(); var token = _metrics.Token;
                _ = Task.Run(async () => { try { while (!token.IsCancellationRequested) { Emit(session, new { @event = "capture_metrics", metrics = new { capture_to_send_p95_ms = 10.8, max_queue_age_ms = 11.8, underrun_packets = 0, dropped_frames = 0, input_rate = 48000 } }); Emit(session, new { @event = "transport_metrics", session_uptime_ms = 123456, sender_late_recoveries = 2, skipped_packets = 10, members = new[] { new { host = "192.0.2.1", media = new { packets_sent = 12000, media_send_errors = 1, sync_send_errors = 0, retransmit_requests = 6, retransmits_sent = 5, retransmit_missing = 0, retransmit_expired = 1, retransmit_queue_drops = 0, retransmit_send_errors = 0, receiver_latency_ms = 150, receiver_latency_estimated = true }, health = new { feedback_rtt_ms = 4.2, feedback_failures = 0, feedback_delayed = false } } } }); await Task.Delay(350, token); } } catch (OperationCanceledException) { } }, CancellationToken.None);
            }
            if (command == "set_device_volume")
            {
                var data = JsonSerializer.SerializeToElement(parameters);
                var value = (int)data.Number("volume")!; var sequence = data.Integer("sequence");
                var host = data.Text("host"); if (host.Length == 0) host = _host;
                Emit(session, new { @event = "device_volume", host, volume = _volumes[host], sequence, status = "pending", available = true });
                _ = Task.Run(async () => {
                    await Task.Delay(350); _volumes[host] = value;
                    Emit(session, new { @event = "device_volume", host, volume = _volumes[host], sequence, status = "confirmed", available = true });
                });
            }
            if (command == "stop") _events.Writer.TryComplete();
            return Task.CompletedTask;
        }
        private void Emit(string session, object payload)
        {
            var json = JsonSerializer.SerializeToNode(payload)!.AsObject(); json["version"] = 1; json["session_id"] = session; _events.Writer.TryWrite(JsonSerializer.SerializeToElement(json));
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { return await _events.Reader.ReadAsync(cancellation); } catch (ChannelClosedException) { return null; }
        }
        public async ValueTask DisposeAsync() { factory.StopEntered?.TrySetResult(); if (factory.StopGate is { } gate) await gate.Task; if (_metrics is not null) { await _metrics.CancelAsync(); _metrics.Dispose(); } _events.Writer.TryComplete(); Interlocked.Increment(ref factory.DisposedCount); }
    }
}
