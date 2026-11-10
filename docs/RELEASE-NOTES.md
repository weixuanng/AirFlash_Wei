# Airplay 0.2.8

Changes since Airplay 0.2.7.

## HomePod on software 27 still not connecting

0.2.7 did not fix the connection, and the reason given for it was wrong. The log from a HomePod mini 2 on 27.2 showed that nothing Airplay sent made the HomePod start its clock, and that the few sessions that did connect were ended by Airplay itself.

- **Airplay no longer relies on one way of sharing a clock with the HomePod.** Other AirPlay senders disagree about what a HomePod on software 27 needs, and none has been tested on this model. Airplay now tries four arrangements in turn, one per connection attempt, and remembers the one that holds a connection for 30 seconds:
  1. Airplay supplies the clock, sending exactly what an iPhone sends. On Automatic, earlier versions never tried this on a HomePod on software 27, and their version of it left out a message every Apple device sends once a second.
  2. Airplay follows the HomePod's clock and sends it nothing, as Music Assistant does.
  3. Airplay follows the HomePod's clock the way a Mac does.
  4. Airplay follows the HomePod's clock as 0.2.7 did, with one more message every Apple device sends.
- **A connected session is no longer dropped by one odd timing reply.** While following the HomePod's clock, a single reply that moved the measured clock offset by more than 5 ms was treated as a lost clock: Airplay stopped sending audio and reported "PTP exchanges stopped". That ended three of the four sessions that connected on 0.2.6 and 0.2.7. Such a reply is now ignored.
- **Timing replies that contradict the HomePod's own clock are no longer used.** This HomePod's replies were often a tenth of a second away from its clock packets. One that slipped through could shift the audio by that much, which is longer than the 120 ms of buffer and is a likely reason a connected session stayed silent.
- The first connection can take up to about 45 seconds while the arrangements are tried. Later connections start with the one that worked.
- None of this has been tested against a real HomePod. If it still does not connect, the log now shows which arrangement was tried on each attempt and how the HomePod answered each one.

## Diagnostics

- The log has a `Clock strategy:` line for every attempt.
- The clock report says whether the HomePod registered Airplay as a timing peer and what it announced while Airplay supplied the clock.

Run **Airplay-0.2.8.msi** to install or upgrade. The portable **Airplay.exe** still works but does not get the firewall rule. Verify downloads with **SHA256SUMS.txt**. Binaries are unsigned, so Windows SmartScreen may ask you to confirm the first run.

Logs are in `%APPDATA%\Airplay\logs`.
