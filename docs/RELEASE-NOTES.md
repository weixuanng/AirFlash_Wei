# Airplay 0.2.10

Changes since Airplay 0.2.9.

## Sound Glow

- **Airplay now sends the artwork of what is playing to the HomePod.** A HomePod takes its Sound Glow colours from the artwork of the current track. Airplay already sent the title and artist but no artwork, so the glow kept the colours of whatever played before. Airplay now takes the thumbnail Windows has for the playing media, shrinks it to at most 512 pixels, and sends it with each new track.
- Colours can only follow media that gives Windows a thumbnail: Spotify, Apple Music, and video in Chrome or Edge usually do. Games and many other apps do not.
- Whether the HomePod uses artwork from Airplay for Sound Glow has not been tested on a real HomePod. The log has a `Now Playing artwork` line for what was sent and how the HomePod answered.

Run **Airplay-0.2.10.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
