use super::*;

fn follower(now: Instant) -> PeerState {
    let mut state = PeerState {
        follow: true,
        active: true,
        ..Default::default()
    };
    followed_packet(&mut state, &announce(8, 0, 0), 0, now, 7);
    state
}
#[test]
fn a_sync_only_mapping_freezes_expires_and_never_survives_an_identity_change() {
    let start = Instant::now();
    let mut state = follower(start);
    for seq in 0..3u16 {
        let at = start + Duration::from_millis(u64::from(seq) * 125);
        let local = 1_000_000_000 + u64::from(seq) * 125_000_000;
        let (sync, follow) = sync_pair(8, seq, local + 5_000_000_000);
        followed_packet(&mut state, &sync, local + 90_000, at, 7);
        followed_packet(&mut state, &follow, local + 95_000, at, 7);
    }
    let last = start + Duration::from_millis(250);
    assert!(state.offset_at.is_none(), "no Delay response was needed");
    let (offset, frozen) = state.map_offset(last, Duration::from_secs(10)).unwrap();
    assert!(!frozen);
    assert_eq!(
        state.map_offset(last + Duration::from_secs(3), Duration::from_secs(10)),
        Some((offset, true))
    );
    assert_eq!(
        state.map_offset(last + Duration::from_secs(11), Duration::from_secs(10)),
        None
    );
    followed_packet(
        &mut state,
        &announce(9, 1, 0),
        0,
        last + Duration::from_secs(4),
        7,
    );
    assert_eq!(
        state.map_offset(last + Duration::from_secs(4), Duration::from_secs(10)),
        None
    );
}

#[test]
fn reordered_pairs_preserve_sample_correction_and_reach_readiness() {
    let start = Instant::now();
    let mut state = follower(start);
    for seq in 0..3u16 {
        let now = start + Duration::from_secs(u64::from(seq));
        let base = 1_000_000_000 + u64::from(seq) * 1_000_000_000;
        let (sync, follow) = sync_pair(8, seq, base + 1_000_000);
        followed_packet(&mut state, &follow, base + 90_000, now, 7);
        followed_packet(&mut state, &sync, base + 100_000, now, 7);
        let mut unrelated_sync = sync_pair(8, seq + 100, base).0;
        unrelated_sync[8..16].copy_from_slice(&(90_000_000i64 * 65536).to_be_bytes());
        followed_packet(&mut state, &unrelated_sync, base + 150_000, now, 7);
        state.delay = Some((seq, base + 200_000));
        state.delay_age = Some(now);
        let mut response = header(9, 54, 8, seq, 0x0408, 0);
        timestamp(&mut response[34..44], base + 1_300_000);
        response[44..52].copy_from_slice(&7u64.to_be_bytes());
        response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
        followed_packet(&mut state, &response, base + 400_000, now, 7);
        followed_packet(&mut state, &response, base + 400_000, now, 7);
        assert_eq!(state.exchanges, u64::from(seq) + 1);
    }
    assert!(state.ready(start + Duration::from_secs(2)));
    assert_eq!(state.offset, Some(1_000_000.0));
    assert_eq!(state.round_trip_ns, Some(200_000.0));
}

#[test]
fn cache_handles_backlog_wrap_duplicates_expiry_and_identity_changes() {
    let start = Instant::now();
    let mut state = follower(start);
    let (sync_a, follow_a) = sync_pair(8, u16::MAX, 1_000_000);
    let (sync_b, follow_b) = sync_pair(8, 0, 2_000_000);
    followed_packet(&mut state, &sync_a, 100_000, start, 7);
    followed_packet(&mut state, &sync_b, 200_000, start, 7);
    followed_packet(&mut state, &follow_b, 210_000, start, 7);
    followed_packet(&mut state, &follow_a, 220_000, start, 7);
    assert_eq!(state.sync_pair.unwrap().origin, 2_000_000);
    followed_packet(&mut state, &sync_b, 300_000, start, 7);
    followed_packet(&mut state, &follow_b, 310_000, start, 7);
    assert_eq!(state.sync_pair.unwrap().received, 200_000);

    let (sync_c, follow_c) = sync_pair(8, 1, 3_000_000);
    followed_packet(&mut state, &sync_c, 400_000, start, 7);
    followed_packet(&mut state, &follow_c, 410_000, start + SAMPLE_LIFETIME, 7);
    assert_eq!(state.sync_pair.unwrap().origin, 2_000_000);
    for seq in 2..100u16 {
        followed_packet(
            &mut state,
            &sync_pair(8, seq, 0).0,
            u64::from(seq),
            start + SAMPLE_LIFETIME,
            7,
        );
    }
    assert_eq!(state.pending_syncs.len(), MAX_PENDING_SYNCS);
    followed_packet(
        &mut state,
        &announce(9, 0, 0),
        0,
        start + SAMPLE_LIFETIME,
        7,
    );
    assert!(state.pending_syncs.is_empty());
    assert!(state.sync_pair.is_none());
    followed_packet(&mut state, &follow_b, 0, start + SAMPLE_LIFETIME, 7);
    assert_eq!(state.packets.last_rejection, Some("source_identity"));
}

