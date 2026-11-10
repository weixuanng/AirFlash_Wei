# Airplay 0.2.13

Changes since Airplay 0.2.12.

## Delay

- **"Lowest (adapts)" played nothing on a HomePod and is now "Auto (adapts)".** A HomePod that keeps its own clock stays silent with a 50 ms buffer, and nothing reported the silence, so the delay never stepped up. Auto now starts at 120 ms on such a HomePod, the shortest delay known to play, and still steps up (to at most 200 ms) when the stream has trouble.
- On such a HomePod, Auto and Balanced now start with the same delay. Auto raises it by itself when the stream has trouble; Balanced stays at 120 ms.

## Look and motion

- **HomePods have their own icon** in the speaker list.
- **Animations run at the screen's refresh rate.** They were stepped 60 times a second on every screen, which showed as judder at 120 Hz.
- **Springs follow Apple's duration and bounce model,** and speaker rows respond to a press.

Run **Airplay-0.2.13.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
