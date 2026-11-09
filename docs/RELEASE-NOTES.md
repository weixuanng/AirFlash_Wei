# Airplay 0.2.6

Changes since Airplay 0.2.4.

## New look

- Apple system colours in light and dark mode, rounder panel and cards, and a solid blue speaker tile.
- The panel springs up from the taskbar when it opens. The status dot shows the state: green when connected, a blue pulse while connecting, orange when another device has the speaker, red on error.
- Level bars move on the speaker that is playing.
- Errors open in a black pill over the header, settle to a compact pill after a few seconds, and reopen when clicked.
- New icon and the bundled Inter font. SF Pro is used if it is installed.

## Install fixes

- The installer now places the audio engine next to Airplay.exe and adds a Windows Firewall rule for it, limited to the local network. Before, the engine ran from a folder that changed with every update, so Windows Firewall could prompt again or block HomePod clock sync.
- Uninstall now removes the start-with-Windows entry.
- Old copies of the engine in %LOCALAPPDATA% are cleaned up.
- The installer is a single page with an "Open Airplay now" option at the end.
- The installer and Windows sign-out can now close Airplay when only its tray icon is running.

Run **Airplay-0.2.6.msi** to install or to upgrade 0.2.4 in place. Before upgrading from 0.2.4, quit Airplay from its tray menu. 0.2.4 cannot be closed by the installer when its panel has not been opened since sign-in. If setup says Airplay could not exit, end Airplay.exe in Task Manager and run setup again. From 0.2.6 on, the installer closes Airplay itself. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

The installer's install, upgrade, repair and uninstall steps and its firewall rule are tested automatically on Windows. No real HomePod playback test was run for this release.