#[test]
fn a_changed_announce_identity_is_timestamped_once_per_restart() {
    let start = Instant::now();
    let mut state = follower(start);
    // The first Announce established the identity and is not a change.
    assert_eq!(state.identity_change_at(), None);
    note_announce_identity(
        &mut state,
        &announce(9, 0, 0),
        start + Duration::from_secs(1),
    );
    followed_packet(
        &mut state,
        &announce(9, 0, 0),
        0,
        start + Duration::from_secs(1),
        7,
    );
    assert_eq!(
        state.identity_change_at(),
        Some(start + Duration::from_secs(1))
    );
    assert_eq!(state.identity_changes, 1);
    // Repeating the same identity must not count again.
    note_announce_identity(
        &mut state,
        &announce(9, 1, 0),
        start + Duration::from_secs(2),
    );
    followed_packet(
        &mut state,
        &announce(9, 1, 0),
        0,
        start + Duration::from_secs(2),
        7,
    );
    assert_eq!(
        state.identity_change_at(),
        Some(start + Duration::from_secs(1))
    );
    assert_eq!(state.identity_changes, 1);
    // A second restart moves the timestamp.
    note_announce_identity(
        &mut state,
        &announce(10, 0, 0),
        start + Duration::from_secs(3),
    );
    assert_eq!(
        state.identity_change_at(),
        Some(start + Duration::from_secs(3))
    );
    assert_eq!(state.identity_changes, 2);
}

#[test]
fn a_silent_peer_reports_when_its_association_stalled() {
    let start = Instant::now();
    let state = PeerState {
        follow: true,
        active: true,
        last: Some(start),
        exchange_interval: Some(Duration::from_secs(1)),
        ..Default::default()
    };
    assert_eq!(state.stalled_at(start + Duration::from_millis(500)), None);
    assert_eq!(
        state.stalled_at(start + Duration::from_secs(3)),
        Some(start),
        "three missed 1 s exchanges is the same bounded timeout readiness uses"
    );
}

#[test]
fn new_unmatched_sync_cannot_refresh_an_expired_complete_sample() {
    let start = Instant::now();
    let mut state = follower(start);
    let (sync, follow) = sync_pair(8, 1, 1_000_000);
    followed_packet(&mut state, &sync, 100_000, start, 7);
    followed_packet(&mut state, &follow, 110_000, start, 7);
    let later = start + Duration::from_secs(3);
    followed_packet(
        &mut state,
        &sync_pair(8, 2, 3_001_000_000).0,
        3_000_100_000,
        later,
        7,
    );
    state.delay = Some((3, 3_000_200_000));
    state.delay_age = Some(later);
    let mut response = header(9, 54, 8, 3, 0x0408, 0);
    timestamp(&mut response[34..44], 3_001_300_000);
    response[44..52].copy_from_slice(&7u64.to_be_bytes());
    response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
    followed_packet(&mut state, &response, 3_000_400_000, later, 7);
    // Only the first complete pair counts as an exchange; the later Sync has no
    // matching Follow_Up and must not refresh or re-arm the sample.
    assert_eq!(state.exchanges, 1);
    assert_eq!(state.sync_pair.map(|sample| sample.received), Some(100_000));
    assert_eq!(
        state.packets.last_rejection,
        Some("delay_identity_or_stale_sample")
    );
}

#[test]
fn short_retry_accepts_an_earlier_outstanding_grant_and_renews_short_leases() {
    let start = Instant::now();
    let mut state = follower(start);
    for seq in [1, 2] {
        state.subscriptions[0].push_back(Subscription {
            sequence: seq,
            interval: -3,
            sent: start + Duration::from_millis(u64::from(seq - 1) * 500),
        });
    }
    let mut request = subscription(7, Some((8, 0x8005)), 1, 0);
    request[50..54].copy_from_slice(&6u32.to_be_bytes());
    let grant = unicast_grants(&request, 8, 1).remove(0);
    let now = start + Duration::from_millis(700);
    followed_packet(&mut state, &grant, 0, now, 7);
    assert_eq!(state.received_grants, 1);
    state.offset = Some(0.0);
    state.offset_at = Some(start);
    for millis in [0, 125, 250] {
        state.exchanged(start + Duration::from_millis(millis));
    }
    assert!(!state.subscription_needed(0, now));
    assert!(state.subscription_needed(0, now + Duration::from_secs(1)));
    state.next_subscription = Some(now + SUBSCRIPTION_RETRY);
    assert!(!state.subscription_due(now));
    assert!(state.subscription_due(now + SUBSCRIPTION_RETRY));
}

#[test]
fn advertised_slow_exchange_streak_expires_with_a_bounded_timeout() {
    let start = Instant::now();
    let mut state = PeerState {
        exchange_interval: Some(Duration::from_secs(4)),
        ..Default::default()
    };
    for seconds in [0, 4, 8] {
        state.exchanged(start + Duration::from_secs(seconds));
    }
    assert!(state.ready(start + Duration::from_secs(8)));
    assert_eq!(state.startup_timeout(), Duration::from_secs(14));
    assert!(!state.ready(start + Duration::from_secs(20)));
    state.exchanged(start + Duration::from_secs(20));
    assert_eq!(state.exchanges, 1);
}

