# Troubleshooting PTP startup

`PTP exchange did not become ready` means a receiver did not complete the
required clock exchanges before the startup deadline. Discovery and successful
audio-format negotiation do not establish that UDP timing traffic can pass.

Reproduce one connection failure and collect the matching `Session failure`
entry from `%APPDATA%\Airplay\logs\wpf-YYYY-MM-DD.log`. Its `ClockStatus`
snapshot includes the engine executable path, per-receiver role and local
address, packet counts, rejected-packet reasons, socket errors, and clock phase.
Use the snapshot from that attempt; an engine update can change the executable
path. Do not include pairing files or credentials.

| Peer phase | Meaning and next check |
| --- | --- |
| `no_rx` | No datagram from this receiver reached the PTP sockets. Check UDP 319/320, the active Windows network profile, VPN/routing, and receiver-side reachability. This alone does not prove a firewall block. |
| `rx_rejected` | Traffic arrived but failed source-port or packet validation. Inspect `packets.last_rejection`. |
| `waiting_announce` | Receiver-clock mode has not identified the receiver's announced clock. Confirm the selected clock mode and receiver behavior. |
| `waiting_sync_pair` | A fresh matching Sync/Follow_Up pair is missing. Inspect the packet types and rejection reason. |
| `waiting_delay_response` | Receiver-clock mode has no offset sample at all. A complete Sync/Follow_Up pair normally supplies one, so this phase means the pair cache is empty as well. |
| `waiting_exchanges` | More valid exchanges are needed, or earlier exchanges expired. Inspect exchange count, timeout and offset/RTT rejection reasons. |
| `ready` | The required timing exchanges passed. This is not an acoustic latency measurement. |

`received_by_type` uses the PTP message type as its index: Sync 0, Delay_Req 1,
Pdelay_Req 2, Follow_Up 8, Delay_Resp 9, Announce 11 and Signaling 12.

`offset_source` reports which exchange produced the receiver-to-local offset.
`sync` means a complete Sync/Follow_Up pair supplied it; that estimate omits the
path delay. `delay` means a matching delay response refined it by measuring the
round trip. That measurement remains preferred for two seconds after the last
accepted delay response; after it expires, current Sync pairs supply the offset
and `offset_source` returns to `sync`. Rejected responses do not extend this
period. A receiver that answers the sync exchange but never supplies a consistent
delay response therefore still reaches `ready` with `offset_source=sync`.

A rejected delay response is recorded in `packets.last_rejection` with the
computed round trip in `packets.last_round_trip_ns`. `round_trip` means the
computed round trip is outside the accepted 0–250 ms range; the sample is
discarded without refreshing the delay measurement. Report that number together
with the receiver model when a `sync`-sourced offset behaves incorrectly.

`network` counters include datagrams rejected before peer validation.
An OS error code in `last_send_error` or `last_receive_error` identifies a local
socket failure. Normal nonblocking polling is not logged as an error.

## Clock strategies on a standalone HomePod (software 27)

No published sender works with every standalone HomePod on HomePod software 27.
Some play only when the sender supplies the clock, others only when the sender
follows theirs, and a HomePod mini 2 on 27.2 has refused the arrangement that
works on a HomePod mini on 27.0. From 0.2.8 a single receiver that has the
standalone shape (see "Connected but no audio" below) and is left on Automatic
timing is therefore offered four arrangements in turn, one per connection
attempt:

| `Clock strategy:` | What the engine does | Taken from |
| --- | --- | --- |
| `sender` | Supplies the clock exactly as an iOS sender: Announce with priority1 128, clockClass 6, accuracy 0x21, variance 0x436a and time source GPS once a second, the 106-byte Apple Signaling once a second, Sync/Follow_Up eight times a second, all starting on the session SETUP reply. Ready when the receiver has sent three Delay_Req. | OwnTone (libairptp), Music Assistant |
| `follow_quiet` | Follows the receiver's clock and sends it no PTP message at all. The offset comes from its Sync/Follow_Up pairs. | Music Assistant's fix for HomePod software 27 |
| `follow_active` | Follows the receiver as a Mac does: the SETUP names no clock of its own (`SupportsClockPortMatchingOverride` true, no `ClockID`), no SETPEERS is sent, and the engine introduces itself (see the next section) and sends the Apple Signaling once a second. | the `airplay` hex package, airplay2-rs |
| `follow_peers` | Follows the receiver as releases up to 0.2.7 did (own `ClockID`, SETPEERS, unicast requests, Delay_Req, introduction), now with the Apple Signaling once a second. | upstream AirFlash |

The log has one `Clock strategy: <name>; receiver <id>; attempt <n>` line per
attempt, and `Clock strategy <name> did not hold (<code>); next: <name>` when the
clock never became ready (`clock_not_ready`) or was lost within 30 seconds of
streaming. Other failures, such as a network drop, keep the arrangement. One
that holds its clock for 30 seconds is saved in
`%APPDATA%\Airplay\clock-strategy.json` and used first from then on; delete
that file to start again from `sender`. Choosing PTP or Follow in Receivers
turns the rotation off and uses that one arrangement.

In the `clock_status` snapshot, `strategy` names the arrangement the engine is
running, `named_by_peer` is true once the receiver has addressed a Signaling to
the engine's port identity (it has registered the engine as a timing peer), and
in the `sender` arrangement `bmca_*` hold what the receiver still announces and
`announces_naming_us` counts Announces in which it named the engine as
grandmaster. A `sender` attempt that ends in `waiting_exchanges` with
`received_by_type[1]` (Delay_Req) at 0 means the receiver never followed the
engine's clock.

