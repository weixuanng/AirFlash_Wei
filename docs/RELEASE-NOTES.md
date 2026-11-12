# Airplay 0.2.17

Changes since Airplay 0.2.16.

## Fixes

- **The Settings page was cut off at the bottom** when "Blur what is behind the panel" was on. The blurred area kept the height of the speakers page, so the lower rows were cropped. It now follows the panel's size.
- **A blue outline appeared around the settings button** after going back from Settings with the mouse. Focus now goes to the panel instead of the nearest button.

Run **Airplay-0.2.17.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