#[test]
fn packet_validation_explains_rejections_without_relaxing_identity_checks() {
    for (index, value, reason) in [
        (0, 0, "profile_or_version"),
        (1, 1, "profile_or_version"),
        (4, 1, "domain"),
        (3, 40, "length"),
    ] {
        let mut packet = sync_pair(8, 1, 0).0;
        packet[index] = value;
        assert_eq!(packet_rejection(&packet), Some(reason));
    }
    assert_eq!(packet_rejection(&[0; 12]), Some("short_header"));
    assert_eq!(packet_rejection(&header(2, 44, 8, 1, 0, 0)), Some("length"));
}

#[test]
fn loop_retries_dropped_subscription_and_answers_pdelay_while_following() {
    // Protocol-fixed PTP ports on a fresh loopback address: parallel suites
    // cannot collide, and the sender still targets the real receiver ports.
    let (peer, remote_event, remote_general) = crate::test_net::ptp_receiver_sockets();
    remote_event.set_nonblocking(true).unwrap();
    remote_general.set_nonblocking(true).unwrap();
    let event = UdpSocket::bind("127.0.0.1:0").unwrap();
    let general = UdpSocket::bind("127.0.0.1:0").unwrap();
    let local_event = event.local_addr().unwrap();
    let local_general = general.local_addr().unwrap();
    let timer =
        PtpMaster::from_sockets(vec![peer], vec![peer], Clock::new(), event, general).unwrap();
    thread::sleep(Duration::from_millis(150));
    let mut buffer = [0u8; 2048];
    assert!(remote_general.recv_from(&mut buffer).is_err());
    timer.activate(peer, "127.0.0.1".parse().unwrap());
    let started = Instant::now();
    let request = header(2, 54, 8, 77, 0x0408, 0);
    remote_event.send_to(&request, local_event).unwrap();
    // Invalid traffic must be visible without making the clock ready.
    remote_general.send_to(&[0; 12], local_general).unwrap();
    let mut requests = 0;
    let mut pdelay_event = false;
    let mut pdelay_follow = false;
    while started.elapsed() < Duration::from_secs(3) {
        while let Ok((n, _)) = remote_general.recv_from(&mut buffer) {
            if buffer[0] & 15 == 12 && n == 54 && buffer[48] == 0 {
                requests += 1; // Deliberately drop every subscription.
            }
            if buffer[0] & 15 == 10 {
                assert_eq!(&buffer[30..32], &77u16.to_be_bytes());
                assert_eq!(&buffer[44..54], &request[20..30]);
                pdelay_follow = true;
            }
        }
        while let Ok((_, _)) = remote_event.recv_from(&mut buffer) {
            if buffer[0] & 15 == 3 {
                assert_eq!(&buffer[44..54], &request[20..30]);
                pdelay_event = true;
            }
        }
        if requests >= 2 && pdelay_event && pdelay_follow {
            break;
        }
        thread::sleep(Duration::from_millis(5));
    }
    assert!(
        requests >= 2,
        "lost subscription must retry inside startup deadline"
    );
    assert!(pdelay_event && pdelay_follow);
    let snapshot = timer.snapshots().remove(0);
    assert_eq!(snapshot.packets.pdelay_responses, 1);
    assert_eq!(snapshot.packets.last_rejection, Some("short_header"));
    assert_eq!(snapshot.exchanges, 0);
    assert!(!snapshot.ready);
    assert_eq!(snapshot.phase, "waiting_announce");
    assert!(timer.network_snapshot().event_received > 0);
    assert!(timer.network_snapshot().general_received > 0);
}

#[test]
fn localhost_follower_locks_after_receiver_drops_the_first_subscription() {
    let (peer, remote_event, remote_general) = crate::test_net::ptp_receiver_sockets();
    remote_event.set_nonblocking(true).unwrap();
    remote_general.set_nonblocking(true).unwrap();
    let event = UdpSocket::bind("127.0.0.1:0").unwrap();
    let general = UdpSocket::bind("127.0.0.1:0").unwrap();
    let local_event = event.local_addr().unwrap();
    let local_general = general.local_addr().unwrap();
    let clock = Clock::new();
    let timer =
        PtpMaster::from_sockets(vec![peer], vec![peer], clock.clone(), event, general).unwrap();
    timer.activate(peer, "127.0.0.1".parse().unwrap());
    let started = Instant::now();
    let mut next_sync = started;
    let mut sync_sequence = 0;
    let mut subscriptions = 0;
    let mut buffer = [0u8; 2048];
    while started.elapsed() < Duration::from_secs(7) && !timer.ready(peer) {
        while let Ok((n, _)) = remote_general.recv_from(&mut buffer) {
            if n == 54 && buffer[0] & 15 == 12 && buffer[48] == 0 {
                subscriptions += 1;
            }
        }
        let now = Instant::now();
        if subscriptions >= 2 && now >= next_sync {
            remote_general
                .send_to(&announce(8, sync_sequence, 0), local_general)
                .unwrap();
            let (sync, follow) = sync_pair(8, sync_sequence, clock.now_ns() + 1_000_000);
            remote_event.send_to(&sync, local_event).unwrap();
            remote_general.send_to(&follow, local_general).unwrap();
            sync_sequence = sync_sequence.wrapping_add(1);
            next_sync = now + Duration::from_millis(125);
        }
        while let Ok((n, _)) = remote_event.recv_from(&mut buffer) {
            if subscriptions >= 2 && n == 44 && buffer[0] & 15 == 1 {
                let sequence = u16::from_be_bytes([buffer[30], buffer[31]]);
                let mut response = header(9, 54, 8, sequence, 0x0408, 0);
                timestamp(&mut response[34..44], clock.now_ns() + 1_000_000);
                response[44..54].copy_from_slice(&buffer[20..30]);
                remote_general.send_to(&response, local_general).unwrap();
            }
        }
        thread::sleep(Duration::from_millis(2));
    }
    assert!(subscriptions >= 2);
    assert!(
        timer.ready(peer),
        "follower must lock before the ordinary eight-second deadline"
    );
    let snapshot = timer.snapshots().remove(0);
    assert!(snapshot.exchanges >= 3);
    assert!(snapshot.offset_ns.is_some());
    assert_eq!(snapshot.phase, "ready");
}

