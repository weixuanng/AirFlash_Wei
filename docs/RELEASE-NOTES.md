# Airplay 0.2.7

Changes since Airplay 0.2.6.

## HomePod stuck on "Connecting"

- On HomePod software 27.2 the HomePod accepted the connection and announced its clock, but never sent the timing packets Airplay waits for, so every attempt ended with "PTP exchange did not become ready" and the panel stayed on "Connecting".
- Airplay now prompts an idle HomePod clock to start, using the sequence the open-source nqptp project sends to Apple receivers. It is tried up to three times per connection attempt.
- This fix has not been tested against a real HomePod. If it still does not connect, the log now contains what is needed to see why.

## Compact panel

- The panel is 320 wide instead of 400, with tighter spacing. The Connect button keeps its full height.
- The error pop-up now sits beside the Quit button and never covers it.

## Diagnostics

- A failed connection now records how many wake attempts were made, what Airplay sent, and the first and latest packet of each kind the HomePod sent.
- The log records the HomePod's model and software version and its timing reply for each attempt.

Run **Airplay-0.2.7.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
