//! Now Playing metadata for the receiver.
//!
//! A HomePod shows the title, artist and elapsed time from `SET_PARAMETER` on the
//! control connection. Apple senders use three payloads, and it is not documented
//! which one a given firmware prefers, so this module sends the two that carry the
//! information the display uses:
//!
//! - `text/parameters` with `progress: <start>/<current>/<end>` in
//!   wrapping RTP sample timestamps, using the same clock as the audio stream;
//! - `application/x-dmap-tagged` with a `mlit` item holding `minm` (title),
//!   `asar` (artist), `asal` (album) and `asgn` (genre).
//!
//! Artwork goes out as a third `SET_PARAMETER` with `image/jpeg`, once per image.
//! A HomePod takes its Sound Glow colours from the artwork of what is playing.
//! The desktop hands the JPEG over as a file path, because an image does not fit
//! in an IPC line. A receiver that rejects the request gets no more artwork for
//! the session, so a refusal never costs more than one request.

use crate::rtsp::{Cancellation, Connection};
use anyhow::{Result, ensure};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    sync::{Arc, Mutex, OnceLock, mpsc},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

/// Track information as the desktop side knows it.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
pub struct NowPlaying {
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub artist: String,
    #[serde(default)]
    pub album: String,
    #[serde(default)]
    pub genre: String,
    #[serde(default)]
    pub duration_ms: u64,
    #[serde(default)]
    pub position_ms: u64,
    /// Paused tracks still show their tags; the progress bar is what stops.
    #[serde(default = "playing_default")]
    pub playing: bool,
    /// A JPEG file with the track's artwork, or empty.
    #[serde(default)]
    pub artwork_path: String,
    /// The bytes of that file, loaded by [`NowPlaying::load_artwork`].
    #[serde(skip)]
    pub artwork: Option<Arc<Vec<u8>>>,
}
/// Larger than any thumbnail the desktop produces; a guard, not a target.
pub const ARTWORK_LIMIT: u64 = 1024 * 1024;
fn playing_default() -> bool {
    true
}
impl NowPlaying {
    /// Read `artwork_path`. Anything that is not a JPEG of a sane size is left
    /// out and reported; the tags still go out without it.
    pub fn load_artwork(&mut self) -> Result<()> {
        self.artwork = None;
        if self.artwork_path.is_empty() {
            return Ok(());
        }
        let size = std::fs::metadata(&self.artwork_path)?.len();
        ensure!(size <= ARTWORK_LIMIT, "artwork is too large ({size} bytes)");
        let bytes = std::fs::read(&self.artwork_path)?;
        ensure!(is_jpeg(&bytes), "artwork is not a JPEG image");
        self.artwork = Some(Arc::new(bytes));
        Ok(())
    }
    /// Nothing to show: the receiver keeps its previous display, which is what
    /// Apple senders do when the source goes away.
    pub fn is_empty(&self) -> bool {
        self.title.is_empty() && self.artist.is_empty() && self.album.is_empty()
    }
    /// All three values use the current stream's wrapping 32-bit RTP sample clock.
    pub fn progress_body(&self, current: u32, rate: u32, elapsed_ms: u64) -> String {
        let position = self
            .position_ms
            .saturating_add(if self.playing { elapsed_ms } else { 0 });
        let ticks =
            |ms: u64| ((u128::from(ms) * u128::from(rate) / 1000) & u128::from(u32::MAX)) as u32;
        let start = current.wrapping_sub(ticks(position));
        let end = if self.duration_ms == 0 {
            current
        } else {
            start.wrapping_add(ticks(self.duration_ms))
        };
        format!("progress: {start}/{current}/{end}\r\n")
    }
}

/// Start-of-image and end-of-image markers of a complete JPEG.
pub fn is_jpeg(bytes: &[u8]) -> bool {
    bytes.len() > 4 && bytes.starts_with(&[0xff, 0xd8, 0xff]) && bytes.ends_with(&[0xff, 0xd9])
}

