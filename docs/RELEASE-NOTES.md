# Airplay 0.2.9

Changes since Airplay 0.2.8.

## Volume

- **The panel has a volume slider for the HomePod.** It sets the speaker's own volume, the same level its touch surface changes. It appears while Airplay is streaming and the HomePod reports its volume.

## Sharing the HomePod with an iPhone

- **Airplay no longer takes the HomePod back from an iPhone.** When audio was moved from an iPhone to the HomePod, the HomePod ended Airplay's session and Airplay reconnected within seconds, cutting the iPhone off. The HomePod plays handed-over audio itself and does not report an AirPlay session for it, so Airplay judged it free. Airplay now also reads the HomePod's "playing" flag, waits while it is set, and resumes once the HomePod is idle.
- After the HomePod ends a session, Airplay waits 4 seconds before checking whether it is free.

Neither change has been tested against a real HomePod. The "playing" flag was identified from one HomePod mini 2 on software 27.2.

Run **Airplay-0.2.9.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
