# AirFlash_Wei — fork notes

Fork of Ding-Kyoma/AirFlash (GPLv3), kept as a public fork for a seamless
Windows-to-HomePod Mini 2 build.

## Direction (v1)

- Persistent connection: once connected, the session stays up through silence
  and pauses. It only drops when the speaker is switched to another source
  (e.g. iPhone AirPlay), then auto-resumes when free.
- Apple-style UI: clean minimal layout, glass/mica backdrop, spring motion,
  press scale 0.97, staggered entrance, SF Pro-style type (Inter fallback).
- One Play button, auto-connect on launch, tray-first, defaults locked
  (44.1 kHz, Normal 200 ms); advanced settings behind a panel.
- Upstream license and attribution kept (LICENSE-GPLv3).

Work happens on `claude/*` branches, PRs into `main`, CI on Windows runners.
