# Airplay 0.2.15

Changes since Airplay 0.2.14.

## Status island

- **The black pill over the header now shows what the connection is doing,** not only errors: playing (level bars and the speaker's name), connecting, waiting for another device, standby, and errors. It sizes itself to its text and morphs between states. Click it for the detail; an error still opens by itself.
- The status lines at the bottom of the panel are gone. The island carries them.

## Settings

- **Settings have their own page,** behind the new button in the header. Esc or the back arrow returns to the speakers.
- **Options are switches** in grouped lists.
- **New: "Release the speaker when silent".** After three minutes without sound, Airplay lets go of the speaker so other devices can use it, and reconnects when sound plays again. The first moment of that sound is not heard. It is off by default.

## Look

- **Stronger glass:** the panel is more see-through, with brighter edges and a stronger highlight.

Run **Airplay-0.2.15.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
