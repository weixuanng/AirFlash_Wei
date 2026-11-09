using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.Verification;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            AppPaths.Log($"Dispatcher failure: {eventArgs.Exception}");
            eventArgs.Handled = true;
            if (_viewModel is not null) _viewModel.ShowError(eventArgs.Exception);
            else
            {
                var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
                if (Program.IsDiagnostic(args))
                {
                    try { WriteOutput(args, new { ok = false, stage = "wpf-dispatcher", error = eventArgs.Exception.ToString() }); }
                    catch (Exception error) { AppPaths.Log($"Diagnostic output failed: {error.Message}"); }
                }
                else MessageBox.Show(eventArgs.Exception.Message + "\n\n" + AppPaths.LogDirectory,
                    "Airplay startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        };
    }
    private SingleInstance? _instance;
    private ThemeService? _theme;
    private AppViewModel? _viewModel;
    private ControlPanel? _panel;
    private TrayService? _tray;
    private EngineInitialization? _engine;
    private bool _quitting;
    private string _uiLanguage = "system";
    private string _uiTheme = "system";
    protected override async void OnStartup(StartupEventArgs args)
    {
        // Initialize before any windows, view models or static choice labels are created.
        var languageIndex = Array.IndexOf(args.Args, "--ui-language");
        var smokeLanguage = (args.Args.Contains("--ui-smoke") || args.Args.Contains("--tray-smoke")) && languageIndex >= 0 && languageIndex + 1 < args.Args.Length
            ? new[] { args.Args[languageIndex + 1] } : null;
        // Verification runs measure final layout; cosmetic entrance motion would
        // leave rows mid-animation (render offset, partial opacity) during checks.
        if (Program.NeedsDiagnosticIsolation(args.Args)) Motion.Enabled = false;
        try
        {
            L.Initialize(smokeLanguage);
            base.OnStartup(args);
            if (args.Args.Contains("--discovery-perf"))
            {
                var outputIndex = Array.IndexOf(args.Args, "--output");
                if (outputIndex < 0 || outputIndex + 1 >= args.Args.Length) throw new ArgumentException("--discovery-perf requires --output.");
                AppPaths.DataDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args.Args[outputIndex + 1]))!, "isolated-data");
                Shutdown(await DiscoveryPerf.RunAsync(args.Args)); return;
            }
            if (args.Args.Contains("--update-check"))
            {
                using var client = new System.Net.Http.HttpClient { MaxResponseContentBufferSize = 1024 * 1024 };
                var release = await new UpdateService(client).CheckAsync();
                WriteOutput(args.Args, new { ok = true, version = AppPaths.Version, display_version = AppPaths.DisplayVersion, latest = release?.Version.ToString(3), download = release?.DownloadPage.AbsoluteUri });
                Shutdown(0); return;
            }
            if (args.Args.Contains("--self-check")) { Shutdown(await SelfCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--stability-check")) { Shutdown(await StabilityCheck.RunAsync(args.Args)); return; }
            if (args.Args.Contains("--compat-check")) { Shutdown(await CompatibilityCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--discovery-check")) { Shutdown(await DiscoveryCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--audio-check")) { Shutdown(await AudioCheckAsync(args.Args)); return; }
            if (args.Args.Contains("--ui-smoke")) { Shutdown(await UiSmoke.RunAsync(args.Args)); return; }
            if (args.Args.Contains("--tray-smoke")) { Shutdown(await UiSmoke.RunAsync(args.Args)); return; }
            _instance = new();
            if (!_instance.IsOwner) { await _instance.NotifyExistingAsync(); await _instance.DisposeAsync(); _instance = null; Shutdown(); return; }
            _theme = new();
            var store = new SettingsStore(Path.Combine(AppPaths.DataDirectory, "config.json"));
            var initial = store.Load();
            _uiLanguage = initial.UiLanguage;
            _uiTheme = initial.Theme;
            _theme.SetPreference(_uiTheme);
            L.Initialize(_uiLanguage == "system" ? null : [_uiLanguage]);
            var audio = new AudioService();
            var factory = new ExtractingEngineFactory();
            _engine = new(token => QueryEngineAsync(factory, token), AppPaths.Log);
            _viewModel = new(store, new WindowsDiscovery(log: AppPaths.Log), new Autostart(), audio, factory, Dispatcher, _engine);
            _panel = new(_viewModel); MainWindow = _panel;
            _panel.QuitRequested += Quit;
            _tray = new(_panel, _viewModel, CheckUpdates, ShowAbout, Quit); _panel.Tray = _tray;
            _instance.Listen(() => Dispatcher.BeginInvoke(() => _panel.ShowPanel()));
            _viewModel.Start();
            // Auto-start with Windows (--startup) stays quietly in the tray; a
            // manual launch, including the first run, opens the panel.
            if (!args.Args.Contains("--startup")) _panel.ShowPanel();
            AppPaths.Log("WPF host started, JSONL v1. Startup stage=shell ready");
            await _engine.InitializeAsync();
        }
        catch (Exception error)
        {
            AppPaths.Log(error.ToString());
            if (Program.IsDiagnostic(args.Args)) { WriteOutput(args.Args, new { ok = false, error = error.ToString() }); Shutdown(1); return; }
            MessageBox.Show(error.Message + "\n\n" + AppPaths.LogDirectory, L.Get("Airplay startup failed"), MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
        }
    }
    private static readonly HttpClient UpdateClient = new() { MaxResponseContentBufferSize = 1024 * 1024 };
    private UpdateCheckState? _updates;
    // Updates are checked only from the tray menu; the result is a dialog,
    // never a panel section.
    private async void CheckUpdates()
    {
        if (_viewModel is null || _quitting) return;
        _updates ??= new(new UpdateService(UpdateClient), Version.Parse(AppPaths.Version));
        await _updates.CheckAsync();
        if (_quitting || _updates is null) return;
        if (_updates is { HasUpdate: true, DownloadPage: { } page })
        {
            var open = _panel is { } panel
                ? MessageBox.Show(panel, _updates.Status + "\n\n" + L.Get("Open the download page?"), L.Get("Update available"), MessageBoxButton.YesNo, MessageBoxImage.Information)
                : MessageBox.Show(_updates.Status + "\n\n" + L.Get("Open the download page?"), L.Get("Update available"), MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes) Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        if (_panel is { } owner) MessageBox.Show(owner, _updates.Status, L.Get("Airplay"), MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(_updates.Status, L.Get("Airplay"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void ShowAbout()
    {
        var text = $"Airplay {AppPaths.DisplayVersion} · Windows x64 · GPLv3 or later.";
        if (_panel is { } panel) MessageBox.Show(panel, text, L.Get("About Airplay"), MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(text, L.Get("About Airplay"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async void Quit()
    {
        if (_quitting) return; _quitting = true;
        try
        {
            if (_engine is not null) await _engine.DisposeAsync();
            if (_viewModel is not null) await _viewModel.DisposeAsync();
            _tray?.Dispose(); _panel?.ShutdownPanel();
            if (_instance is not null) await _instance.DisposeAsync();
            _theme?.Dispose();
        }
        catch (Exception error) { AppPaths.Log(error.ToString()); }
        finally { Shutdown(); }
    }
    internal static async Task<string> QueryEngineAsync(IEngineFactory factory, CancellationToken cancellation = default) => (await QueryHelloAsync(factory, cancellation)).Text("engine_version", L.Get("Unknown"));
    private static async Task<JsonElement> QueryHelloAsync(IEngineFactory factory, CancellationToken cancellation = default)
    {
        return await EngineHandshake.HelloAsync(factory, cancellation);
    }
    private sealed class ExtractingEngineFactory : IEngineFactory
    {
        public IEngineConnection Open()
        {
            AppPaths.Log("Startup stage=engine-extract started");
            var executable = AppPaths.ExtractEngine();
            AppPaths.Log("Startup stage=engine-extract ready");
            return new ProcessEngineFactory(executable, AppPaths.Log).Open();
        }
    }
    private static async Task<int> CompatibilityCheckAsync(string[] args)
    {
        var capabilities = WindowsCapabilities.Current;
        var stage = "engine-extract";
        try
        {
            var executable = AppPaths.ExtractEngine();
            stage = "engine-hello";
            var hello = await QueryHelloAsync(new ProcessEngineFactory(executable, AppPaths.Log));
            WriteOutput(args, new { ok = capabilities.Supported && capabilities.DnsSdAvailable, version = AppPaths.Version, display_version = AppPaths.DisplayVersion, capabilities,
                requirement = WindowsCompatibility.Requirement, native = hello,
                qualification = "Win10 execution pending; this check does not exercise discovery, pairing or playback." });
            return capabilities.Supported && capabilities.DnsSdAvailable ? 0 : 1;
        }
        catch (Exception error)
        {
            AppPaths.Log($"Compatibility stage={stage} failed {error}");
            WriteOutput(args, new { ok = false, version = AppPaths.Version, display_version = AppPaths.DisplayVersion, capabilities, stage, error = error.ToString(), hresult = $"0x{error.HResult:X8}" });
            return 1;
        }
    }
    private static async Task<int> SelfCheckAsync(string[] args)
    {
        var engine = AppPaths.ExtractEngine();
        var hello = await QueryHelloAsync(new ProcessEngineFactory(engine));
        WriteOutput(args, new { ok = true, host = "wpf", version = AppPaths.Version, display_version = AppPaths.DisplayVersion, engine, native = hello });
        return 0;
    }
    private static async Task<int> AudioCheckAsync(string[] args)
    {
        await using var audio = new AudioService();
        var endpoints = await audio.GetEndpointsAsync();
        var defaultId = await audio.GetDefaultEndpointIdAsync();
        WriteOutput(args, new { ok = true, endpoints = endpoints.Count, devices = endpoints.Select(e => new { e.Id, e.Name, kind = e.Kind.ToString() }), default_endpoint_found = endpoints.Any(e => e.Id == defaultId), note = "Read-only MTA audio enumeration; no playback or mute changes." });
        return 0;
    }
    private static async Task<int> DiscoveryCheckAsync(string[] args)
    {
        using var discovery = new WindowsDiscovery();
        IReadOnlyList<Receiver> receivers = []; var errors = new List<string>();
        discovery.Changed += value => receivers = value;
        discovery.Failed += error => { lock (errors) errors.Add(error); };
        discovery.Start(); await Task.Delay(TimeSpan.FromSeconds(8));
        WriteOutput(args, new { ok = errors.Count == 0, receivers, errors }); return errors.Count == 0 ? 0 : 1;
    }
    internal static void WriteOutput(string[] args, object value, JsonSerializerOptions? options = null)
    {
        var index = Array.FindIndex(args, a => a is "--self-check-output" or "--output");
        if (index >= 0 && index + 1 < args.Length)
        {
            var output = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(value, options ?? new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