#[test]
fn stale_pair_cannot_rewind_the_sync_offset_or_exchange_streak() {
    let start = Instant::now();
    let mut state = follower(start);
    let receiver_offset: i128 = 5_000_000_000;
    let newest = 2_000_000_000u64;
    let (sync, follow) = sync_pair(8, 2, (newest as i128 + receiver_offset) as u64);
    followed_packet(&mut state, &sync, newest + 10_000, start, 7);
    followed_packet(&mut state, &follow, newest + 20_000, start, 7);
    let stale = 1_000_000_000u64;
    let later = start + Duration::from_millis(125);
    let (sync, follow) = sync_pair(8, 1, (stale as i128 + receiver_offset) as u64);
    // Both packets arrive after the newer pair, so local receive timestamps
    // alone cannot identify this older source sequence.
    followed_packet(&mut state, &sync, newest + 125_010_000, later, 7);
    followed_packet(&mut state, &follow, newest + 125_020_000, later, 7);
    assert_eq!(state.exchanges, 1);
    assert_eq!(
        state.sync_pair.map(|sample| sample.received),
        Some(newest + 10_000)
    );
    let offset = state.sync_offset.expect("sync offset");
    assert!((offset - (receiver_offset as f64 - 10_000.0)).abs() < 1.0);
}

#[test]
fn follower_locks_from_sync_pairs_when_delay_responses_stay_unusable() {
    // Receivers such as HomePod OS 27 answer the delay request with timestamps
    // that contradict their own sync timestamps. The session must still obtain a
    // usable timeline from the sync exchange instead of failing to start.
    let start = Instant::now();
    let mut state = follower(start);
    let receiver_offset: i128 = 4_200_000_000;
    for seq in 0..3u16 {
        let at = start + Duration::from_millis(u64::from(seq) * 125);
        let local = 1_000_000_000 + u64::from(seq) * 1_000_000_000;
        let (sync, follow) = sync_pair(8, seq, (local as i128 + receiver_offset) as u64);
        followed_packet(&mut state, &sync, local + 90_000, at, 7);
        followed_packet(&mut state, &follow, local + 95_000, at, 7);
        state.delay = Some((seq, local + 200_000));
        state.delay_age = Some(at);
        let mut response = header(9, 54, 8, seq, 0x0408, 0);
        timestamp(&mut response[34..44], 17);
        response[44..52].copy_from_slice(&7u64.to_be_bytes());
        response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
        followed_packet(&mut state, &response, local + 400_000, at, 7);
        assert_eq!(state.packets.last_rejection, Some("round_trip"));
    }
    assert_eq!(state.exchanges, 3);
    assert_eq!(state.packets.rejected, 3);
    assert!(state.offset.is_none(), "no delay response was accepted");
    let now = start + Duration::from_millis(250);
    assert_eq!(state.offset_source(now), Some("sync"));
    let negative_rtt = state
        .packets
        .last_round_trip_ns
        .expect("round trip recorded");
    assert!(negative_rtt < 0.0, "contradictory timestamps");
    let offset = state.effective_offset(now).expect("sync offset");
    assert!((offset - receiver_offset as f64).abs() < 200_000.0);
    assert!(state.ready(start + Duration::from_millis(250)));
}

