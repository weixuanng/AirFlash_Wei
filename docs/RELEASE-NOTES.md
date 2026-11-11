# Airplay 0.2.14

Changes since Airplay 0.2.13.

## Connection

- **Auto delay now reacts to Wi-Fi loss.** It used to watch only the PC's own timing, which stays perfect while the network drops packets. It now also watches what the speaker asks to have sent again, and steps the delay up when resent audio keeps arriving too late or loss stays at 2 % or more. One bad second does not trigger it.
- **Signal mark on the speaker in use.** Three bars beside "Playing" show how well audio is getting through over the last ten seconds. They turn amber and drop to two bars when packets are being resent, and to one when audio is being lost. Hover for the explanation.

## Look and motion

- **Real blur behind the panel.** Windows now blurs what is behind the panel instead of the panel only being tinted. If the panel looks wrong on your PC, turn off "Blur what is behind the panel" in the options.
- **The panel opens from the tray icon** and shrinks back to it when closed.
- **The volume slider follows the pointer on a short spring,** like the volume overlay.

Run **Airplay-0.2.14.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