A single delay response that disagrees with the running offset by more than 5 ms
(`last_rejection` `offset_jump`) no longer ends a followed session: the delay
refinement is dropped and the Sync/Follow_Up estimate carries the timeline.
Before 0.2.8 that one sample reset the association and was reported as
"PTP exchanges stopped". A delay response whose offset is more than 10 ms above
the Sync/Follow_Up estimate (`delay_disagrees_with_sync`, an apparent round trip
of more than about 20 ms) is refused as well: a
HomePod's delay timestamps can sit a tenth of a second from its own Sync
timestamps and still pass the round-trip gate, and using one moves the audio
anchor by half that error.

## A followed receiver announces but never syncs

`waiting_sync_pair` with many Announce (type 11) and Signaling (type 12)
datagrams, no Sync (type 0) or Follow_Up (type 8), and `received_grants` 0 means
the receiver is reachable and names itself grandmaster, but its clock is idle.
A HomePod on software 27.2 does this to a sender that never takes part in PTP
as a clock; it does not answer the unicast subscription requests, and HomePods
do not answer a Delay_Req that carries a real clock identity.

From 0.2.7 the engine uses two remedies taken from working implementations.
They did not start the clock of a HomePod mini 2 on 27.2, which is why 0.2.8
tries the arrangements above in turn; the remedies remain part of
`follow_active` and of a Follow timing chosen by hand:

1. **Introduction**, sent as soon as SETPEERS is accepted. It is what a Mac
   sends when it joins as a clock that will follow: three Sync/Follow_Up pairs
   125 ms apart, two Announces with priority1 250 (worse than a HomePod's 248),
   then one Signaling with an 802.1AS message-interval request (Sync 8 per
   second, Announce 4 per second) and two Apple extension TLVs.
2. **Wake**, if three Announces still arrive without Sync traffic 1.5 s later.
   This is nqptp's sequence for a silent Apple clock: one Announce with a
   priority1 one step better than the receiver's, then two Announces one step
   worse, 150 ms apart.

If the clock stays silent the engine repeats the introduction after 2 s, this
time with the message-interval request encoded without its two reserved bytes,
and wakes once more 1.5 s after that, then stops. Silence means three Announces
and no Sync or Follow_Up for 1.5 s, so a receiver that syncs slowly is not woken. A single complete pair restores
that budget; once three exchanges have made the clock usable it is never nudged
again for that session, so a streaming receiver is left alone.

The snapshot reports `introductions`, `wake_attempts`, `announces_without_sync`
and `announces_naming_us` (Announces in which the receiver named the engine as
grandmaster). `first_pair_ms`, `first_pair_after_wakes` and
`first_pair_after_introductions` say when the first complete pair arrived and
which remedy preceded it. `packets.sent_by_type` counts what the engine sent,
and `packets.first_received` / `packets.last_received` hold the first and latest
datagram of each received type as hex, up to 128 bytes. The log also records
`Receiver info:` (model and software version) and `Receiver timing:` (the key
names and `timingPeerInfo` of the receiver's SETUP reply, at most 2 KB).

These entries contain local network addresses and a clock identity derived
from a network adapter address. Remove them before posting a log publicly.

If Sync still never arrives, include the whole `Session failure` entry in the
report: the Announce and Signaling samples show what the receiver expects.

## Connected but no audio on a standalone HomePod

A standalone HomePod on HomePod software 27 keeps announcing itself as the PTP
grandmaster and stays silent when the sender also supplies the timeline.
Airplay recognises the standalone shape from the receiver TXT record (`model`
starting with `AudioAccessory`, `igl=1`, no `pgid`, no `tsid`, and `osvers` 27 or
later) and follows that receiver's clock instead. Releases before 0.5.0 always
acted as the grandmaster, so they establish the session and send audio that the
HomePod never renders.

The `Receiver negotiated:` line in `wpf-YYYY-MM-DD.log` records the selected
codec, transport, timing and `clock_role`. `clock_role follow` means the
receiver's clock is in use. `clock_role sender` means Airplay supplies the
timeline, which is correct for a stereo pair or an Apple TV output but not for a
standalone OS 27 HomePod. An NTP-timed realtime stream is also silent on a
HomePod, so a followed receiver stays on PTP even when the PTP feature bit is not
advertised.

If a current release still streams without audio while `clock_role follow` is
reported, include that line and the `clock_status` snapshot. UDP 319/320 must
reach the engine for the follow exchange, so the firewall checks below still
apply.

## Windows Firewall

The process owning PTP sockets is
`%LOCALAPPDATA%\Airplay\engine\<hash>\airflash-engine.exe`, extracted from the
application. A program rule for the outer `Airplay.exe` does not match that
process. The hash directory changes when the engine changes.

In Windows Defender Firewall with Advanced Security, inspect rules applying to
the engine path in the failure snapshot:

- Timing uses **UDP**, not TCP. Check the actual protocol field, not the rule name.
- Incoming timing traffic uses local ports **319 and 320**.
- If outbound traffic is restricted, outgoing timing traffic uses remote ports
  **319 and 320**.
- The rule's profile must match the current connection. A Private-only rule
  does not apply to a connection categorized as Public.
- Restrict any exception to the trusted receiver or local subnet and the
  intended network profile. An explicit block rule can override an allow rule.
- Recheck the program path after an update; an exception for an older hash
  directory does not cover the new engine.

Airplay reports the path and timing evidence; it does not change firewall
policy automatically. On managed computers, local rules may be overridden by
administrator policy.

If timing still fails, include the complete failure snapshot and the HomePod
software version in the issue. Packet capture around the failed attempt, filtered
to UDP 319/320, can distinguish traffic blocked before the process from packets
the process receives and rejects. Playback or loud test tones are not needed.