#[test]
fn expired_delay_offset_tracks_fresh_syncs_and_recovers_without_stale_smoothing() {
    fn respond(state: &mut PeerState, seq: u16, local: u64, remote: u64, at: Instant) {
        state.delay = Some((seq, local + 200_000));
        state.delay_age = Some(at);
        let mut response = header(9, 54, 8, seq, 0x0408, 0);
        timestamp(&mut response[34..44], remote);
        response[44..52].copy_from_slice(&7u64.to_be_bytes());
        response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
        followed_packet(state, &response, local + 400_000, at, 7);
    }

    let start = Instant::now();
    let mut state = follower(start);
    let initial_offset = 5_000_000_000u64;
    // Simulate one minute of 100 ppm clock drift, with ongoing Sync pairs but
    // only the first delay response usable. Rejected responses cannot renew it.
    for seq in 0..483u16 {
        let at = start + Duration::from_millis(u64::from(seq) * 125);
        let local = 1_000_000_000 + u64::from(seq) * 125_000_000;
        let drift = u64::from(seq) * 12_500;
        let (sync, follow) = sync_pair(8, seq, local + initial_offset + drift);
        followed_packet(&mut state, &sync, local + 90_000, at, 7);
        followed_packet(&mut state, &follow, local + 95_000, at, 7);
        if seq % 8 == 0 {
            let remote = if seq == 0 {
                local + initial_offset + 290_000
            } else {
                17
            };
            respond(&mut state, seq, local, remote, at);
        }
        if seq < 16 {
            assert_eq!(state.offset_source(at), Some("delay"));
            assert_eq!(state.effective_offset(at), Some(initial_offset as f64));
        } else {
            assert_eq!(state.offset_source(at), Some("sync"));
            assert_eq!(state.effective_offset(at), state.sync_offset);
        }
    }
    let now = start + Duration::from_millis(482 * 125);
    let current_offset = initial_offset as f64 + 482.0 * 12_500.0;
    assert!(state.ready(now));
    assert_eq!(state.packets.last_rejection, Some("round_trip"));
    assert!((state.effective_offset(now).unwrap() - current_offset).abs() < 200_000.0);

    // Once delay responses recover, the old measurement must not bias the new
    // estimate or cause a false offset jump that clears the healthy Sync streak.
    let seq = 483;
    let at = start + Duration::from_millis(u64::from(seq) * 125);
    let local = 1_000_000_000 + u64::from(seq) * 125_000_000;
    let recovered_offset = initial_offset + u64::from(seq) * 12_500;
    let (sync, follow) = sync_pair(8, seq, local + recovered_offset);
    followed_packet(&mut state, &sync, local + 90_000, at, 7);
    followed_packet(&mut state, &follow, local + 95_000, at, 7);
    respond(
        &mut state,
        seq,
        local,
        local + recovered_offset + 290_000,
        at,
    );
    assert_eq!(state.offset_source(at), Some("delay"));
    assert_eq!(state.effective_offset(at), Some(recovered_offset as f64));
    assert_eq!(state.jitter_ns, None);
    assert!(state.ready(at));
}

#[test]
fn delayed_sync_after_its_follow_up_cannot_rewind_a_newer_pair() {
    for (older, newer) in [(1, 2), (u16::MAX, 0)] {
        let start = Instant::now();
        let mut state = follower(start);
        let offset = 5_000_000_000;
        let (old_sync, old_follow) = sync_pair(8, older, 1_000_000_000 + offset);
        followed_packet(&mut state, &old_follow, 1_000_090_000, start, 7);
        let (sync, follow) = sync_pair(8, newer, 1_125_000_000 + offset);
        let at = start + Duration::from_millis(125);
        followed_packet(&mut state, &sync, 1_125_090_000, at, 7);
        followed_packet(&mut state, &follow, 1_125_095_000, at, 7);
        let offset_before = state.sync_offset;
        followed_packet(
            &mut state,
            &old_sync,
            1_250_090_000,
            start + Duration::from_millis(250),
            7,
        );
        assert_eq!(state.sync_pair.unwrap().sequence, newer);
        assert_eq!(state.sync_offset, offset_before);
        assert_eq!(state.last, Some(at));
        assert_eq!(state.exchanges, 1);
    }
}

#[test]
fn sync_sequence_wrap_and_restart_after_timeout_can_reach_readiness() {
    let start = Instant::now();
    let mut state = follower(start);
    for (seq, millis) in [
        (u16::MAX, 0),
        (0, 125),
        (1, 250),
        (0, 4000),
        (1, 4125),
        (2, 4250),
    ] {
        let at = start + Duration::from_millis(millis);
        let local = 1_000_000_000 + millis * 1_000_000;
        let (sync, follow) = sync_pair(8, seq, local + 1_000_000);
        followed_packet(&mut state, &sync, local + 90_000, at, 7);
        followed_packet(&mut state, &follow, local + 95_000, at, 7);
        if millis == 250 || millis == 4250 {
            assert_eq!(state.exchanges, 3);
            assert!(state.ready(at));
        } else if millis == 4000 {
            assert_eq!(state.exchanges, 1);
            assert!(!state.ready(at));
        }
    }
}

#[test]
fn older_exchange_instants_cannot_refresh_or_inflate_readiness() {
    let start = Instant::now();
    let mut state = PeerState::default();
    state.exchanged(start);
    state.exchanged(start + Duration::from_millis(125));
    state.exchanged(start + Duration::from_millis(50));
    assert_eq!(state.exchanges, 2);
    assert_eq!(state.last, Some(start + Duration::from_millis(125)));
    assert!(!state.ready(start + Duration::from_millis(250)));
    state.exchanged(start + Duration::from_millis(250));
    assert!(state.ready(start + Duration::from_millis(250)));
}