/// DMAP is a flat TLV format: four ASCII bytes of tag, a big-endian byte length,
/// then the payload. The outer `mlit` item wraps the text items.
pub fn dmap_tags(now: &NowPlaying) -> Vec<u8> {
    let mut inner = Vec::new();
    for (tag, value) in [
        (b"minm", now.title.as_str()),
        (b"asar", now.artist.as_str()),
        (b"asal", now.album.as_str()),
        (b"asgn", now.genre.as_str()),
    ] {
        if value.is_empty() {
            continue;
        }
        inner.extend_from_slice(tag);
        inner.extend_from_slice(&(value.len() as u32).to_be_bytes());
        inner.extend_from_slice(value.as_bytes());
    }
    if inner.is_empty() {
        return Vec::new();
    }
    let mut out = Vec::with_capacity(inner.len() + 8);
    out.extend_from_slice(b"mlit");
    out.extend_from_slice(&(inner.len() as u32).to_be_bytes());
    out.extend_from_slice(&inner);
    out
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Command {
    pub sequence: u64,
    pub sent_at: Instant,
}

#[derive(Clone, Default)]
pub struct Control(Arc<Mutex<Option<(u64, NowPlaying, Instant)>>>);
impl Control {
    pub fn set(&self, sequence: u64, now: NowPlaying) {
        let mut slot = self.0.lock().unwrap();
        if slot.as_ref().is_none_or(|(old, _, _)| sequence >= *old) {
            *slot = Some((sequence, now, Instant::now()));
        }
    }
    pub fn clear(&self) {
        *self.0.lock().unwrap() = None;
    }
    fn latest(&self) -> Option<(u64, NowPlaying, Instant)> {
        self.0.lock().unwrap().clone()
    }
}

/// Process-wide control, mirroring the volume and lead controls: the engine runs
/// one session at a time and every IPC command can reach it without threading
/// another parameter through the call stack.
pub fn global() -> &'static Control {
    static CONTROL: OnceLock<Control> = OnceLock::new();
    CONTROL.get_or_init(Control::default)
}

/// Progress changes faster than the display can use; Apple senders coalesce to
/// roughly one update every second and a half.
pub const PROGRESS_INTERVAL: Duration = Duration::from_millis(1500);

pub type Peer = (String, String, Arc<Mutex<Connection>>);

#[derive(Clone)]
pub struct Timeline {
    timestamp: Arc<std::sync::atomic::AtomicU32>,
    rate: u32,
}
impl Timeline {
    pub fn new(timestamp: u32, rate: u32) -> Self {
        Self {
            timestamp: Arc::new(std::sync::atomic::AtomicU32::new(timestamp)),
            rate,
        }
    }
    pub fn set(&self, timestamp: u32) {
        self.timestamp
            .store(timestamp, std::sync::atomic::Ordering::Release);
    }
    fn current(&self) -> u32 {
        self.timestamp.load(std::sync::atomic::Ordering::Acquire)
    }
}

