# Airplay 0.2.12

Changes since Airplay 0.2.11.

## Volume slider

- **The slider now shows the level.** In 0.2.11 the bar never filled, so the level could only be read from the number. The filled part is now drawn in blue on a clearer grey bar.

## Delay

- **The "Stable connection" tick box is now a Delay choice with three settings:**
  - **Lowest (adapts):** starts with a 50 ms buffer, less than half of before, and steps it up (to at most 200 ms) only when the stream has trouble. Use this for video.
  - **Balanced:** the fixed 120 ms buffer earlier versions used.
  - **Most stable:** the 500 ms buffer, for music on poor Wi-Fi.
- If you had "Stable connection" ticked, you are now on Most stable. Pick Lowest to try the shorter delay.
- A delay remains on every setting. Lowest has not been tested on a real HomePod, and a step up restarts the stream with a short gap.

## Artwork

- Sending artwork gets 15 seconds instead of 4 before it is given up on, so a slow moment on Wi-Fi no longer switches artwork off for the session.

Run **Airplay-0.2.12.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