/// A followed peer as `PtpMaster::activate` leaves it, plus the receiver's
/// first Announce (priority1 96, priority2 128, clock 8).
fn introduced(now: Instant) -> PeerState {
    let mut state = PeerState {
        follow: true,
        ..Default::default()
    };
    state.begin_session("127.0.0.1".parse().unwrap(), now);
    followed_packet(&mut state, &announce(8, 0, 0), 0, now, 7);
    state
}
fn announces(state: &mut PeerState, count: u16, now: Instant) {
    for seq in 0..count {
        followed_packet(state, &announce(8, 100 + seq, 0), 0, now, 7);
    }
}
/// Step through the four introduction datagram groups starting at `from`.
fn finish_introduction(state: &mut PeerState, from: Instant) {
    for step in 0..4u8 {
        let at = from + INTRODUCTION_STEP * u32::from(step);
        assert_eq!(nudges(state, at), [Nudge::Introduction(step, 250)]);
    }
}
/// Drain everything that is due at `now`.
fn nudges(state: &mut PeerState, now: Instant) -> Vec<Nudge> {
    std::iter::from_fn(|| state.nudge(now)).collect()
}

#[test]
fn plain_announce_carries_the_requested_priorities() {
    let packet = plain_announce(7, 3, 247, 245);
    assert_eq!(packet.len(), 64);
    assert_eq!(packet_rejection(&packet), None);
    assert_eq!(&packet[2..4], &64u16.to_be_bytes());
    assert_eq!(&packet[20..28], &7u64.to_be_bytes());
    assert_eq!(&packet[30..32], &3u16.to_be_bytes());
    assert_eq!(
        parse_announce(&packet),
        Some(BmcaDataset {
            priority1: 247,
            clock_class: 248,
            accuracy: 0xfe,
            variance: 0x436a,
            priority2: 245,
            grandmaster: 7,
            steps_removed: 0,
        })
    );
}

#[test]
fn wake_priorities_step_around_the_receiver_and_stay_in_range() {
    let remote = |priority1| BmcaDataset {
        priority1,
        priority2: 245,
        ..BmcaDataset::local(8)
    };
    assert_eq!(wake_priorities(remote(248)), ((247, 245), (249, 245)));
    assert_eq!(wake_priorities(remote(2)), ((248, 248), (3, 245)));
    assert_eq!(wake_priorities(remote(255)), ((254, 245), (250, 245)));
}

#[test]
fn follower_signaling_is_three_well_formed_organization_tlvs() {
    let packet = follower_signaling(7, 9);
    assert_eq!(packet.len(), 122);
    assert_eq!(packet_rejection(&packet), None);
    assert_eq!(packet[0] & 15, 12);
    assert_eq!(&packet[34..44], &[0xff; 10]);
    let mut at = 44;
    let mut seen = Vec::new();
    while at < packet.len() {
        assert_eq!(&packet[at..at + 2], &[0, 3], "organization extension");
        let length = usize::from(u16::from_be_bytes([packet[at + 2], packet[at + 3]]));
        seen.push((packet[at + 4..at + 10].to_vec(), length));
        at += 4 + length;
    }
    assert_eq!(at, packet.len(), "TLVs fill the datagram exactly");
    assert_eq!(
        seen,
        vec![
            (vec![0, 0x80, 0xc2, 0, 0, 2], 12),
            (vec![0, 0x0d, 0x93, 0, 0, 1], 22),
            (vec![0, 0x0d, 0x93, 0, 0, 5], 32),
        ]
    );
    // Link delay -3, time sync -3 (8 per second), announce -2 (4 per second).
    assert_eq!(&packet[54..58], &[0xfd, 0xfd, 0xfe, 2]);
}

#[test]
fn a_silent_receiver_is_introduced_to_then_woken_within_a_fixed_budget() {
    let start = Instant::now();
    let mut state = introduced(start);
    // The introduction starts at once and takes four steps 125 ms apart.
    assert_eq!(nudges(&mut state, start), [Nudge::Introduction(0, 250)]);
    for step in 1..4u8 {
        let at = start + INTRODUCTION_STEP * u32::from(step);
        assert_eq!(nudges(&mut state, at), [Nudge::Introduction(step, 250)]);
    }
    announces(&mut state, 4, start);
    assert_eq!(nudges(&mut state, start + Duration::from_secs(1)), []);
    // Still silent: one better Announce, then two worse ones 150 ms apart.
    let mut at = start + INTRODUCTION_SETTLE;
    assert_eq!(nudges(&mut state, at), [Nudge::Announce(95, 128)]);
    assert_eq!(nudges(&mut state, at + Duration::from_millis(100)), []);
    assert_eq!(
        nudges(&mut state, at + WAKE_GAP),
        [Nudge::Announce(97, 128)]
    );
    assert_eq!(
        nudges(&mut state, at + WAKE_GAP * 2),
        [Nudge::Announce(97, 128)]
    );
    announces(&mut state, 4, at);
    assert_eq!(nudges(&mut state, at + Duration::from_secs(1)), []);
    // Then the introduction again, then a last wake.
    at += WAKE_RETRY;
    assert_eq!(nudges(&mut state, at), [Nudge::Introduction(0, 250)]);
    for step in 1..4u8 {
        let due = at + INTRODUCTION_STEP * u32::from(step);
        assert_eq!(nudges(&mut state, due), [Nudge::Introduction(step, 250)]);
    }
    announces(&mut state, 4, at);
    at += INTRODUCTION_SETTLE;
    assert_eq!(nudges(&mut state, at), [Nudge::Announce(95, 128)]);
    assert_eq!(nudges(&mut state, at + WAKE_GAP).len(), 1);
    assert_eq!(nudges(&mut state, at + WAKE_GAP * 2).len(), 1);
    // The budget is spent: a receiver that never syncs is left alone.
    announces(&mut state, 40, at);
    assert_eq!(nudges(&mut state, at + Duration::from_secs(60)), []);
    assert_eq!((state.wake.wakes, state.wake.introductions), (2, 2));
}

