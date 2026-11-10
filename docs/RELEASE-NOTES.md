# Airplay 0.2.11

Changes since Airplay 0.2.10.

## Volume

- **The volume now follows the slider while you drag it.** Before, the HomePod only changed once you stopped moving. Changes now go out as you drag, a few times a second.
- **The slider is a thick rounded bar that fills as you drag**, in the style of iOS Control Centre. Click or drag anywhere on it, or use the mouse wheel or arrow keys.
- The slider no longer disappears for a moment when one volume reading fails.

## Stutter

- **New option: Stable connection (adds delay).** A log from a ten-minute session showed 221 lost packets, 83 of which could not be re-sent in time with the 120 ms buffer. This option uses a 500 ms buffer so they can. Sound then runs about half a second behind video, so leave it off for video and turn it on for music.

## Capture

- **New option: Capture apps directly.** Takes audio from apps before the PC speakers, so the PC can stay muted and a silent output device is no longer needed. It is off by default and untested on real hardware: turn it off again if the HomePod goes silent. Needs Windows build 20348 or later.

None of this has been tested against a real HomePod.

Run **Airplay-0.2.11.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