pub struct Worker {
    stop: Cancellation,
    worker: Option<JoinHandle<()>>,
    pub events: mpsc::Receiver<Value>,
}
impl Worker {
    pub fn start(peers: Vec<Peer>, control: Control, timeline: Timeline) -> Result<Self> {
        ensure!(!peers.is_empty(), "metadata worker requires a receiver");
        let stop = Cancellation::default();
        let done = stop.clone();
        let (tx, events) = mpsc::sync_channel(16);
        let worker = thread::Builder::new()
            .name("airplay-metadata".into())
            .spawn(move || {
                let mut last: Option<(u64, NowPlaying, Instant)> = None;
                let mut tags_sent: Vec<Option<Vec<u8>>> = vec![None; peers.len()];
                let mut progress_enabled = vec![true; peers.len()];
                let mut tags_enabled = vec![true; peers.len()];
                let mut artwork_sent: Vec<Option<Arc<Vec<u8>>>> = vec![None; peers.len()];
                let mut artwork_enabled = vec![true; peers.len()];
                while !done.is_cancelled() {
                    let Some((sequence, now, sampled)) = control.latest() else {
                        thread::sleep(Duration::from_millis(100));
                        continue;
                    };
                    let fresh = last.as_ref().is_none_or(|(old, previous, at)| {
                        *old != sequence && (previous != &now || at.elapsed() >= PROGRESS_INTERVAL)
                    });
                    if !fresh {
                        thread::sleep(Duration::from_millis(50));
                        continue;
                    }
                    let mut errors = Vec::new();
                    let mut tags_updated = false;
                    let mut artwork_updated = false;
                    for (index, (host, uri, connection)) in peers.iter().enumerate() {
                        if done.is_cancelled() {
                            return;
                        }
                        let mut connection = connection.lock().unwrap();
                        let current = timeline.current();
                        let progress = now.progress_body(
                            current,
                            timeline.rate,
                            sampled.elapsed().as_millis() as u64,
                        );
                        let stamp = ("RTP-Info", format!("rtptime={current}"));
                        if progress_enabled[index] {
                            if let Err(error) = connection.request(
                                "SET_PARAMETER",
                                uri,
                                &[("Content-Type", "text/parameters".into()), stamp.clone()],
                                progress.as_bytes(),
                            ) {
                                errors.push(format!("{host}: progress: {error:#}"));
                                // A complete rejection is persistent for this session. An I/O
                                // failure must not monopolise the shared control connection.
                                progress_enabled[index] = false;
                            }
                        }
                        let tags = dmap_tags(&now);
                        if tags_enabled[index]
                            && !tags.is_empty()
                            && tags_sent[index].as_ref() != Some(&tags)
                        {
                            if let Err(error) = connection.request(
                                "SET_PARAMETER",
                                uri,
                                &[
                                    ("Content-Type", "application/x-dmap-tagged".into()),
                                    stamp.clone(),
                                ],
                                &tags,
                            ) {
                                errors.push(format!("{host}: tags: {error:#}"));
                                tags_enabled[index] = false;
                            } else {
                                tags_sent[index] = Some(tags);
                                tags_updated = true;
                            }
                        }
                        if let Some(artwork) = now.artwork.as_ref().filter(|artwork| {
                            artwork_enabled[index] && artwork_sent[index].as_ref() != Some(*artwork)
                        }) {
                            if let Err(error) = connection.request(
                                "SET_PARAMETER",
                                uri,
                                &[("Content-Type", "image/jpeg".into()), stamp],
                                artwork,
                            ) {
                                errors.push(format!("{host}: artwork: {error:#}"));
                                artwork_enabled[index] = false;
                            } else {
                                artwork_sent[index] = Some(artwork.clone());
                                artwork_updated = true;
                            }
                        }
                    }

                    let _ = tx.try_send(json!({
                        "event": "metadata_sent",
                        "title": now.title,
                        "artist": now.artist,
                        "position_ms": now.position_ms,
                        "duration_ms": now.duration_ms,
                        "playing": now.playing,
                        "tags_updated": tags_updated,
                        "artwork_updated": artwork_updated,
                        "artwork_bytes": now.artwork.as_ref().map_or(0, |a| a.len()),
                        "message": errors.join("; "),
                    }));
                    last = Some((sequence, now, Instant::now()));
                }
            })?;
        Ok(Self {
            stop,
            worker: Some(worker),
            events,
        })
    }
}
impl Drop for Worker {
    fn drop(&mut self) {
        self.stop.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> NowPlaying {
        NowPlaying {
            title: "Title".into(),
            artist: "Artist".into(),
            album: "Album".into(),
            genre: String::new(),
            duration_ms: 240_000,
            position_ms: 42_000,
            playing: true,
            ..NowPlaying::default()
        }
    }

    #[test]
    fn artwork_is_loaded_only_from_a_complete_jpeg_of_a_sane_size() {
        let dir = std::env::temp_dir().join(format!("airflash-artwork-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let jpeg = [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 0xff, 0xd9];
        assert!(is_jpeg(&jpeg));
        assert!(!is_jpeg(&jpeg[..7]), "a truncated image is not sent");
        assert!(!is_jpeg(b"\x89PNG\r\n\x1a\n"));
        let path = |name: &str, bytes: &[u8]| {
            let path = dir.join(name);
            std::fs::write(&path, bytes).unwrap();
            path.to_string_lossy().into_owned()
        };
        let mut now = NowPlaying {
            title: "Song".into(),
            artwork_path: path("good.jpg", &jpeg),
            ..NowPlaying::default()
        };
        now.load_artwork().unwrap();
        assert_eq!(now.artwork.as_deref().map(Vec::as_slice), Some(&jpeg[..]));
        // No path is not an error, and clears what an earlier load left.
        now.artwork_path.clear();
        now.load_artwork().unwrap();
        assert!(now.artwork.is_none());
        for bad in [
            path("bad.jpg", b"not an image"),
            path("big.jpg", &vec![0xff; ARTWORK_LIMIT as usize + 1]),
            dir.join("missing.jpg").to_string_lossy().into_owned(),
        ] {
            now.artwork_path = bad;
            assert!(now.load_artwork().is_err());
            assert!(now.artwork.is_none());
        }
        // The file path is not part of what the desktop may omit.
        let parsed: NowPlaying = serde_json::from_str(r#"{"title":"Song"}"#).unwrap();
        assert!(parsed.artwork_path.is_empty() && parsed.artwork.is_none());
        std::fs::remove_dir_all(&dir).unwrap();
    }
    #[test]
    fn dmap_items_are_tagged_length_prefixed_and_nested_in_mlit() {
        let bytes = dmap_tags(&sample());
        assert_eq!(&bytes[..4], b"mlit");
        let inner_len = u32::from_be_bytes(bytes[4..8].try_into().unwrap()) as usize;
        assert_eq!(inner_len, bytes.len() - 8);
        let inner = &bytes[8..];
        // Three non-empty tags, in a fixed order, each with its own length.
        let mut offset = 0;
        for (tag, value) in [(b"minm", "Title"), (b"asar", "Artist"), (b"asal", "Album")] {
            assert_eq!(&inner[offset..offset + 4], tag);
            let len =
                u32::from_be_bytes(inner[offset + 4..offset + 8].try_into().unwrap()) as usize;
            assert_eq!(&inner[offset + 8..offset + 8 + len], value.as_bytes());
            offset += 8 + len;
        }
        assert_eq!(offset, inner.len(), "no trailing bytes");
    }

    #[test]
    fn empty_tracks_produce_no_dmap_item() {
        assert!(dmap_tags(&NowPlaying::default()).is_empty());
        assert!(NowPlaying::default().is_empty());
        assert!(!sample().is_empty());
    }

    #[test]
    fn progress_line_uses_wrapping_rtp_samples_and_crlf() {
        let body = sample().progress_body(1_852_200, 44100, 0);
        assert_eq!(body, "progress: 0/1852200/10584000\r\n");
    }

    #[test]
    fn progress_wraps_at_the_selected_sample_rate_and_pause_stops_elapsed_time() {
        let mut now = sample();
        now.position_ms = 1_000;
        now.duration_ms = 2_000;
        let start = 10u32.wrapping_sub(72_000);
        let end = start.wrapping_add(96_000);
        assert_eq!(
            now.progress_body(10, 48_000, 500),
            format!("progress: {start}/10/{end}\r\n")
        );
        now.playing = false;
        let start = 10u32.wrapping_sub(48_000);
        let end = start.wrapping_add(96_000);
        assert_eq!(
            now.progress_body(10, 48_000, 500),
            format!("progress: {start}/10/{end}\r\n")
        );
    }

    #[test]
    fn control_keeps_the_newest_sequence() {
        let control = Control::default();
        control.set(2, sample());
        control.set(1, NowPlaying::default());
        assert_eq!(control.latest().unwrap().0, 2);
        assert_eq!(control.latest().unwrap().1.title, "Title");
        control.set(3, NowPlaying::default());
        assert_eq!(control.latest().unwrap().0, 3);
    }

    #[test]
    fn worker_sends_progress_then_tags_and_only_repeats_progress() {
        use std::io::{Read, Write};
        use std::net::TcpListener;
        use std::sync::mpsc;

        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let (requests, received) = mpsc::channel();
        let server = std::thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            socket
                .set_read_timeout(Some(Duration::from_millis(500)))
                .unwrap();
            let mut pending = Vec::new();
            let mut buffer = [0; 4096];
            loop {
                let read = socket.read(&mut buffer).unwrap_or(0);
                if read == 0 {
                    break;
                }
                pending.extend_from_slice(&buffer[..read]);
                // Every request in this test has an empty or self-contained body, so
                // a complete read is one shot of two requests back to back.
                while let Some(end) = find_request_end(&pending) {
                    let request = String::from_utf8_lossy(&pending[..end]).to_string();
                    let body_len = request
                        .lines()
                        .find_map(|line| {
                            line.to_ascii_lowercase()
                                .strip_prefix("content-length:")
                                .map(|value| value.trim().parse::<usize>().unwrap_or(0))
                        })
                        .unwrap_or(0);
                    let total = end + body_len;
                    if pending.len() < total {
                        break;
                    }
                    let body = pending[end..total].to_vec();
                    pending.drain(..total);
                    let cseq = request.lines().find_map(|line| {
                        line.to_ascii_lowercase()
                            .strip_prefix("cseq:")
                            .map(|v| v.trim().to_string())
                    });
                    if let Some(cseq) = cseq {
                        let _ = socket.write_all(
                            format!("RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Length: 0\r\n\r\n")
                                .as_bytes(),
                        );
                    }
                    if requests.send((request, body)).is_err() {
                        return;
                    }
                }
            }
        });
        let control = Control::default();
        control.set(1, sample());
        let connection = Connection::connect(addr, Cancellation::default()).unwrap();
        let peers = vec![(
            "127.0.0.1".to_string(),
            "/stream".to_string(),
            Arc::new(Mutex::new(connection)),
        )];
        let worker = Worker::start(
            peers,
            control.clone(),
            Timeline::new(42_000 * 441 / 10, 44100),
        )
        .unwrap();
        let first = received
            .recv_timeout(Duration::from_secs(2))
            .expect("progress payload");
        assert!(first.0.starts_with("SET_PARAMETER /stream RTSP/1.0"));
        assert!(
            first
                .0
                .to_ascii_lowercase()
                .contains("content-type: text/parameters")
        );
        assert!(
            String::from_utf8_lossy(&first.1).starts_with("progress: "),
            "{:?}",
            String::from_utf8_lossy(&first.1)
        );
        let second = received
            .recv_timeout(Duration::from_secs(2))
            .expect("tags payload");
        assert!(
            second
                .0
                .to_ascii_lowercase()
                .contains("content-type: application/x-dmap-tagged")
        );
        assert_eq!(&second.1[..4], b"mlit");
        // Same track, new position: progress is re-sent, tags are not.
        let mut moved = sample();
        moved.position_ms += 2_000;
        control.set(2, moved.clone());
        let third = received
            .recv_timeout(Duration::from_secs(3))
            .expect("progress after a position change");
        assert!(String::from_utf8_lossy(&third.1).starts_with("progress: "));
        drop(worker);
        drop(received);
        let _ = server.join();
    }

    /// Index just past the blank line that ends the RTSP header block.
    fn find_request_end(bytes: &[u8]) -> Option<usize> {
        bytes
            .windows(4)
            .position(|window| window == b"\r\n\r\n")
            .map(|at| at + 4)
    }
}