#[test]
fn a_settled_clock_is_never_nudged_again() {
    let start = Instant::now();
    let mut state = introduced(start);
    assert_eq!(nudges(&mut state, start).len(), 1);
    for seq in 0..3u16 {
        let at = start + Duration::from_millis(u64::from(seq) * 125);
        let local = 1_000_000_000 + u64::from(seq) * 125_000_000;
        let (sync, follow) = sync_pair(8, seq, local + 5_000_000_000);
        followed_packet(&mut state, &sync, local, at, 7);
        followed_packet(&mut state, &follow, local + 5_000, at, 7);
    }
    assert!(state.wake.settled);
    assert_eq!(
        state.wake.first_pair.map(|first| (first.1, first.2)),
        Some((0, 1))
    );
    // The rest of the introduction is dropped once the clock is usable.
    assert_eq!(nudges(&mut state, start + Duration::from_millis(400)), []);
    // A 650 ms Sync gap under 4 Hz Announces, while the clock is still ready.
    let gap = start + Duration::from_millis(900);
    announces(&mut state, 3, gap);
    assert!(state.ready(gap));
    assert_eq!(nudges(&mut state, gap), []);
    // Sync at 1 Hz against Announce at 4 Hz, for twenty seconds.
    for second in 2..22u16 {
        let at = start + Duration::from_secs(u64::from(second));
        announces(&mut state, 4, at);
        assert_eq!(nudges(&mut state, at), [], "second {second}");
        let local = 1_000_000_000 + u64::from(second) * 1_000_000_000;
        let (sync, follow) = sync_pair(8, 10 + second, local + 5_000_000_000);
        followed_packet(&mut state, &sync, local, at, 7);
        followed_packet(&mut state, &follow, local + 5_000, at, 7);
    }
    assert_eq!((state.wake.wakes, state.wake.introductions), (0, 1));
}

#[test]
fn sync_traffic_holds_back_a_wake_and_a_single_pair_restores_the_budget() {
    let start = Instant::now();
    let mut state = introduced(start);
    let after = start + INTRODUCTION_SETTLE;
    finish_introduction(&mut state, start);
    announces(&mut state, 3, after);
    let (sync, follow) = sync_pair(8, 0, 6_000_000_000);
    followed_packet(&mut state, &sync, 1_000_000_000, after, 7);
    assert_eq!(state.wake.announces_without_sync, 0);
    assert_eq!(
        nudges(&mut state, after),
        [],
        "a syncing clock is not woken"
    );
    state.wake.attempts = WAKE_ESCALATIONS;
    followed_packet(&mut state, &follow, 1_000_100_000, after, 7);
    assert_eq!(state.wake.attempts, 0);
    assert!(!state.wake.settled, "one pair is not yet a usable clock");
    // A sender-role peer and an inactive follower are never nudged.
    let mut sender = PeerState::default();
    sender.begin_session("127.0.0.1".parse().unwrap(), start);
    sender.bmca = Some(BmcaDataset::local(8));
    sender.wake.announces_without_sync = 9;
    assert_eq!(nudges(&mut sender, start + Duration::from_secs(9)), []);
    let mut inactive = introduced(start);
    inactive.active = false;
    assert_eq!(nudges(&mut inactive, start), []);
}

#[test]
fn an_announce_naming_this_engine_is_not_adopted_as_the_followed_clock() {
    let start = Instant::now();
    let mut state = introduced(start);
    let theirs = state.bmca;
    // The receiver briefly names clock 7 (this engine) as its grandmaster.
    let mut echo = announce(8, 5, 0);
    echo[53..61].copy_from_slice(&7u64.to_be_bytes());
    echo[47] = 95;
    followed_packet(&mut state, &echo, 0, start, 7);
    assert_eq!(state.clock_id, Some(8));
    assert_eq!(state.bmca, theirs);
    assert_eq!(state.wake.announces_naming_us, 1);
    assert_eq!(state.wake.announces_without_sync, 2);
    assert_eq!(state.packets.rejected, 0);
}

#[test]
fn wake_state_survives_a_receiver_restart_and_a_started_wake_survives_reactivation() {
    let start = Instant::now();
    let mut state = introduced(start);
    let at = start + INTRODUCTION_SETTLE;
    finish_introduction(&mut state, start);
    announces(&mut state, 3, at);
    assert_eq!(nudges(&mut state, at), [Nudge::Announce(95, 128)]);
    // The receiver restarts its PTP port under a new clock identity.
    followed_packet(&mut state, &announce(9, 0, 0), 0, at, 7);
    assert_eq!(state.clock_id, Some(9));
    assert_eq!((state.wake.wakes, state.wake.attempts), (1, 1));
    assert!(state.wake.closing.is_some());
    // A member setup retry activates the peer again; the wake is still closed.
    state.begin_session("127.0.0.1".parse().unwrap(), at);
    assert_eq!(state.wake.introductions, 2);
    let later = at + WAKE_GAP * 3;
    let sent = nudges(&mut state, later);
    assert_eq!(sent.first(), Some(&Nudge::Announce(97, 128)));
}

