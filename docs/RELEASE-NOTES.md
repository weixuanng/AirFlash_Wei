# Airplay 0.2.17

Changes since Airplay 0.2.16.

## Lower delay (experimental)

These two are for finding out how low the delay can go on your speaker. Neither has been tested against a real HomePod.

- **Custom delay.** Delay has a fourth choice, Custom, with a slider from 40 to 300 ms. A speaker asked for less than it can play goes silent without any error, so the lowest value has to be found by ear. The stream reconnects each time the value changes.
- **Low latency (Mac mode).** Connects to a HomePod the way a Mac does (a Mac identity, uncompressed audio, a wide delay range), following what the open-source homepod-mini-linux project found a HomePod mini 2 accepts at a 70 ms delay. Off by default. Uncompressed audio is about twice the data, so it is more exposed to Wi-Fi loss.

## Speakers

- **Only HomePods are listed** by default, so televisions and other AirPlay devices no longer appear. "Only show HomePods" in Settings turns this off.

## Fixes

- **The Settings page was cut off at the bottom** when "Blur what is behind the panel" was on. The blurred area kept the height of the speakers page, so the lower rows were cropped. It now follows the panel's size.
- **A blue outline appeared around the settings button** after going back from Settings with the mouse. Focus now goes to the panel instead of the nearest button.

Run **Airplay-0.2.17.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
