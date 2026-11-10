# Airplay 0.2.11

Changes since Airplay 0.2.10.

## Volume

- **The volume now follows the slider while you drag it.** Before, the HomePod only changed once you stopped moving. Changes now go out as you drag, a few times a second.
- **The slider is a thick rounded bar that fills as you drag**, in the style of iOS Control Centre. Click or drag anywhere on it, or use the mouse wheel or arrow keys.
- The slider no longer disappears for a moment when one volume reading fails.

## Stutter

- **New option: Stable connection (adds delay).** A log from a ten-minute session showed 221 lost packets, 83 of which could not be re-sent in time with the 120 ms buffer. This option asks for a 500 ms buffer to give them time. Sound then runs about half a second behind video, so leave it off for video and turn it on for music.

## Capture

- **New option: Capture apps directly.** Takes audio from apps before the PC speakers, which is meant to let the PC stay muted without a silent output device. Whether it does on your PC is untested, so it is off by default: turn it off again if the HomePod goes silent. Needs Windows build 20348 or later.

None of this has been tested against a real HomePod.

Run **Airplay-0.2.11.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