/// A receiver that announces at 4 Hz and starts syncing once `woken` says so.
fn run_silent_receiver(woken: impl Fn(&[Vec<u8>]) -> bool) -> (PeerSnapshot, Vec<Vec<u8>>) {
    let (peer, remote_event, remote_general) = crate::test_net::ptp_receiver_sockets();
    remote_event.set_nonblocking(true).unwrap();
    remote_general.set_nonblocking(true).unwrap();
    let event = UdpSocket::bind("127.0.0.1:0").unwrap();
    let general = UdpSocket::bind("127.0.0.1:0").unwrap();
    let local_event = event.local_addr().unwrap();
    let local_general = general.local_addr().unwrap();
    let clock = Clock::new();
    let timer =
        PtpMaster::from_sockets(vec![peer], vec![peer], clock.clone(), event, general).unwrap();
    timer.activate(peer, "127.0.0.1".parse().unwrap());
    let started = Instant::now();
    let mut buffer = [0u8; 2048];
    let mut heard = Vec::new();
    let mut seq = 0u16;
    let mut next_packet = started;
    while started.elapsed() < Duration::from_secs(12) && !timer.ready(peer) {
        for socket in [&remote_general, &remote_event] {
            while let Ok((n, _)) = socket.recv_from(&mut buffer) {
                // Subscription requests and Delay_Req are not part of a nudge.
                let subscription = buffer[0] & 15 == 12 && n == 54;
                if !subscription && buffer[0] & 15 != 1 {
                    heard.push(buffer[..n].to_vec());
                }
            }
        }
        if Instant::now() >= next_packet {
            next_packet += Duration::from_millis(250);
            remote_general
                .send_to(&announce(8, seq, 0), local_general)
                .unwrap();
            if woken(&heard) {
                let (sync, follow) = sync_pair(8, seq, clock.now_ns() + 5_000_000_000);
                remote_event.send_to(&sync, local_event).unwrap();
                remote_general.send_to(&follow, local_general).unwrap();
            }
            seq += 1;
        }
        thread::sleep(Duration::from_millis(5));
    }
    assert!(timer.ready(peer), "the followed clock became ready");
    (timer.snapshots().remove(0), heard)
}

#[test]
fn loop_introduces_itself_and_follows_a_receiver_that_then_syncs() {
    // This receiver starts syncing as soon as it has the follower Signaling.
    let (snapshot, heard) =
        run_silent_receiver(|heard| heard.iter().any(|p| p[0] & 15 == 12 && p.len() == 122));
    let kinds: Vec<u8> = heard.iter().take(9).map(|p| p[0] & 15).collect();
    // Follow_Up and Announce share a socket with Sync on another, so compare counts.
    assert_eq!(kinds.iter().filter(|kind| **kind == 0).count(), 3);
    assert_eq!(kinds.iter().filter(|kind| **kind == 8).count(), 3);
    assert_eq!(kinds.iter().filter(|kind| **kind == 11).count(), 2);
    assert_eq!(kinds.iter().filter(|kind| **kind == 12).count(), 1);
    let announced = heard.iter().find(|p| p[0] & 15 == 11).unwrap();
    assert_eq!((announced.len(), announced[47]), (64, 250));
    assert_eq!((snapshot.wake_attempts, snapshot.introductions), (0, 1));
    assert_eq!(snapshot.first_pair_after_wakes, Some(0));
    assert_eq!(snapshot.offset_source, Some("sync"));
    let report = serde_json::to_value(&snapshot).unwrap();
    let first = report["packets"]["first_received"]["11"].as_str().unwrap();
    assert_eq!(first.len(), 76 * 2, "the first Announce is kept as hex");
    assert!(first.starts_with("1b02004c"));
    assert!(report["packets"]["last_received"]["8"].is_string());
    assert!(report["first_pair_ms"].is_u64());
}

#[test]
fn loop_wakes_a_receiver_that_ignores_the_introduction() {
    // This receiver syncs only after a better Announce followed by a worse one.
    let (snapshot, heard) = run_silent_receiver(|heard| {
        let priorities: Vec<u8> = heard
            .iter()
            .filter(|p| p[0] & 15 == 11)
            .map(|p| p[47])
            .collect();
        priorities.windows(2).any(|pair| pair == [95, 97])
    });
    let priorities: Vec<u8> = heard
        .iter()
        .filter(|p| p[0] & 15 == 11)
        .map(|p| p[47])
        .collect();
    assert_eq!(&priorities[..4], &[250, 250, 95, 97]);
    assert_eq!((snapshot.wake_attempts, snapshot.introductions), (1, 1));
    assert_eq!(snapshot.first_pair_after_wakes, Some(1));
    assert!(snapshot.packets.sent_by_type[11] >= 4);
}
