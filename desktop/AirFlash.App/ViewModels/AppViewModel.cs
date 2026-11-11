using System.Collections.ObjectModel;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.ViewModels;

public sealed class AppViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISettingsStore _store;
    private readonly IDiscoveryService _discovery;
    private readonly IAutostart _autostart;
    private readonly IAudioService _audio;
    private readonly Dispatcher _dispatcher;
    private readonly EngineInitialization? _engine;
    private readonly IRestartService _restart;
    public IReceiverCapabilityCatalog ReceiverCapabilities { get; }
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly Dictionary<string, Receiver> _known = [];
    public IReadOnlyDictionary<string, string> ReceiverMigrations { get; private set; } = new Dictionary<string, string>();
    private readonly HashSet<string> _autoAttempted = [];
    private readonly DispatcherTimer _volumeTimer;
    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _startupDiscoveryTimer;
    private readonly DispatcherTimer _discoverySearchTimer;
    private bool _started, _panelVisible, _startupDiscovery, _discoveryRequested, _isDiscovering, _searchSettled;
    public bool IsDiscovering => _isDiscovering;
    public string DiscoveryEmptyTitle => IsDiscovering && !_searchSettled ? L.Get("Searching for receivers") : L.Get("No receivers found");
    public string DiscoveryStatus => IsDiscovering ? L.Get("Searching for receivers") : L.Get("Discovery paused. Showing previous results.");
    private AppSettings _settings;
    private SessionSnapshot _snapshot = new(PlaybackState.Idle);
    private string _notice = "";
    private bool _volumeDirty, _autoSuppressed, _closing;
    private string? _defaultEndpoint;
    private long _editRevision;
    private Task _startup = Task.CompletedTask;
    private Task? _disposeTask;
    public EndpointCatalog Endpoints { get; }
    public IApplicationCatalog Applications { get; }
    public bool IsClosing => _closing;
    public SessionController Session { get; }
    public NowPlayingPump NowPlaying { get; }
    private readonly CancellationTokenSource _nowPlayingStop;
    private readonly Task _nowPlayingWork;
    public ObservableCollection<ReceiverViewModel> Receivers { get; } = [];
    public IReadOnlyList<Receiver> AllReceivers => _known.Values.Concat(SoftwareStereo.Resolve(_known.Values, _settings)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    public AppSettings Settings => _settings;
    public SessionSnapshot Snapshot => _snapshot;
    /// <summary>The speaker has reported its own volume in this session. One missed
    /// reading does not hide the control; a change is only sent while it is readable.</summary>
    public bool VolumeAvailable => _snapshot is { State: PlaybackState.Streaming or PlaybackState.Standby, Receiver.IsSoftwareGroup: false, DeviceVolume.Actual: not null };
    /// <summary>The speaker's own volume, 0 to 100: the level its touch surface changes.</summary>
    public double DeviceVolume
    {
        get => _snapshot.DeviceVolume?.Display ?? 0;
        set
        {
            var volume = (int)Math.Round(Math.Clamp(value, 0, 100));
            if (!VolumeAvailable || _snapshot.Receiver is not { } receiver || volume == _snapshot.DeviceVolume?.Display) return;
            _ = SetDeviceVolumeAsync(receiver.Id, volume);
        }
    }
    public string DeviceVolumeText => $"{(int)DeviceVolume}";
    /// <summary>A change would be accepted right now. The keyboard keys are only taken
    /// from Windows while this holds, so a key is never swallowed for nothing.</summary>
    public bool VolumeWritable => VolumeAvailable && _snapshot.DeviceVolume?.Available == true;
    private readonly PanelPreferences _panel = PanelPreferences.Load();
    /// <summary>The keyboard's volume keys change the speaker while it is streaming.</summary>
    public bool VolumeKeysEnabled
    {
        get => _panel.VolumeKeys;
        set { if (value == _panel.VolumeKeys) return; _panel.VolumeKeys = value; _panel.Save(); AppPaths.Log($"Setting volume_keys={value}"); Notify(); }
    }
    /// <summary>Windows blurs what is behind the panel.</summary>
    public bool PanelBlurEnabled
    {
        get => _panel.Blur;
        set { if (value == _panel.Blur) return; _panel.Blur = value; _panel.Save(); AppPaths.Log($"Setting panel_blur={value}"); Notify(); }
    }
    private readonly LinkQuality _link = new();
    /// <summary>How well audio is reaching the speaker in use.</summary>
    public LinkGrade LinkGrade => _link.Grade;
    /// <summary>Speakers the user has hidden from the list.</summary>
    public int HiddenCount => _settings.Receivers.Values.Count(options => options.Hidden);
    public bool HasHidden => HiddenCount > 0;
    public string ShowHiddenLabel => L.Format("Show hidden speakers ({0})", HiddenCount);
    public void HideReceiver(ReceiverViewModel row)
    {
        // The speaker in use stays: hiding it would leave a session with no row.
        if (_snapshot.IsActive && _snapshot.Receiver?.Id == row.Receiver.Id) return;
        _settings.Options(row.Receiver.Id).Hidden = true;
        AppPaths.Log($"Receiver hidden: {row.Receiver.Id}");
        HiddenChanged();
    }
    public void ShowHiddenReceivers()
    {
        foreach (var options in _settings.Receivers.Values) options.Hidden = false;
        HiddenChanged();
    }
    private void HiddenChanged()
    {
        VolumeChanged(); RefreshReceivers();
        Notify(nameof(HiddenCount)); Notify(nameof(HasHidden)); Notify(nameof(ShowHiddenLabel));
    }
    private void NotifyOptions()
    {
        Notify(nameof(DelayMode)); Notify(nameof(DirectCapture)); Notify(nameof(DirectCaptureAvailable));
        Notify(nameof(HiddenCount)); Notify(nameof(HasHidden)); Notify(nameof(ShowHiddenLabel));
    }
    /// <summary>
    /// How much audio the speaker holds, as an index into <see cref="DelayModes"/>:
    /// adaptive (starts at 50 ms and steps up only when the stream has trouble),
    /// fixed 120 ms, or 500 ms so lost packets have time to be sent again.
    /// </summary>
    private static readonly string[] DelayModes = [LatencyPolicy.Mode, "realtime", "buffered"];
    public int DelayMode
    {
        get => Math.Max(0, Array.IndexOf(DelayModes, _settings.LatencyMode));
        set
        {
            if (value < 0 || value >= DelayModes.Length || DelayModes[value] == _settings.LatencyMode) return;
            _settings.LatencyMode = DelayModes[value];
            // Without this the speaker's own minimum replaces the longer buffer.
            _settings.ForceLatency = DelayModes[value] == "buffered";
            AppPaths.Log($"Setting delay_mode={DelayModes[value]}");
            Notify(); VolumeChanged();
        }
    }
    /// <summary>Capture application audio directly, before the playback device's volume
    /// and mute, so the PC's own speakers can stay muted.</summary>
    public bool DirectCapture
    {
        get => _settings.CaptureMode == "system";
        set
        {
            if (value == DirectCapture || _settings.CaptureMode is not ("loopback" or "system")) return;
            _settings.CaptureMode = value ? "system" : "loopback";
            AppPaths.Log($"Setting direct_capture={value}");
            Notify(); VolumeChanged();
        }
    }
    public bool DirectCaptureAvailable => Applications.Supported && _settings.CaptureMode is "loopback" or "system";
    private async Task SetDeviceVolumeAsync(string receiverId, int volume)
    {
        try { await Session.SetDeviceVolumeAsync(receiverId, volume); }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppPaths.Log(error.ToString()); }
    }
    private Task? _stopTask;
    private long _playIntent;
    public bool IsStopping { get; private set; }
    public string Notice { get => _notice; private set { Set(ref _notice, value); Notify(nameof(ErrorDetails)); } }
    /// <summary>Colour of the status dot: live (green), busy (blue, breathing),
    /// wait (orange), error (red) or idle (grey).</summary>
    public string StatusTone => IsStopping ? "busy" : Session.NeedsCleanup ? "error" : _snapshot.State switch
    {
        PlaybackState.Connecting or PlaybackState.Pairing => "busy",
        PlaybackState.Streaming or PlaybackState.Standby => "live",
        PlaybackState.Preempted => "wait",
        PlaybackState.Error => "error",
        _ => "idle"
    };
    public bool StatusBusy => StatusTone == "busy";
    /// <summary>The status lines under the controls. Hidden while streaming, when the
    /// green dot on the speaker's row says all there is to say.</summary>
    public bool ShowStatus => StatusTone != "live";
    /// <summary>Drives the Dynamic Island style error pop-up at the top of the panel.</summary>
    public bool IslandActive => EngineFailed || StatusTone == "error";
    public string IslandTitle => EngineFailed ? L.Get("Could not start the audio engine.") : StatusTitle;
    public string IslandMessage => EngineFailed ? _engine?.Error ?? "" : ActionHint.Length > 0 ? ActionHint : StatusDetail;
    private void NotifyIsland() { Notify(nameof(IslandActive)); Notify(nameof(IslandTitle)); Notify(nameof(IslandMessage)); }
    public string StatusTitle => IsStopping ? L.Get("Disconnecting…") : Session.NeedsCleanup ? L.Get("Connection error") : _snapshot.State switch { PlaybackState.Connecting => L.Get("Connecting"), PlaybackState.Streaming => _snapshot.Receiver is { } connected ? L.Format("Connected to {0}", connected.Name) : L.Get("Playing"), PlaybackState.Standby => L.Get("Standby"), PlaybackState.Pairing => L.Get("Pairing"), PlaybackState.Error => L.Get("Connection error"), PlaybackState.Preempted => Session.IsYielding ? (Session.YieldedToOtherDevice ? L.Get("Playing on another device") : L.Get("Waiting for the speaker")) : L.Get("Taken over by another device"), _ => L.Get("Ready") };
    /// <summary>The session owns a receiver: playing, connecting, or quietly
    /// waiting for it after another device took it over.</summary>
    public bool SessionHeld => _snapshot.IsActive || Session.IsYielding;
    public string StatusDetail => IsStopping ? L.Get("Stopping playback and disconnecting…") : Session.NeedsCleanup ? L.Get("The audio engine could not stop. Try disconnecting again.") : _snapshot.Message.Length > 0 ? _snapshot.Message : _snapshot.State switch
    {
        PlaybackState.Streaming => L.Get("The session stays up through silence, and resumes on its own if another device takes the speaker."),
        PlaybackState.Standby => _snapshot.Receiver is { } standby ? L.Format("Connected to {0}.", standby.Name) : L.Get("Connected."),
        PlaybackState.Pairing => L.Get("Pairing…"),
        PlaybackState.Connecting => _snapshot.Receiver is { } connecting ? L.Format("Connecting to {0}…", connecting.Name) : L.Get("Connecting…"),
        _ => PrimaryTarget is { } target ? L.Format("Not connected. Press Connect to link to {0}.", target.Receiver.Name) : L.Get("Select a receiver to play system audio")
    };
    public string ActionHint => _snapshot.State == PlaybackState.Error ? _snapshot.ActionHint : "";
    public string ErrorDetails => _snapshot.State is PlaybackState.Error or PlaybackState.Preempted
        ? _snapshot.Diagnostics?.LastFault?.Detail is { Length: > 0 } detail ? detail : _snapshot.Message
        : Notice;
    /// <summary>The receiver now belongs to another controller. Reconnecting is a
    /// deliberate user action, never the automatic transport retry.</summary>
    public bool CanReconnect => !_closing && !IsStopping && !Session.NeedsCleanup && (Session.RequiresManualReconnect || Session.IsYielding) && _snapshot.Receiver is not null;
    public string EngineVersion { get; set; } = L.Get("Unknown");
    public bool EngineReady => _engine is null || _engine.State == EngineAvailability.Ready;
    public bool EngineFailed => _engine?.State == EngineAvailability.Failed;
    public string EngineMessage => EngineReady ? "" : EngineFailed
        ? L.Format("The audio engine could not start: {0}", _engine!.Error)
        : L.Get("Initializing audio engine…");
    public AsyncCommand RetryEngineCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand ReconnectCommand { get; }
    /// <summary>The panel's single main control: Stop while a session is held (or
    /// needs cleanup); otherwise Play on the last used speaker, falling back to
    /// the first playable one.</summary>
    public AsyncCommand PrimaryCommand { get; }
    public bool PrimaryIsStop => SessionHeld || Session.NeedsCleanup;
    public string PrimaryLabel => IsStopping ? L.Get("Disconnecting…") : PrimaryIsStop ? L.Get("Disconnect") : PrimaryTarget is { } target ? L.Format("Connect to {0}", target.Receiver.Name) : L.Get("Connect");
    private ReceiverViewModel? PrimaryTarget => Receivers.FirstOrDefault(r => r.Receiver.Id == _settings.LastReceiverId && r.CanPlay) ?? Receivers.FirstOrDefault(r => r.CanPlay);
    private void RefreshPrimary()
    {
        Notify(nameof(PrimaryIsStop)); Notify(nameof(PrimaryLabel));
        PrimaryCommand.Refresh();
    }
    public event Action? SettingsChanged;
    public event Action? CatalogChanged;
    public AppViewModel(ISettingsStore store, IDiscoveryService discovery, IAutostart autostart, IAudioService audio, IEngineFactory factory, Dispatcher dispatcher, EngineInitialization? engine = null, IApplicationCatalog? applications = null, IReceiverCapabilityCatalog? receiverCapabilities = null, IRestartService? restart = null)
    {
        _engine = engine;
        _restart = restart ?? new RestartService();
        ReceiverCapabilities = receiverCapabilities ?? new ReceiverCapabilityCatalog(factory);
        RetryEngineCommand = new(() => _engine?.InitializeAsync() ?? Task.CompletedTask, ShowError, () => EngineFailed && !_closing);
        if (_engine is not null) _engine.Changed += EngineChanged;
        _store = store; _discovery = discovery; _autostart = autostart; _audio = audio; _dispatcher = dispatcher;
        Applications = applications ?? new ApplicationCatalog(factory);
        Endpoints = new(audio, dispatcher);
        Endpoints.DevicesChanged += OnEndpointsChangedAsync;
        Endpoints.Failed += ShowError;
        _settings = store.Load();
        _discovery.SetInterface(_settings.DiscoveryInterfaceId);
        Session = new(factory, audio, AppPaths.Log, clockStrategies: new(Path.Combine(AppPaths.DataDirectory, "clock-strategy.json")));
        Session.Changed += SessionChanged;
        // A receiver's own buttons arrive as transport commands; forward them to the
        // media keys so the app that is playing reacts, without touching the stream.
        _nowPlayingStop = new();
        var nowPlayingSource = new AirFlash.App.Metadata.SmtcNowPlayingSource(AppPaths.Log);
        Session.RemoteCommandReceived += command =>
        {
            if (command.Name is "play" or "pause")
            {
                _ = nowPlayingSource.ExecuteAsync(command, _nowPlayingStop.Token);
                return;
            }
            if (command.VirtualKey is not { } key) return;
            _dispatcher.BeginInvoke(() => AirFlash.App.Input.MediaKeys.Press(key, AppPaths.Log));
        };
        // Now Playing is polled from SMTC and pushed only when it actually changed;
        // the engine turns it into the receiver's SET_PARAMETER payloads.
        NowPlaying = new NowPlayingPump(nowPlayingSource, log: AppPaths.Log);
        _nowPlayingWork = Task.Run(() => NowPlaying.RunAsync(now => _ = Session.PublishNowPlayingAsync(now, _nowPlayingStop.Token), _nowPlayingStop.Token));
        _discovery.Changed += list => _dispatcher.BeginInvoke(() => OnDiscovered(list));
        _discovery.Failed += message => _dispatcher.BeginInvoke(() => ShowError(new IOException(message)));
        _discovery.RunningChanged += running => _dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            _isDiscovering = running && _discoveryRequested; Notify(nameof(IsDiscovering)); Notify(nameof(DiscoveryEmptyTitle)); Notify(nameof(DiscoveryStatus));
        });
        _volumeTimer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, async (_, _) => { _volumeTimer!.Stop(); try { await FlushVolumeAsync(); } catch (Exception error) { ShowError(error); } }, dispatcher);
        _volumeTimer.Stop();
        _autoTimer = new(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, async (_, _) => { _autoTimer!.Stop(); try { await TryAutoConnectAsync(); } catch (Exception error) { ShowError(error); } }, dispatcher);
        _autoTimer.Stop();
        _startupDiscoveryTimer = new(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) =>
        {
            _startupDiscoveryTimer!.Stop(); _startupDiscovery = false; UpdateDiscoveryDemand();
        }, dispatcher);
        _startupDiscoveryTimer.Stop();
        _discoverySearchTimer = new(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) =>
        {
            _discoverySearchTimer!.Stop(); _searchSettled = true; Notify(nameof(DiscoveryEmptyTitle));
        }, dispatcher);
        _discoverySearchTimer.Stop();
        StopCommand = new(() => StopAsync(), ShowError, () => !IsStopping && (SessionHeld || Session.NeedsCleanup));
        ReconnectCommand = new(() => _snapshot.Receiver is { } receiver ? ToggleAsync(receiver, SessionStartIntent.Manual, reconnect: true) : Task.CompletedTask, ShowError, () => CanReconnect);
        PrimaryCommand = new(() => PrimaryIsStop ? StopAsync() : PrimaryTarget is { } target ? ToggleAsync(target.Receiver) : Task.CompletedTask, ShowError,
            () => PrimaryIsStop ? StopCommand.CanExecute(null) : !IsStopping && PrimaryTarget is not null);
        MergeManualReceivers();
    }
    public void Start()
    {
        _startup = StartAsync();
    }
    public Task Startup => _startup;
    private async Task StartAsync()
    {
        await _settingsGate.WaitAsync();
        try
        {
            if (_store is SettingsStore { LoadWarning: { } warning }) Notice = warning;
            try { _defaultEndpoint = await _audio.GetDefaultEndpointIdAsync(); }
            catch (Exception error) { ShowError(error); }
            if (_closing) return;
            // Startup is always on and the theme follows the system: no UI edits
            // these anymore, so a stale configuration value must never reach the
            // engine or autostart. The delay is one of the panel's three choices.
            var fixedDefaultsChanged = false;
            if (!DelayModes.Contains(_settings.LatencyMode)) { _settings.LatencyMode = "realtime"; fixedDefaultsChanged = true; }
            if (_settings.ForceLatency != (_settings.LatencyMode == "buffered")) { _settings.ForceLatency = _settings.LatencyMode == "buffered"; fixedDefaultsChanged = true; }
            NotifyOptions();
            if (!_settings.StartAtLogin) { _settings.StartAtLogin = true; fixedDefaultsChanged = true; }
            if (_settings.Theme != "system") { _settings.Theme = "system"; fixedDefaultsChanged = true; }
            foreach (var options in _settings.Receivers.Values)
                if (options.LatencyMode is not null || options.ForceLatency is not null) { options.LatencyMode = null; options.ForceLatency = null; fixedDefaultsChanged = true; }
            if (fixedDefaultsChanged) await SaveSettingsAsync(_settings);
            try { await _autostart.SetAsync(_settings.StartAtLogin); }
            catch (Exception error) { ShowError(error); }
            await MigrateEndpointNameAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _settingsGate.Release(); }
        if (_closing) return;
        _started = true;
        _startupDiscovery = _settings.AutoConnectOnDiscover || _settings.Receivers.Values.Any(o => o.AutoConnect == true && !o.Hidden)
            || _settings.AutoConnectLastReceiver && _settings.LastReceiverId is { Length: > 0 } last && _settings.ReadOptions(last).AutoConnect != false && !_settings.ReadOptions(last).Hidden;
        if (_startupDiscovery) _startupDiscoveryTimer.Start();
        UpdateDiscoveryDemand();
        RefreshReceivers(); ScheduleAutoConnect();
    }
    public void SetPanelVisible(bool visible) { _panelVisible = visible; UpdateDiscoveryDemand(); }
    private void UpdateDiscoveryDemand()
    {
        if (!_started || _closing) return;
        var requested = _panelVisible || _startupDiscovery || _snapshot.IsActive && _snapshot.Receiver?.IsSoftwareGroup == true;
        if (requested == _discoveryRequested) return;
        _discoveryRequested = requested;
        if (requested)
        {
            _searchSettled = false; _discoverySearchTimer.Stop(); _discoverySearchTimer.Start(); try { _discovery.Start(); } catch (Exception error) { ShowError(error); }
        }
        else { _discoverySearchTimer.Stop(); try { _discovery.Stop(); } catch (Exception error) { ShowError(error); } }
        Notify(nameof(DiscoveryEmptyTitle));
    }
    private void EngineChanged() => _dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        if (EngineReady) EngineVersion = _engine!.Version;
        Notify(nameof(EngineReady)); Notify(nameof(EngineFailed)); Notify(nameof(EngineMessage)); NotifyIsland();
        RetryEngineCommand.Refresh();
        foreach (var row in Receivers) row.Refresh();
        RefreshPrimary();
        CatalogChanged?.Invoke();
        if (EngineReady) ScheduleAutoConnect();
    });
    private async Task MigrateEndpointNameAsync()
    {
        if (_settings.CaptureMode != "endpoint" || string.IsNullOrWhiteSpace(_settings.CaptureEndpoint)) return;
        try
        {
            var selected = _settings.CaptureEndpoint;
            var endpoints = await Endpoints.GetAsync();
            if (_settings.CaptureEndpoint != selected || _closing) return;
            if (endpoints.Any(e => e.Id == _settings.CaptureEndpoint)) return;
            var matches = endpoints.Where(e => e.Name == _settings.CaptureEndpoint).ToArray();
            if (matches.Length == 1) _settings.CaptureEndpoint = matches[0].Id;
            else Notice = L.Get("The previous capture endpoint is unavailable.");
        }
        catch (Exception error) { ShowError(error); }
    }
    private async Task OnEndpointsChangedAsync()
    {
        if (_closing) return;
        var intent = _playIntent;
        await _settingsGate.WaitAsync();
        try
        {
            if (_closing) return;
            var next = await _audio.GetDefaultEndpointIdAsync();
            if (_closing) return;
            if (!IsStopping && intent == _playIntent && _settings.CaptureMode is "loopback" or "system" && next != _defaultEndpoint && _snapshot.IsActive && _snapshot.State != PlaybackState.Pairing && _snapshot.Receiver is { } receiver) await Session.StartAsync(receiver, _settings.Clone());
            _defaultEndpoint = next;
            if (!_snapshot.IsActive) await _audio.RestoreAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _settingsGate.Release(); }
    }
    private async void OnDiscovered(IReadOnlyList<Receiver> list)
    {
        if (_closing) return;
        try
        {
            await _settingsGate.WaitAsync();
            try
            {
                if (_closing) return;
                await ReconcileDiscoveryAsync(list);
            }
            finally { _settingsGate.Release(); if (_volumeDirty && !_closing) { _volumeTimer.Stop(); _volumeTimer.Start(); } }
        }
        catch (Exception error) { ShowError(error); }
    }
    private async Task ReconcileDiscoveryAsync(IReadOnlyList<Receiver> list)
    {
        await MigrateMixedGroupsAsync(list);
        var currentSnapshot = Session.Snapshot;
        var discoverySession = currentSnapshot;
        var originalSettings = _settings.Clone();
        var reconciliation = ReceiverCatalog.Reconcile(list, _settings, currentSnapshot.Receiver?.Id);
        if (!SettingsMerge.Equal(_settings, reconciliation.Settings))
        {
            var revision = _editRevision;
            await SaveSettingsAsync(reconciliation.Settings, reconciliation.NeedsBackup);
            var live = _settings.Clone(); var baseline = originalSettings.Clone();
            ReceiverCatalog.ApplyMigrations(live, reconciliation.Migrations, currentSnapshot.Receiver?.Id);
            ReceiverCatalog.ApplyMigrations(baseline, reconciliation.Migrations, currentSnapshot.Receiver?.Id);
            _settings = SettingsMerge.Merge(baseline, live, reconciliation.Settings);
            _volumeDirty = revision != _editRevision;
            Notify(nameof(Settings)); NotifyOptions(); SettingsChanged?.Invoke();
            var migrated = reconciliation.Migrations.Where(p => p.Key != p.Value && (originalSettings.Receivers.ContainsKey(p.Key) || currentSnapshot.Receiver?.Id == p.Key)).ToArray();
            if (migrated.Length > 0) AppPaths.Log("Receiver identity migrated: " + System.Text.Json.JsonSerializer.Serialize(migrated));
        }
        ReceiverMigrations = reconciliation.Migrations;
        list = reconciliation.Receivers;
        var before = AllReceivers;
        var discovered = list.ToDictionary(r => r.Id);
        foreach (var previous in before.Where(r => !r.IsManual))
            if (ReceiverMigrations.TryGetValue(previous.Id, out var target) && previous.Id != target && discovered.TryGetValue(target, out var promoted))
                AppPaths.Log("Receiver identity upgraded: " + System.Text.Json.JsonSerializer.Serialize(new { OldId = previous.Id, NewId = target, promoted.Address, promoted.Port, promoted.DeviceId, promoted.Model, promoted.StereoId,
                    Reason = ReceiverIdentity.IsBroadcast(previous.Id) ? "confirmed_broadcast_alias" : "unique_endpoint" }));
        foreach (var key in _known.Keys.ToArray())
            if (!_known[key].IsManual && (ReceiverMigrations.TryGetValue(key, out var target) && target != key || ReceiverCatalog.IsCoveredMember(key, list))) _known.Remove(key);
        foreach (var id in _autoAttempted.ToArray())
            if (ReceiverMigrations.TryGetValue(id, out var target) && id != target) { _autoAttempted.Remove(id); _autoAttempted.Add(target); }
        foreach (var key in _known.Keys.ToArray())
            if (!_known[key].IsManual && !discovered.ContainsKey(key))
            {
                if (_settings.DiscoveryInterfaceId.Length > 0) _known.Remove(key);
                else _known[key] = _known[key] with { Online = false };
            }
        foreach (var receiver in list)
        {
            var previous = before.Where(r => !r.IsManual && ReceiverMigrations.GetValueOrDefault(r.Id, r.Id) == receiver.Id)
                .OrderByDescending(r => r.Online).ThenByDescending(r => r.Complete).FirstOrDefault();
            if (previous is null || !previous.Online || !previous.Complete && receiver.Complete) _autoAttempted.Remove(receiver.Id);
            _known[receiver.Id] = receiver;
        }
        MergeManualReceivers();
        if (!CatalogEqual(before, AllReceivers)) { RefreshReceivers(); CatalogChanged?.Invoke(); }
        currentSnapshot = Session.Snapshot;
        if (currentSnapshot.Receiver is { IsManual: false } current)
        {
            var id = ReceiverMigrations.GetValueOrDefault(current.Id, current.Id);
            if (current.IsSoftwareGroup)
            {
                var resolved = SoftwareStereo.Resolve(_known.Values, _settings).FirstOrDefault(r => r.Id == id);
                if (resolved?.Members.Any(m => m.IsGroup || m.StereoId.Length > 0) == true)
                {
                    if (await Session.StopForDiscoveryConflictAsync(discoverySession))
                        ShowError(new IOException(L.Get("Stereo groups require two distinct independent receivers.")));
                    return;
                }
                if (resolved is { Online: true, Complete: true }) discovered[id] = resolved;
            }
            // Discovery snapshots can be incomplete while scanning or suspended.
            // The transport health checks own disconnect detection during playback.
            if (currentSnapshot.State != PlaybackState.Pairing && discovered.TryGetValue(id, out var next) && next.Complete)
                await Session.RefreshReceiverAsync(next, discoverySession, ReceiverMigrations);
        }
        ScheduleAutoConnect();
    }
    /// <summary>
    /// Acknowledge the current group invariant while preserving saved pairs.
    /// Incompatible pairs stay visible and can only be split by the user.
    /// </summary>
    private async Task MigrateMixedGroupsAsync(IReadOnlyList<Receiver> catalog)
    {
        if (_store is not SettingsStore store || !store.MixedGroupMigrationPending) return;
        var report = MixedGroupMigration.Apply(_settings, catalog, AppPaths.Log);
        if (MixedGroupMigration.IsSettled(_settings))
        {
            await store.CompleteMixedGroupMigrationAsync(_settings);
            Notify(nameof(Settings)); NotifyOptions(); SettingsChanged?.Invoke();
        }
        else if (report.Changed)
        {
            await SaveSettingsAsync(_settings);
            Notify(nameof(Settings)); NotifyOptions(); SettingsChanged?.Invoke();
        }
    }
    private void MergeManualReceivers()
    {
        var ids = _settings.ManualReceivers.Select(r => r.Id).ToHashSet();
        foreach (var key in _known.Where(p => p.Value.IsManual && !ids.Contains(p.Key)).Select(p => p.Key).ToArray()) _known.Remove(key);
        foreach (var manual in _settings.ManualReceivers)
            _known[manual.Id] = new(manual.Id, manual.Name, manual.Host, manual.Port) { IsManual = true };
    }
    private static bool CatalogEqual(IReadOnlyList<Receiver> left, IReadOnlyList<Receiver> right)
        => left.Count == right.Count && left.Zip(right).All(p => ReceiverEqual(p.First, p.Second));
    internal static bool ReceiverEqual(Receiver left, Receiver right)
        => (left with { Members = Array.Empty<Receiver>(), Codecs = Array.Empty<byte>(), Aliases = Array.Empty<string>() }) == (right with { Members = Array.Empty<Receiver>(), Codecs = Array.Empty<byte>(), Aliases = Array.Empty<string>() })
            && left.Codecs.SequenceEqual(right.Codecs) && left.Aliases.SequenceEqual(right.Aliases) && CatalogEqual(left.Members, right.Members);
    private void RefreshReceivers()
    {
        var active = Session.Snapshot;
        // A receiver the session is waiting on (yielding) stays listed like an active one.
        var held = active.IsActive || Session.IsYielding;
        IEnumerable<Receiver> catalog = AllReceivers;
        if (held && active.Receiver is { } current && catalog.All(r => r.Id != current.Id))
            catalog = catalog.Append(current with { Online = false });
        var visible = catalog.Where(r => (r.Online || held && r.Id == active.Receiver?.Id) && !_settings.ReadOptions(r.Id).Hidden).ToList();
        foreach (var row in Receivers.Where(row => visible.All(r => r.Id != row.Receiver.Id)).ToArray()) Receivers.Remove(row);
        for (var i = 0; i < visible.Count; i++)
        {
            var row = Receivers.FirstOrDefault(r => r.Receiver.Id == visible[i].Id);
            if (row is null) { row = new(this, visible[i]); Receivers.Insert(i, row); }
            else { row.Receiver = visible[i]; var previous = Receivers.IndexOf(row); if (previous != i) Receivers.Move(previous, i); }
            row.Refresh();
        }
        Notify(nameof(AllReceivers));
        RefreshPrimary();
    }
    private void SessionChanged(SessionSnapshot snapshot) => _dispatcher.BeginInvoke(() => ApplySessionSnapshot(snapshot));
    private void ApplySessionSnapshot(SessionSnapshot snapshot)
    {
        if (_closing || !ReferenceEquals(snapshot, Session.Snapshot)) return;
        var title = StatusTitle; var tone = StatusTone; var detail = StatusDetail; var errors = ErrorDetails; var hint = ActionHint;
        var previous = _snapshot;
        var volumeAvailable = VolumeAvailable; var volume = DeviceVolume; var volumeWritable = VolumeWritable;
        _snapshot = snapshot;
        var grade = _link.Grade;
        if (snapshot.State != PlaybackState.Streaming) _link.Reset();
        else _link.Observe(snapshot.Diagnostics?.Transport);
        if (grade != _link.Grade) AppPaths.Log($"Link quality={_link.Grade}; resent_percent={_link.LossPercent:0.0}");
        if (volumeAvailable != VolumeAvailable)
        {
            Notify(nameof(VolumeAvailable));
            AppPaths.Log($"Speaker volume control available={VolumeAvailable}; status={snapshot.DeviceVolume?.Status}; volume={snapshot.DeviceVolume?.Actual}");
        }
        if (volume != DeviceVolume) { Notify(nameof(DeviceVolume)); Notify(nameof(DeviceVolumeText)); }
        if (volumeWritable != VolumeWritable) Notify(nameof(VolumeWritable));
        if (Session.RequiresManualReconnect) _autoSuppressed = true;
        Notify(nameof(Snapshot));
        if (title != StatusTitle) Notify(nameof(StatusTitle));
        if (tone != StatusTone) { Notify(nameof(StatusTone)); Notify(nameof(StatusBusy)); Notify(nameof(ShowStatus)); }
        NotifyIsland();
        if (detail != StatusDetail) Notify(nameof(StatusDetail));
        if (errors != ErrorDetails) Notify(nameof(ErrorDetails));
        if (hint != ActionHint) Notify(nameof(ActionHint));
        if (previous.State != snapshot.State || previous.Receiver?.Id != snapshot.Receiver?.Id)
        {
            RefreshReceivers();
            StopCommand.Refresh();
            Notify(nameof(CanReconnect));
            ReconnectCommand.Refresh();
            UpdateDiscoveryDemand();
        }
        foreach (var row in Receivers) row.Refresh();
        RefreshPrimary();
    }
    public Task ToggleAsync(Receiver receiver) => ToggleAsync(receiver, SessionStartIntent.Manual);
    private async Task ToggleAsync(Receiver receiver, SessionStartIntent startIntent, bool reconnect = false)
    {
        if (_closing || IsStopping) return;
        // Reconnect claims the receiver back; a plain toggle on the held receiver stops.
        if (!reconnect && SessionHeld && _snapshot.Receiver?.Id == receiver.Id) { await StopAsync(); return; }
        if (_closing || !EngineReady || Session.NeedsCleanup) return;
        var group = AllReceivers.FirstOrDefault(r => r.IsGroup && !r.IsSoftwareGroup && r.Members.Any(m => m.Address == receiver.Address));
        if (group is not null) receiver = group;
        var intent = ++_playIntent;
        await FlushVolumeAsync();
        await _settingsGate.WaitAsync();
        try
        {
            if (_closing || IsStopping || intent != _playIntent || !EngineReady || Session.NeedsCleanup) return;
            if (_settings.CaptureMode is "loopback" or "system") _defaultEndpoint = await _audio.GetDefaultEndpointIdAsync();
            if (_closing || IsStopping || intent != _playIntent) return;
            if (startIntent == SessionStartIntent.Manual) _autoSuppressed = false;
            Notice = "";
            _autoAttempted.Add(receiver.Id);
            await Session.StartAsync(receiver, _settings.Clone(), startIntent);
            if (intent != _playIntent) return;
            _settings.LastReceiverId = receiver.Id;
            VolumeChanged();
        }
        finally { _settingsGate.Release(); }
        await FlushVolumeAsync();
    }
    // ---- Sleep. The speaker is let go before the PC sleeps and taken back on wake,
    // so nothing else in the house finds it held by a sleeping laptop.
    private string? _wakeTarget;
    private bool _asleep, _wakeBusy;
    private int _wakeTries;
    private DispatcherTimer? _wakeTimer;
    private const int WakeTries = 30;
    /// <summary>Called on the UI thread just before the PC sleeps.</summary>
    public Task SuspendAsync()
    {
        if (_closing) return Task.CompletedTask;
        _wakeTimer?.Stop();
        // Remember the speaker only when one is really in use: not while a stop the
        // user asked for is still finishing. A speaker still waiting to be taken
        // back from an earlier sleep stays remembered.
        var held = SessionHeld && !IsStopping;
        if (!_asleep && held && _snapshot.Receiver?.Id is { } id) _wakeTarget = id;
        _asleep = true;
        AppPaths.Log($"Sleep: releasing speaker={held}; reconnect on wake={_wakeTarget is not null}");
        return held ? StopAsync(userInitiated: false) : Task.CompletedTask;
    }
    /// <summary>Called on the UI thread once the PC is awake again.</summary>
    public void Resume()
    {
        if (!_asleep) return;
        _asleep = false;
        AppPaths.Log($"Wake: reconnecting={_wakeTarget is not null}");
        if (_closing) return;
        if (_wakeTarget is null) { ScheduleAutoConnect(); return; }
        _wakeTries = 0;
        // Wi-Fi and discovery need a moment after wake, so the speaker is retried for a while.
        _wakeTimer ??= new(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => { try { await WakeTickAsync(); } catch (Exception error) { ShowError(error); } }, _dispatcher);
        _wakeTimer.Start();
    }
    private async Task WakeTickAsync()
    {
        if (_closing || _asleep || _wakeTarget is not { } target) { _wakeTimer?.Stop(); return; }
        // Done once the speaker is held again (by this retry or by the user), or
        // when the tries run out; ordinary auto-connect takes over from there.
        if (SessionHeld || ++_wakeTries > WakeTries)
        {
            AppPaths.Log($"Wake: reconnect finished held={SessionHeld}; tries={_wakeTries}");
            _wakeTimer?.Stop(); _wakeTarget = null;
            if (!SessionHeld) ScheduleAutoConnect();
            return;
        }
        if (IsStopping || Session.NeedsCleanup || !EngineReady) return;
        if (Receivers.FirstOrDefault(r => r.Receiver.Id == target && r.CanPlay) is not { } row) return;
        // The first tries can fail while Wi-Fi is still coming back; the target is
        // kept so the next tick tries again.
        if (_wakeBusy) return; // a slow start must not be restarted by the next tick
        _wakeBusy = true;
        try { await ToggleAsync(row.Receiver, SessionStartIntent.Automatic); }
        catch (Exception error) { AppPaths.Log($"Wake: reconnect try {_wakeTries} failed: {error.Message}"); }
        finally { _wakeBusy = false; }
    }
    public Task StopAsync(bool userInitiated = true)
    {
        if (userInitiated) { _autoSuppressed = true; _wakeTarget = null; }
        if (_stopTask is not null) return _stopTask;
        ++_playIntent;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopTask = completion.Task;
        IsStopping = true; RefreshStopUi();
        _ = StopCoreAsync(completion);
        return completion.Task;
    }
    private async Task StopCoreAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await Session.StopAsync(); }
        catch (Exception error) { failure = error; }
        finally
        {
            ApplySessionSnapshot(Session.Snapshot);
            IsStopping = false; _stopTask = null; RefreshStopUi();
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
    private void RefreshStopUi()
    {
        Notify(nameof(IsStopping)); Notify(nameof(StatusTitle)); Notify(nameof(StatusTone)); Notify(nameof(StatusBusy)); Notify(nameof(ShowStatus)); NotifyIsland(); Notify(nameof(StatusDetail)); Notify(nameof(CanReconnect));
        StopCommand.Refresh(); ReconnectCommand.Refresh();
        foreach (var row in Receivers) row.Refresh();
        RefreshPrimary();
    }
    private void ScheduleAutoConnect() { _autoTimer.Stop(); if (!_closing) _autoTimer.Start(); }
    private async Task SaveSettingsAsync(AppSettings settings, bool? identityBackup = null)
    {
        var warning = (_store as SettingsStore)?.LoadWarning;
        if (identityBackup is { } backup) await _store.SaveReceiverIdentityAsync(settings, backup);
        else await _store.SaveAsync(settings);
        if (warning is not null && Notice == warning && _store is SettingsStore store && store.LoadWarning != warning)
            Notice = store.LoadWarning ?? "";
    }
    internal async Task TryAutoConnectAsync()
    {
        // While yielding, the session itself resumes once the receiver is free.
        if (_closing || _asleep || _wakeTarget is not null || IsStopping || Session.NeedsCleanup || Session.RequiresManualReconnect || Session.IsYielding || !EngineReady || _autoSuppressed || Session.Snapshot.IsActive) return;
        // An explicit per-receiver choice wins; otherwise the last-used receiver
        // (AutoConnectLastReceiver) or every receiver (AutoConnectOnDiscover).
        var eligible = AllReceivers.Where(r => r.Online && r.Complete && !_settings.ReadOptions(r.Id).Hidden
            && (_settings.ReadOptions(r.Id).AutoConnect ?? (_settings.AutoConnectOnDiscover || _settings.AutoConnectLastReceiver && r.Id == _settings.LastReceiverId)));
        var receiver = eligible.OrderByDescending(r => r.Id == _settings.LastReceiverId).FirstOrDefault();
        if (receiver is not null && !_autoAttempted.Contains(receiver.Id)) await ToggleAsync(receiver, SessionStartIntent.Automatic);
    }
    private void VolumeChanged() { _editRevision++; _volumeDirty = true; _volumeTimer.Stop(); if (!_closing) _volumeTimer.Start(); }
    public async Task FlushVolumeAsync()
    {
        _volumeTimer.Stop();
        if (!_volumeDirty) return;
        await _settingsGate.WaitAsync();
        var saved = false;
        try
        {
            if (!_volumeDirty) return;
            var current = _settings.Clone();
            var revision = _editRevision;
            using (UiPerformance.Measure("settings.persist")) await SaveSettingsAsync(current);
            saved = true;
            if (revision == _editRevision) _volumeDirty = false;
            await Session.UpdateSettingsAsync(current);
        }
        finally { _settingsGate.Release(); if (saved && _volumeDirty && !_closing) { _volumeTimer.Stop(); _volumeTimer.Start(); } }
    }
    public async Task<SettingsApplyResult> ApplyAsync(AppSettings baseline, AppSettings draft, Action<string>? progress = null)
    {
        if (_closing) throw new InvalidOperationException(L.Get("The application is closing."));
        baseline = baseline.Clone(); draft = draft.Clone();
        _volumeTimer.Stop();
        using (UiPerformance.Measure("apply.queue")) await _settingsGate.WaitAsync();
        try
        {
            if (_closing) throw new InvalidOperationException(L.Get("The application is closing."));
            _volumeTimer.Stop();
            ReceiverCatalog.ApplyMigrations(baseline, ReceiverMigrations, Session.Snapshot.Receiver?.Id);
            ReceiverCatalog.ApplyMigrations(draft, ReceiverMigrations, Session.Snapshot.Receiver?.Id);
            var before = _settings.Clone();
            var revision = _editRevision;
            AppSettings merged;
            using (UiPerformance.Measure("apply.merge")) merged = SettingsMerge.Merge(baseline, draft, before);
            if (merged.Validate() is { } validation) throw new InvalidOperationException(validation);
            progress?.Invoke(L.Get("Saving settings…"));
            if (merged.CaptureMode == "endpoint")
            {
                using (UiPerformance.Measure("apply.endpoints"))
                    if (!(await Endpoints.RefreshAsync()).Any(e => e.Id == merged.CaptureEndpoint))
                        throw new InvalidOperationException(L.Get("The selected capture endpoint is unavailable. Select another."));
            }
            if (merged.CaptureMode is "application" or "system" && !Applications.Supported) throw new InvalidOperationException(L.Get("Application capture requires Windows build 20348 or later. Use a virtual audio endpoint on this Windows version."));
            var changedAutostart = merged.StartAtLogin != before.StartAtLogin;
            using (UiPerformance.Measure("apply.persist"))
            {
                if (changedAutostart) await _autostart.SetAsync(merged.StartAtLogin);
                try { await SaveSettingsAsync(merged); }
                catch (Exception saveError)
                {
                    if (changedAutostart)
                        try { await _autostart.SetAsync(before.StartAtLogin); }
                        catch (Exception rollbackError) { throw new AggregateException(L.Get("Saving failed and startup settings could not be restored."), saveError, rollbackError); }
                    throw;
                }
            }
            using (UiPerformance.Measure("apply.ui"))
            {
                if (!before.AutoConnectOnDiscover && merged.AutoConnectOnDiscover) { _autoSuppressed = false; _autoAttempted.Clear(); }
                // Preserve edits made in the panel while this snapshot was being saved.
                _settings = revision == _editRevision ? merged.Clone() : SettingsMerge.Merge(before, _settings, merged);
                foreach (var removed in baseline.Receivers.Keys.Except(draft.Receivers.Keys)) _settings.Receivers.Remove(removed);
                _volumeDirty = revision != _editRevision;
                if (before.DiscoveryInterfaceId != _settings.DiscoveryInterfaceId) _discovery.SetInterface(_settings.DiscoveryInterfaceId);
                var oldCatalog = AllReceivers;
                MergeManualReceivers(); RefreshReceivers();
                Notify(nameof(Settings)); NotifyOptions();
                SettingsChanged?.Invoke();
                if (!CatalogEqual(oldCatalog, AllReceivers)) CatalogChanged?.Invoke();
            }
            progress?.Invoke(L.Get("Updating audio…"));
            string? audioError = null;
            try
            {
                using (UiPerformance.Measure("apply.session"))
                {
                    if (Session.Snapshot.Receiver is { IsSoftwareGroup: true } active)
                    {
                        var previousGroup = before.SoftwareGroups.FirstOrDefault(g => g.Id == active.Id);
                        var nextGroup = merged.SoftwareGroups.FirstOrDefault(g => g.Id == active.Id);
                        // Only an explicit membership edit adopts the current catalog here.
                        // Hot settings must retain working addresses through discovery loss.
                        if (previousGroup is not null && nextGroup is not null && (previousGroup.LeftId != nextGroup.LeftId || previousGroup.RightId != nextGroup.RightId))
                        {
                            var next = AllReceivers.FirstOrDefault(r => r.Id == active.Id);
                            if (next is { Online: true, Complete: true }) await Session.UpdateReceiverAsync(next, merged);
                            else await Session.StopAsync();
                        }
                    }
                    await Session.UpdateSettingsAsync(merged);
                }
            }
            catch (Exception error) { audioError = L.Format("Settings saved, but audio could not be updated: {0}", error.Message); ShowError(new IOException(audioError, error)); }
            ScheduleAutoConnect();
            return new(merged, audioError);
        }
        finally { _settingsGate.Release(); if (_volumeDirty && !_closing) { _volumeTimer.Stop(); _volumeTimer.Start(); } }
    }
    public void ShowError(Exception error) { AppPaths.Log(error.ToString()); Notice = error.Message; }
    public string Diagnostics() => $"Airplay {AppPaths.DisplayVersion} / WPF\n" + L.Format("Engine: {0}\nStatus: {1}\n{2}\nEnd-to-end latency: not measured", EngineVersion, StatusTitle, StatusDetail) + (ActionHint.Length > 0 ? "\n" + ActionHint : "") + "\n" + System.Text.Json.JsonSerializer.Serialize(_snapshot.Diagnostics, AppSettings.JsonOptions);
    /// <summary>Write the current engine's redacted diagnostic bundle into the log folder.</summary>
    public async Task<string> ExportDiagnosticsAsync()
    {
        var snapshot = await DiagnosticExport.SnapshotAsync(Session).ConfigureAwait(false);
        var bundle = DiagnosticExport.Compose(AppPaths.DisplayVersion, EngineVersion, snapshot);
        var path = DiagnosticExport.Write(AppPaths.LogDirectory, bundle);
        AppPaths.Log($"Diagnostics exported to {path}");
        return path;
    }
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _closing = true; _wakeTimer?.Stop(); _volumeTimer.Stop(); _autoTimer.Stop(); _startupDiscoveryTimer.Stop(); _discoverySearchTimer.Stop(); _discovery.Dispose();
        _nowPlayingStop.Cancel();
        await _nowPlayingWork;
        _nowPlayingStop.Dispose();
        if (_engine is not null) _engine.Changed -= EngineChanged;
        await _startup;
        Endpoints.Dispose();
        try { await Endpoints.DrainAsync(); } catch (Exception error) { AppPaths.Log(error.ToString()); }
        await _settingsGate.WaitAsync(); _settingsGate.Release();
        try { await FlushVolumeAsync(); } catch (Exception error) { AppPaths.Log(error.ToString()); }
        await Session.DisposeAsync();
        if (_audio is IAsyncDisposable disposable) await disposable.DisposeAsync();
    }
}
public sealed record SettingsApplyResult(AppSettings Saved, string? AudioError)
{
    public bool AudioUpdated => AudioError is null;
}
public sealed class ReceiverViewModel : ObservableObject
{
    private readonly Dictionary<string, object?> _display = [];
    private readonly AppViewModel _app;
    public Receiver Receiver { get; set; }
    public ReceiverViewModel(AppViewModel app, Receiver receiver)
    {
        _app = app; Receiver = receiver;
    }
    public string Name => Receiver.Name;
    public string Detail => Receiver.Detail;
    public bool Active => _app.SessionHeld && _app.Snapshot.Receiver?.Id == Receiver.Id;
    public bool IsStopping => _app.IsStopping && _app.Snapshot.Receiver?.Id == Receiver.Id;
    public bool NeedsDisconnect => Active || _app.Session.NeedsCleanup && _app.Snapshot.Receiver?.Id == Receiver.Id;
    public bool CanPlay => !_app.IsStopping && !_app.Session.NeedsCleanup && (Active || _app.EngineReady && Receiver.Online && Receiver.Complete);
    public bool IsStreaming => Active && _app.Snapshot.State == PlaybackState.Streaming;
    public string StateText => (_app.Snapshot.State == PlaybackState.Streaming && Active ? L.Get("Playing")
        : NeedsDisconnect ? _app.StatusTitle
        : !Receiver.Online ? L.Get("Offline") : !Receiver.Complete ? L.Get("Waiting for the other member") : L.Get("Disconnected")) + (Receiver.IsGroup ? $" · {Receiver.Members.Length}/2" : "");
    /// <summary>Bars to light on the row's signal mark: 0 hides it, 3 is a clean link.</summary>
    public int Quality => IsStreaming ? (int)_app.LinkGrade : 0;
    public string QualityText => _app.LinkGrade switch
    {
        LinkGrade.Good => L.Get("Connection is good"),
        LinkGrade.Fair => L.Get("Some audio is being sent again. Wi-Fi is dropping packets."),
        LinkGrade.Poor => L.Get("Audio is being lost on the way. Move the PC or the speaker closer to the router, or set Delay to Most stable."),
        _ => "",
    };
    public void Refresh()
    {
        var values = new Dictionary<string, object?> { [nameof(Quality)] = Quality, [nameof(QualityText)] = QualityText, [nameof(IsStopping)] = IsStopping, [nameof(NeedsDisconnect)] = NeedsDisconnect, [nameof(Name)] = Name, [nameof(Detail)] = Detail, [nameof(Active)] = Active, [nameof(IsStreaming)] = IsStreaming, [nameof(CanPlay)] = CanPlay, [nameof(StateText)] = StateText };
        foreach (var (name, value) in values)
            if (!_display.TryGetValue(name, out var old) || !Equals(old, value))
            {
                _display[name] = value; Notify(name);
            }
    }
}

