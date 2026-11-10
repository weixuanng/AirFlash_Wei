# Airplay 0.2.12

Changes since Airplay 0.2.11.

## Volume slider

- **The slider now shows the level.** In 0.2.11 the bar never filled, so the level could only be read from the number. The filled part is now drawn in blue on a clearer grey bar.

## Delay

- **The "Stable connection" tick box is now a Delay choice with three settings:**
  - **Lowest (adapts):** starts with a 50 ms buffer, less than half of before, and steps it up (to at most 200 ms) only when the stream has trouble. Use this for video.
  - **Balanced:** the 120 ms buffer earlier versions asked for.
  - **Most stable:** the 500 ms buffer, for music on poor Wi-Fi.
- If you had "Stable connection" ticked, you are now on Most stable. Pick Lowest to try the shorter delay.
- A delay remains on every setting. Lowest has not been tested on a real HomePod. If the HomePod will not start on it, choose Balanced.

## Everyday use

- **The keyboard volume keys control the HomePod while it is connected.** Volume up and down change the speaker instead of the PC. The mute key is left to Windows. Untick "Volume keys control the speaker" to turn this off.
- **The tray icon shows the state:** a green dot while streaming, amber while connecting or waiting for another device to finish, red on an error. Its tooltip shows the speaker volume.
- **Speakers can be hidden.** Right-click a speaker you never use and choose "Hide this speaker". "Show hidden speakers" under the list brings them back.
- With "Capture apps directly" on, changing the Windows output device now restarts the stream so the new device is muted as well.
- Airplay already connects to the last speaker when Windows starts, so nothing changed there.

## Artwork

- Sending artwork gets up to 15 seconds instead of 4 before it is given up on, so a slow moment on Wi-Fi no longer switches artwork off for the session.

Run **Airplay-0.2.12.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
