# <img src="desktop/AirFlash.App/Assets/app.svg" width="36" height="36" alt="" style="vertical-align:middle" /> Airplay

<p align="center">
  <a href="README.md">English</a> ·
  <a href="docs/README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="docs/images/airflash-en.png" alt="Airplay English interface" width="550" />
</p>

Airplay supports existing two-device stereo pairs and low-latency playback from Windows.
Stream Windows desktop audio to HomePod through a native AirPlay 2 sender,
with support for HomePod OS 27.

Airplay is an AirPlay 2 alternative to TuneBlade (AirPlay 1), designed to bring
Windows audio streaming to HomePods running newer versions of HomePod software.

## Installation

Download the latest release from [GitHub Releases](https://github.com/weixuanng/AirFlash_Wei/releases/latest), run
`Airplay-X.Y.Z.msi`, and launch Airplay from the installed shortcut. Apps and Features provides repair and uninstall.

The minimum target is **Windows x64, Windows 10 1809 / build 17763** (LTSC 2019),
including LTSC 2021, 22H2 and Windows 11. Windows 10 qualification is pending
affected-user testing. See [Windows compatibility and startup diagnostics](docs/WINDOWS-COMPATIBILITY.md)
for the candidate status and the PowerShell 5.1 diagnostic collector.

Airplay is distributed as a Windows x64 executable and standalone MSI. Windows may show
a SmartScreen warning because release binaries are not code-signed. Use
**More info** and **Run anyway** only when the downloaded files are from a
source you trust.

Airplay captures Windows system audio from the default playback device.
To stream only one application's audio, install a virtual endpoint
with [Virtual Audio Cable (VAC Lite)](https://software.muzychenko.net/freeware/vac471lite.zip)
or a similar tool, route that application's output to it, and set it as the
default playback device.

## Key features

- Streams Windows system audio to HomePod over AirPlay 2.
- Supports synchronized playback on existing two-device HomePod stereo pairs.
- Adds experimental two-receiver software stereo groups, independent hardware volume,
  per-receiver PCM/ALAC and UDP/TCP profiles, and 5 ms continuity transitions.
- Offers automatic device discovery.
- Includes tray operation, mute restoration, automatic reconnection, and
  connection diagnostics. Latency is fixed to the Real-time profile (120 ms).
- Captures Windows system audio from the default playback device, including virtual audio devices.
- Keeps the AirPlay session up through silence, reconnects automatically after short
  network interruptions, and resumes on its own when another device takes the speaker.
- Available as a portable Windows x64 app with English, Simplified Chinese, Traditional Chinese, and German UI.

## Feature plan

See [audio continuity and schema v4](docs/audio-stereo-v3.md) for setup, protocol details,
simulation results and remaining hardware acceptance. Samsung, Mac and mixed-brand
playback and acoustic synchronization have not been qualified by this implementation.

- [ ] Dolby Atmos support.

## Usage

1. Connect the PC and both HomePods to the same reachable local network.
2. Start Airplay and wait for the receiver list to populate.
3. Press **Connect** to link to your HomePod.

Latency is fixed to the Real-time profile (120 ms), the fastest option; there are no latency settings. The engine negotiates the stream format (codec, sample rate, UDP/TCP transport, and clock strategy) from the receiver's `/info` capabilities automatically, preferring 44.1 kHz. These scheduling values are not measured end-to-end audio latency.

Stopping playback or cancelling a connection immediately shows **Disconnecting…**.
The **Connect** button becomes available again after the old session is cleaned up.
Airplay drains final engine output during shutdown and gives the engine a shared
two-second stop budget before terminating an unresponsive engine process.

Airplay captures the default playback device through loopback, so whatever plays
on the PC streams to HomePod. Automatic local mute applies only to playback devices.

**Specific application** captures the selected application and its child processes;
browsers include the complete process tree, not individual tabs. This native mode
requires Windows build 20348 or later. Older Windows versions retain endpoint
capture. The application selection is saved by executable path and follows a
restart when exactly one matching instance is running. If several independent
instances match, select an instance again. A missing application never falls back
to system audio.

To hear the target application only on HomePod while games and notifications stay
on your headphones, install a virtual cable such as [VB-CABLE](https://vb-audio.com/Cable/),
use **Open Windows volume mixer** to route only that application's output to
**CABLE Input**, and set **CABLE Input** as the default playback device.
Disable **Listen to this device** and any local monitoring. The manual Windows
routing remains after Airplay stops; change it back when local playback is needed.
Virtual routing cannot guarantee capture of DRM-protected content, and capture,
resampling, and stream-format conversion do not guarantee bit-perfect output.

Receiver discovery runs while the control panel is visible, and remains active
for software stereo topology changes during playback. Otherwise it pauses when
the panel is hidden. Previous results remain available;
pausing discovery does not interrupt playback. If automatic connection is enabled
globally or for a receiver, Airplay also discovers devices for ten seconds at
startup. Open a receiver list to resume discovery afterwards.

Airplay intentionally uses fresh application storage under `%APPDATA%/Airplay`
and `%LOCALAPPDATA%/Airplay`. It does not read configuration, credentials, or
engine caches created by the previous product name; users must configure and pair
again after installing Airplay.

Use the tray menu (right-click the tray icon) > **Check for updates** to check for a stable release and open its download page. Checks run only when requested.

The real-time profile targets 120 ms; target and local transport timings are not measurements of end-to-end acoustic latency.

For `PTP exchange did not become ready`, see [PTP startup troubleshooting](docs/PTP-TROUBLESHOOTING.md)
for clock diagnostics and the correct engine process and UDP firewall rules.

See [Release workflow](docs/RELEASING.md) for version reservation and publishing.

See [Icon maintenance](docs/ICONS.md) for the vector source, generated resources, and high-DPI verification.

## Stack

- **C# / .NET 10 / WPF** for the Windows UI, tray integration, and discovery
- **Rust** for WASAPI capture, AirPlay 2 sessions, HAP, PTP, RTP, and audio transport
- **Windows DNS-SD** for receiver discovery
- **WASAPI loopback** for system audio capture
- **PCM and ALAC** with encrypted RTP for HomePod playback
- **uv, pytest, and Ruff** for the finite validation tools and Python checks

The published application is self-contained and statically links the native MSVC runtime. It does not require Python, a separately installed .NET runtime, or a separately installed VC++ Redistributable.

## Development and build

Development requires Windows 10/11 x64, the .NET 10 SDK, the Rust MSVC toolchain,
and Visual Studio C++/Windows SDK components. Python 3.12+ and `uv` are used for
validation scripts only.

```powershell
uv sync --locked
pwsh scripts/build-native.ps1 -Check
pwsh scripts/dotnet.ps1 restore AirFlash.sln --locked-mode
pwsh scripts/dotnet.ps1 run --project AirFlash.App/AirFlash.App.csproj
pwsh scripts/build-dev.ps1
```

The development build produces:

- `dist/dev/Airplay.exe`
- `dist/dev/Airplay-X.Y.Z-dev.<commit>.msi`

The development package is labeled from the latest stable `vX.Y.Z` tag and the
current commit. It does not reserve a release version or push a tag. Uninstall an
existing same-version Airplay package before installing a development MSI.

Use `pwsh scripts/build-release.ps1` only when performing a release; it requires a
clean release branch, reserves the next numeric version, and writes `dist/`.

Run the MSI directly. It installs the self-contained executable and provides optional desktop and Start menu shortcuts.
Airplay uses the Windows DNS-SD API and does not require Bonjour or install a background service.
The Rust engine is embedded in the WPF executable and extracted to a content-addressed cache under `%LOCALAPPDATA%/Airplay/engine/<hash>` at runtime.

## Tests

```powershell
pwsh scripts/dotnet.ps1 test AirFlash.sln
uv run pytest
uv run ruff check .
pwsh scripts/build-native.ps1 -Check
```

The ignored Rust soak test uses localhost UDP receivers only. Real HomePod tests
are intentionally limited to the repository's low-volume, five-second procedure.

See [Discovery stability](docs/DISCOVERY-STABILITY.md) for the isolated playback
retention regression check and repair validation notes.

## Contributing

Keep protocol behavior, pairing boundaries, and measurement limits explicit in
changes. Pull requests are welcome!

## License

[GPLv3+](LICENSE-GPLv3) or [Commercial License](LICENSE-COMMERCIAL.md).
