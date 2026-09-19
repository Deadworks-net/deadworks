//! A tiny loopback HTTP server that lets the in-game Panorama addon drive the
//! launcher.
//!
//! Panorama has no fetch/XHR/websocket: the ONLY inbound data channel an addon
//! has is the intrinsic pixel size of an `<Image>` it loads. So every response
//! here is a PNG whose (width, height) encode two 6-bit values, using exactly
//! the encoding deadworks-api already speaks:
//!
//! ```text
//! dim = UNIT_BASE(15) + unit * UNIT_STEP(9)        unit 0..63 → dim 15..582
//! ```
//!
//! The generous spacing is what makes the read survive a non-integer UI scale:
//! the client calibrates against a fixed 600x800 probe first, and a ±1-2px
//! engine rounding error can then never flip a value. Outbound data (which
//! server to prepare) rides in the query string, which costs nothing.
//!
//! The addon asks us to download a server's custom content, watches the job
//! progress, and then issues `connect` itself from inside the game. We never
//! raise a window and never launch or connect the game — that is what makes an
//! in-game join silent.

use std::io::Write as _;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

use tauri::{AppHandle, Listener};

use crate::addons::{self, DownloadProgress};

/// Ports tried in order; the client probes the whole range in parallel, so any
/// one of them works and a busy port is never fatal.
const PORT_BASE: u16 = 47800;
const PORT_SPAN: u16 = 4;

/// Bumped when the wire format below changes. The addon refuses a launcher that
/// does not match rather than misreading it.
const PROTO_VERSION: u8 = 1;
/// Guards against some unrelated local service answering on our port.
const HELLO_MAGIC: u8 = 42;

/// Private event channel the install path emits on. Distinct from the launcher
/// UI's `download-progress` so the React side never sees in-game traffic.
const PROGRESS_EVENT: &str = "dwl-bridge-progress";

const UNIT_BASE: u32 = 15;
const UNIT_STEP: u32 = 9;
const PROBE_W: u32 = 600;
const PROBE_H: u32 = 800;

// ── job state ───────────────────────────────────────────────────────────────

pub mod state {
    pub const IDLE: u8 = 0;
    pub const QUEUED: u8 = 1;
    pub const RESOLVING: u8 = 2;
    pub const DOWNLOADING: u8 = 3;
    pub const INSTALLING: u8 = 4;
    pub const READY: u8 = 6;
    pub const ERROR: u8 = 7;
    pub const CANCELLED: u8 = 8;
}

#[derive(Clone, Copy, Default)]
struct Snapshot {
    state: u8,
    pct63: u8,
    files_done: u8,
    files_total: u8,
    total_mib: u16,
    err: u8,
}

#[derive(Default)]
struct Plan {
    /// Prefix sums of `sizes`, so `prefix[i]` is "bytes done before item i".
    prefix: Vec<u64>,
    sizes: Vec<u64>,
    total: u64,
    files_total: u8,
}

struct Job {
    seq: u8,
    snap: Snapshot,
    cancel: Arc<AtomicBool>,
    plan: Plan,
}

fn job() -> &'static Mutex<Job> {
    static JOB: OnceLock<Mutex<Job>> = OnceLock::new();
    JOB.get_or_init(|| {
        Mutex::new(Job {
            seq: 0,
            snap: Snapshot::default(),
            cancel: Arc::new(AtomicBool::new(false)),
            plan: Plan::default(),
        })
    })
}

/// Publish a change, but only while `seq` is still the current job — a
/// superseded job must never overwrite its replacement's progress.
fn publish(seq: u8, f: impl FnOnce(&mut Snapshot)) {
    let mut j = job().lock().unwrap();
    if j.seq != seq {
        return;
    }
    f(&mut j.snap);
}

fn pct63(done: u64, total: u64) -> u8 {
    if total == 0 {
        return 0;
    }
    ((done.min(total) as u128 * 63) / total as u128) as u8
}

/// Record the per-item download plan. Items already installed at the right
/// version have size 0 and are excluded from both the byte total and the file
/// count, so the bar reflects work actually remaining.
fn build_plan(sizes: &[u64]) -> Plan {
    let mut prefix = Vec::with_capacity(sizes.len() + 1);
    let mut acc = 0u64;
    for s in sizes {
        prefix.push(acc);
        acc = acc.saturating_add(*s);
    }
    prefix.push(acc);
    let files_total = sizes.iter().filter(|s| **s > 0).count().min(63) as u8;
    Plan { prefix, sizes: sizes.to_vec(), total: acc, files_total }
}

fn set_plan(seq: u8, sizes: &[u64]) {
    let plan = build_plan(sizes);
    let files_total = plan.files_total;
    let total_mib = (plan.total / (1024 * 1024)).min(4095) as u16;

    let mut j = job().lock().unwrap();
    if j.seq != seq {
        return;
    }
    j.plan = plan;
    j.snap.files_total = files_total;
    j.snap.total_mib = total_mib;
}

/// Fold one progress event into the single percentage the game draws.
fn fold_progress(seq: u8, p: &DownloadProgress) {
    let mut j = job().lock().unwrap();
    if j.seq != seq || j.plan.prefix.is_empty() {
        return;
    }
    let idx = p.item_index.min(j.plan.sizes.len().saturating_sub(1));
    let before = j.plan.prefix[idx];
    let size = j.plan.sizes.get(idx).copied().unwrap_or(0);

    // Only the download phase carries real byte progress. Decompression reports
    // against a fake "compressed * 3" hint, so it is pinned at the item's end
    // rather than allowed to run the bar backwards.
    let (done, st) = match p.status.as_str() {
        "downloading" => (before + p.bytes_downloaded.min(size), state::DOWNLOADING),
        "decompressing" | "ready" => (before + size, state::INSTALLING),
        "checking" => (before, state::DOWNLOADING),
        _ => (before, state::RESOLVING),
    };
    let files_done = j
        .plan
        .sizes
        .iter()
        .take(if p.status == "ready" { idx + 1 } else { idx })
        .filter(|s| **s > 0)
        .count()
        .min(63) as u8;
    let total = j.plan.total;

    j.snap.state = st;
    j.snap.pct63 = pct63(done, total);
    j.snap.files_done = files_done;
}

// ── ping batches ────────────────────────────────────────────────────────────
// Panorama cannot open a socket, so it cannot measure a round trip. The API
// ships a coarse country-to-country estimate as a fallback, but the launcher is
// a native process on the player's own machine — it can just measure the thing.
// The game hands us a list of addresses and polls the results back.

/// 12 bits per result (two 6-bit units), so values are bounded at 4095.
const PING_FAILED: u16 = 4095;
const PING_PENDING: u16 = 0;
/// Bounds the work one request can ask for.
const PING_MAX_ADDRS: usize = 64;

struct PingBatch {
    seq: u8,
    results: Vec<u16>,
}

fn pings() -> &'static Mutex<PingBatch> {
    static P: OnceLock<Mutex<PingBatch>> = OnceLock::new();
    P.get_or_init(|| Mutex::new(PingBatch { seq: 0, results: Vec::new() }))
}

/// Start measuring `addrs`, superseding any batch in flight. Returns the new
/// sequence number, which the client quotes when polling results.
fn start_ping_batch(addrs: Vec<String>) -> u8 {
    let seq = {
        let mut b = pings().lock().unwrap();
        b.seq = b.seq.wrapping_add(1) & 63;
        b.results = vec![PING_PENDING; addrs.len()];
        b.seq
    };
    for (i, addr) in addrs.into_iter().enumerate() {
        tauri::async_runtime::spawn(async move {
            let ms = crate::ping::ping_server(addr).await;
            let v = if ms < 0 {
                PING_FAILED
            } else {
                // 0 is the "pending" sentinel and a loopback server really can
                // answer in under a millisecond, so the floor is 1.
                (ms as u32).clamp(1, (PING_FAILED - 1) as u32) as u16
            };
            let mut b = pings().lock().unwrap();
            if b.seq == seq {
                if let Some(slot) = b.results.get_mut(i) {
                    *slot = v;
                }
            }
        });
    }
    seq
}

fn ping_result(seq: Option<u8>, idx: usize) -> u16 {
    let b = pings().lock().unwrap();
    if seq != Some(b.seq) {
        return PING_PENDING;
    }
    b.results.get(idx).copied().unwrap_or(PING_PENDING)
}

/// Comma-separated `ip:port` list from the query string. Anything malformed is
/// dropped rather than failing the batch, so one bad row cannot cost the rest.
fn parse_addr_list(url: &str) -> Vec<String> {
    let raw = match query(url, "a") {
        Some(v) => v,
        None => return Vec::new(),
    };
    raw.split(',')
        .filter_map(|pair| {
            let (ip, port) = pair.split_once(':')?;
            let port: u16 = port.parse().ok()?;
            if port == 0 || !valid_ipv4(ip) {
                return None;
            }
            Some(format!("{}:{}", ip, port))
        })
        .take(PING_MAX_ADDRS)
        .collect()
}

// ── server list relay ───────────────────────────────────────────────────────
// The game can only receive data as image dimensions, so fetching the list from
// the API costs it ~100 round trips across the internet. We are a native
// process that can pull the whole payload in ONE ordinary HTTP request, then
// re-serve it over loopback on the same paths — same protocol, same bytes,
// local round trips.
//
// We never parse the payload. The wire format stays defined in exactly one
// place (deadworks-api/src/utils/browse.ts); this is a byte pipe that knows
// only how to slice bytes into 6-bit units.

/// How long a cached list is served before it is re-pulled from the API.
const LIST_STALE_SECS: u64 = 15;
/// Dimension the API uses to signal "something went wrong, re-head". It decodes
/// to a negative unit, which the client already treats as a hard error.
const SENTINEL_DIM: u32 = 2;

#[derive(Default)]
struct ListCache {
    version: u8,
    payload: Vec<u8>,
    fetched: Option<std::time::Instant>,
}

fn list_cache() -> &'static Mutex<ListCache> {
    static C: OnceLock<Mutex<ListCache>> = OnceLock::new();
    C.get_or_init(|| Mutex::new(ListCache::default()))
}

/// Pull a fresh payload if ours has aged out. Returns false only when we have
/// nothing to serve at all — a failed refresh with a stale copy in hand still
/// serves the stale copy, which beats failing the player's list outright.
fn refresh_list(app: &AppHandle) -> bool {
    refresh_list_from(&crate::addons::resolve_api_url(app))
}

fn refresh_list_from(api_url: &str) -> bool {
    let stale = {
        let c = list_cache().lock().unwrap();
        c.payload.is_empty()
            || c.fetched.is_none_or(|t| t.elapsed().as_secs() >= LIST_STALE_SECS)
    };
    if !stale {
        return true;
    }

    let url = format!("{}/api/browse/raw", api_url);
    let got = tauri::async_runtime::block_on(async move {
        let resp = reqwest::get(&url).await.ok()?;
        if !resp.status().is_success() {
            return None;
        }
        let version: u8 = resp.headers().get("x-dw-version")?.to_str().ok()?.parse().ok()?;
        let bytes = resp.bytes().await.ok()?;
        Some((version, bytes.to_vec()))
    });

    match got {
        Some((version, payload)) if !payload.is_empty() => {
            let n = payload.len();
            let mut c = list_cache().lock().unwrap();
            c.version = version;
            c.payload = payload;
            c.fetched = Some(std::time::Instant::now());
            println!("[local_api] list refreshed: {} bytes, version {}", n, version);
            true
        }
        _ => {
            println!("[local_api] list refresh failed; serving what we have");
            !list_cache().lock().unwrap().payload.is_empty()
        }
    }
}

/// One 6-bit unit, MSB-first. Mirrors `unitAt` in browse.ts exactly — this is
/// the one piece of the wire format that has to be reproduced here.
fn unit_at(bytes: &[u8], u: usize) -> u8 {
    let start = u * 6;
    let mut out = 0u8;
    for b in 0..6 {
        let bit = start + b;
        let byte = bytes.get(bit >> 3).copied().unwrap_or(0);
        out = (out << 1) | ((byte >> (7 - (bit & 7))) & 1);
    }
    out
}

/// ceil(ceil(len*8/6)/2), matching `imageCountFor`.
fn image_count_for(len: usize) -> usize {
    let units = (len * 8).div_ceil(6);
    units.div_ceil(2).max(1)
}

/// (version:8, imageCount:16) packed into four units, matching `headUnits`.
fn head_units(version: u8, image_count: u16) -> [u8; 4] {
    let v = ((version as u32) << 16) | image_count as u32;
    [
        ((v >> 18) & 0x3f) as u8,
        ((v >> 12) & 0x3f) as u8,
        ((v >> 6) & 0x3f) as u8,
        (v & 0x3f) as u8,
    ]
}

// ── PNG ─────────────────────────────────────────────────────────────────────

fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    out.extend_from_slice(kind);
    out.extend_from_slice(data);
    let mut h = crc32fast::Hasher::new();
    h.update(kind);
    h.update(data);
    out.extend_from_slice(&h.finalize().to_be_bytes());
}

/// Smallest PNG that lays out at exactly `w` x `h`: 1-bit greyscale, every
/// pixel black. Only the dimensions carry meaning, so the pixels never do.
fn png_1bit(w: u32, h: u32) -> Vec<u8> {
    let row = 1 + (w as usize).div_ceil(8); // filter byte + packed bits
    let raw = vec![0u8; row * h as usize];

    let mut z = flate2::write::ZlibEncoder::new(Vec::new(), flate2::Compression::default());
    let _ = z.write_all(&raw);
    let idat = z.finish().unwrap_or_default();

    let mut out = Vec::with_capacity(idat.len() + 64);
    out.extend_from_slice(&[0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a]);
    let mut ihdr = Vec::with_capacity(13);
    ihdr.extend_from_slice(&w.to_be_bytes());
    ihdr.extend_from_slice(&h.to_be_bytes());
    ihdr.extend_from_slice(&[1, 0, 0, 0, 0]); // bit depth 1, greyscale, no interlace
    chunk(&mut out, b"IHDR", &ihdr);
    chunk(&mut out, b"IDAT", &idat);
    chunk(&mut out, b"IEND", &[]);
    out
}

fn unit_dim(u: u8) -> u32 {
    UNIT_BASE + (u.min(63) as u32) * UNIT_STEP
}

/// One response image carrying two 6-bit values.
fn units_png(a: u8, b: u8) -> Vec<u8> {
    png_1bit(unit_dim(a), unit_dim(b))
}

// ── HTTP ────────────────────────────────────────────────────────────────────

fn query<'a>(url: &'a str, key: &str) -> Option<&'a str> {
    let q = url.split_once('?')?.1;
    q.split('&').find_map(|pair| {
        let (k, v) = pair.split_once('=')?;
        (k == key).then_some(v)
    })
}

fn path_of(url: &str) -> &str {
    url.split('?').next().unwrap_or(url)
}

/// Dotted-quad only: never a hostname, so nothing here can be turned into a
/// name lookup or a request to an arbitrary host.
fn valid_ipv4(ip: &str) -> bool {
    let octets: Vec<&str> = ip.split('.').collect();
    if octets.len() != 4 {
        return false;
    }
    octets.iter().all(|o| {
        !o.is_empty()
            && o.len() <= 3
            && o.bytes().all(|b| b.is_ascii_digit())
            && o.parse::<u16>().is_ok_and(|v| v <= 255)
    })
}

/// Only an `ip:port` we rebuild ourselves ever reaches the API, so a caller
/// cannot point the launcher at an arbitrary host.
fn parse_addr(url: &str) -> Option<String> {
    let ip = query(url, "ip")?;
    let port: u16 = query(url, "port")?.parse().ok()?;
    if port == 0 || !valid_ipv4(ip) {
        return None;
    }
    Some(format!("{}:{}", ip, port))
}

/// Reject anything that smells like a web page.
///
/// A browser can load `127.0.0.1` images from any site, so without this a random
/// page could make the launcher start downloads. Browsers attach `Sec-Fetch-*`
/// (and `Origin` on some requests) to every subresource load; Steam's HTTP
/// client, which is what Panorama uses, attaches neither.
fn from_browser(req: &tiny_http::Request) -> bool {
    req.headers().iter().any(|h| {
        let f = h.field.as_str().as_str();
        f.eq_ignore_ascii_case("origin") || f.to_ascii_lowercase().starts_with("sec-fetch-")
    })
}

fn respond(req: tiny_http::Request, png: Vec<u8>) {
    let mut resp = tiny_http::Response::from_data(png);
    for (k, v) in [
        ("Content-Type", "image/png"),
        // Every request reads live state; a cached image would freeze the bar.
        ("Cache-Control", "no-store, no-cache, must-revalidate"),
        ("Pragma", "no-cache"),
    ] {
        if let Ok(h) = tiny_http::Header::from_bytes(k.as_bytes(), v.as_bytes()) {
            resp.add_header(h);
        }
    }
    let _ = req.respond(resp);
}

fn handle(app: &AppHandle, req: tiny_http::Request) {
    if from_browser(&req) {
        let _ = req.respond(tiny_http::Response::empty(403));
        return;
    }
    let url = req.url().to_string();
    match path_of(&url) {
        "/dwl/probe.png" => respond(req, png_1bit(PROBE_W, PROBE_H)),
        "/dwl/hello.png" => respond(req, units_png(PROTO_VERSION, HELLO_MAGIC)),
        "/dwl/prep.png" => match parse_addr(&url) {
            Some(addr) => {
                let seq = start_job(app.clone(), addr);
                respond(req, units_png(1, seq));
            }
            None => respond(req, units_png(2, 0)),
        },
        "/dwl/status.png" => {
            let seq: Option<u8> = query(&url, "j").and_then(|s| s.parse().ok());
            let i: u8 = query(&url, "i").and_then(|s| s.parse().ok()).unwrap_or(0);
            let j = job().lock().unwrap();
            // A poll naming a job we are no longer running reads as idle, so a
            // late reply from a previous attempt can never be mistaken for
            // progress on the current one.
            let snap = if seq == Some(j.seq) { j.snap } else { Snapshot::default() };
            drop(j);
            let png = match i {
                0 => units_png(snap.state, snap.pct63),
                1 => units_png(
                    if snap.state == state::ERROR { snap.err } else { snap.files_done },
                    snap.files_total,
                ),
                _ => units_png((snap.total_mib & 63) as u8, (snap.total_mib >> 6) as u8),
            };
            respond(req, png);
        }
        "/dwl/cancel.png" => {
            let seq: Option<u8> = query(&url, "j").and_then(|s| s.parse().ok());
            let mut j = job().lock().unwrap();
            if seq == Some(j.seq) {
                j.cancel.store(true, Ordering::Relaxed);
                j.snap.state = state::CANCELLED;
            }
            drop(j);
            respond(req, units_png(1, 0));
        }
        "/dwl/ping.png" => {
            let addrs = parse_addr_list(&url);
            if addrs.is_empty() {
                respond(req, units_png(2, 0));
            } else {
                let n = addrs.len();
                let seq = start_ping_batch(addrs);
                println!("[local_api] pinging {} server(s), batch {}", n, seq);
                respond(req, units_png(1, seq));
            }
        }
        "/dwl/pingres.png" => {
            let seq: Option<u8> = query(&url, "b").and_then(|s| s.parse().ok());
            let idx: usize = query(&url, "i").and_then(|s| s.parse().ok()).unwrap_or(0);
            let ms = ping_result(seq, idx);
            respond(req, units_png((ms & 63) as u8, (ms >> 6) as u8));
        }

        // The API's own image-channel paths, served from our cached payload.
        // Identical semantics, so the game only has to swap the base URL.
        "/api/browse/probe.png" => respond(req, png_1bit(PROBE_W, PROBE_H)),
        "/api/browse/head.png" => {
            let half: usize = if query(&url, "h") == Some("1") { 1 } else { 0 };
            let have: i32 = query(&url, "have").and_then(|s| s.parse().ok()).unwrap_or(-1);
            // Only the FIRST head image may refresh: the payload must then hold
            // still for the data images that follow, or the client would read a
            // torn list.
            if half == 0 && !refresh_list(app) {
                respond(req, png_1bit(SENTINEL_DIM, SENTINEL_DIM));
                return;
            }
            let c = list_cache().lock().unwrap();
            if c.payload.is_empty() {
                drop(c);
                respond(req, png_1bit(SENTINEL_DIM, SENTINEL_DIM));
                return;
            }
            // "Nothing changed since your version" costs the client two images.
            let image_count = if have >= 0 && have as u8 == c.version {
                0
            } else {
                image_count_for(c.payload.len()).min(u16::MAX as usize) as u16
            };
            let u = head_units(c.version, image_count);
            drop(c);
            respond(req, units_png(u[half * 2], u[half * 2 + 1]));
        }
        "/api/browse/d.png" => {
            let idx: usize = query(&url, "i").and_then(|s| s.parse().ok()).unwrap_or(0);
            let want: i32 = query(&url, "v").and_then(|s| s.parse().ok()).unwrap_or(-1);
            let c = list_cache().lock().unwrap();
            // A version the client asked for that we no longer hold means the
            // list rolled over mid-fetch; the sentinel makes it re-head rather
            // than stitch two different lists together.
            if c.payload.is_empty() || (want >= 0 && want as u8 != c.version) {
                drop(c);
                respond(req, png_1bit(SENTINEL_DIM, SENTINEL_DIM));
                return;
            }
            let png = units_png(unit_at(&c.payload, idx * 2), unit_at(&c.payload, idx * 2 + 1));
            drop(c);
            respond(req, png);
        }
        _ => {
            let _ = req.respond(tiny_http::Response::empty(404));
        }
    }
}

// ── job driver ──────────────────────────────────────────────────────────────

/// Supersede any running job and start preparing `addr`. Returns the new
/// sequence number, which the client quotes on every later poll.
fn start_job(app: AppHandle, addr: String) -> u8 {
    let (seq, cancel) = {
        let mut j = job().lock().unwrap();
        // Tell the outgoing job to stop; it can no longer publish anyway,
        // because the sequence number has moved on.
        j.cancel.store(true, Ordering::Relaxed);
        j.seq = j.seq.wrapping_add(1) & 63;
        j.snap = Snapshot { state: state::QUEUED, ..Snapshot::default() };
        j.cancel = Arc::new(AtomicBool::new(false));
        j.plan = Plan::default();
        (j.seq, j.cancel.clone())
    };

    tauri::async_runtime::spawn(async move {
        publish(seq, |s| s.state = state::RESOLVING);

        // Byte-level progress comes from inside the shared download helper,
        // which reports by emitting. Listening on a private channel keeps that
        // helper untouched and keeps this traffic away from the launcher UI.
        let listener = app.listen(PROGRESS_EVENT, move |event| {
            if let Ok(p) = serde_json::from_str::<DownloadProgress>(event.payload()) {
                fold_progress(seq, &p);
            }
        });

        let result = addons::install_for_address(
            &app,
            &addr,
            PROGRESS_EVENT,
            cancel,
            &|sizes: &[u64]| set_plan(seq, sizes),
        )
        .await;
        app.unlisten(listener);

        match result {
            Ok(()) => publish(seq, |s| {
                s.state = state::READY;
                s.pct63 = 63;
                s.files_done = s.files_total;
            }),
            Err(msg) if msg == addons::CANCELLED_MSG => {
                publish(seq, |s| s.state = state::CANCELLED);
            }
            Err(msg) => {
                println!("[local_api] job {} failed: {}", seq, msg);
                let code = addons::error_code(&msg);
                publish(seq, |s| {
                    s.state = state::ERROR;
                    s.err = code;
                });
            }
        }
    });
    seq
}

// ── startup ─────────────────────────────────────────────────────────────────

/// Bind the first free port in the range and serve forever on a dedicated
/// thread. Failure is not fatal: without it the addon simply reports that the
/// launcher is unavailable and offers a plain connect.
pub fn start(app: AppHandle) {
    let mut server = None;
    for port in PORT_BASE..PORT_BASE + PORT_SPAN {
        if let Ok(s) = tiny_http::Server::http(("127.0.0.1", port)) {
            println!("[local_api] listening on 127.0.0.1:{}", port);
            server = Some(s);
            break;
        }
    }
    match server {
        Some(s) => spawn_server(app, s),
        None => println!(
            "[local_api] no free port in {}..{}; in-game bridge disabled",
            PORT_BASE,
            PORT_BASE + PORT_SPAN
        ),
    }
}

fn spawn_server(app: AppHandle, server: tiny_http::Server) {
    std::thread::Builder::new()
        .name("dw-local-api".into())
        .spawn(move || {
            for req in server.incoming_requests() {
                handle(&app, req);
            }
        })
        .ok();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn png_has_the_requested_dimensions() {
        let png = png_1bit(600, 800);
        assert_eq!(&png[..8], &[0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a]);
        // IHDR data starts at 8 (sig) + 4 (len) + 4 (type).
        assert_eq!(u32::from_be_bytes(png[16..20].try_into().unwrap()), 600);
        assert_eq!(u32::from_be_bytes(png[20..24].try_into().unwrap()), 800);
        assert_eq!(png[24], 1, "bit depth");
        assert_eq!(png[25], 0, "greyscale");
    }

    /// Walk the chunk list the way a decoder does: every length/CRC must agree,
    /// and the IDAT must inflate to exactly the scanline count PNG requires for
    /// the declared size. A merely plausible-looking PNG would make the engine
    /// skip the load, surfacing in-game as an unexplained timeout.
    #[test]
    fn png_is_structurally_valid() {
        for (w, h) in [(15u32, 15u32), (600, 800), (582, 582), (15, 582)] {
            let png = png_1bit(w, h);
            let mut pos = 8;
            let mut seen: Vec<String> = Vec::new();
            let mut idat = Vec::new();
            while pos < png.len() {
                let len = u32::from_be_bytes(png[pos..pos + 4].try_into().unwrap()) as usize;
                let kind = &png[pos + 4..pos + 8];
                let data = &png[pos + 8..pos + 8 + len];
                let crc =
                    u32::from_be_bytes(png[pos + 8 + len..pos + 12 + len].try_into().unwrap());
                let mut hasher = crc32fast::Hasher::new();
                hasher.update(kind);
                hasher.update(data);
                assert_eq!(hasher.finalize(), crc, "CRC mismatch in {:?}", kind);
                if kind == b"IDAT" {
                    idat.extend_from_slice(data);
                }
                seen.push(String::from_utf8_lossy(kind).into_owned());
                pos += 12 + len;
            }
            assert_eq!(pos, png.len(), "trailing bytes after IEND");
            assert_eq!(seen, vec!["IHDR", "IDAT", "IEND"], "chunk order for {}x{}", w, h);

            let mut raw = Vec::new();
            std::io::Read::read_to_end(
                &mut flate2::read::ZlibDecoder::new(std::io::Cursor::new(&idat)),
                &mut raw,
            )
            .expect("IDAT must be valid zlib");
            assert_eq!(raw.len(), (1 + (w as usize).div_ceil(8)) * h as usize);
        }
    }

    /// The whole channel rests on a third-party decoder (Source 2's) agreeing
    /// with us about the size of these images, so check that claim against an
    /// independent decoder rather than only against our own byte layout.
    #[test]
    fn a_real_decoder_reads_back_the_encoded_units() {
        for a in [0u8, 1, 31, 42, 63] {
            for b in [0u8, 5, 63] {
                let png = units_png(a, b);
                let img = image::load_from_memory_with_format(&png, image::ImageFormat::Png)
                    .unwrap_or_else(|e| panic!("decoder rejected units({},{}): {}", a, b, e));
                // Exactly the arithmetic the addon runs on actuallayoutwidth.
                let decode = |d: u32| (d - UNIT_BASE) / UNIT_STEP;
                assert_eq!((decode(img.width()), decode(img.height())), (a as u32, b as u32));
            }
        }
    }

    #[test]
    fn units_round_trip_through_dimensions() {
        for u in 0u8..64 {
            assert_eq!((unit_dim(u) - UNIT_BASE) / UNIT_STEP, u as u32);
        }
        assert_eq!(unit_dim(0), 15);
        assert_eq!(unit_dim(63), 582);
    }

    #[test]
    fn query_and_addr_parsing() {
        let u = "/dwl/prep.png?ip=10.0.0.7&port=27015&rnd=abc";
        assert_eq!(query(u, "port"), Some("27015"));
        assert_eq!(path_of(u), "/dwl/prep.png");
        assert_eq!(parse_addr(u).as_deref(), Some("10.0.0.7:27015"));
        assert_eq!(parse_addr("/x?ip=10.0.0&port=1"), None);
        assert_eq!(parse_addr("/x?ip=10.0.0.999&port=1"), None);
        assert_eq!(parse_addr("/x?ip=evil.example.com&port=1"), None);
        assert_eq!(parse_addr("/x?ip=1.2.3.4&port=0"), None);
    }

    /// These three must match browse.ts bit for bit or the client's integrity
    /// check rejects everything we serve. Vectors are from the TS definitions.
    #[test]
    fn unit_slicing_matches_the_api() {
        // 0b10110010_01110001 -> units 101100 100111 0001(00 pad)
        let b = [0b1011_0010u8, 0b0111_0001];
        assert_eq!(unit_at(&b, 0), 0b101100);
        assert_eq!(unit_at(&b, 1), 0b100111);
        assert_eq!(unit_at(&b, 2), 0b000100);
        // Reads past the end are zero-padded, exactly as the encoder assumes.
        assert_eq!(unit_at(&b, 9), 0);

        assert_eq!(image_count_for(0), 1, "never zero images");
        assert_eq!(image_count_for(1), 1); // 2 units -> 1 image
        assert_eq!(image_count_for(3), 2); // 4 units -> 2 images
        assert_eq!(image_count_for(157), 105, "matches the live payload");

        // (version, imageCount) packed MSB-first across four 6-bit units.
        let u = head_units(196, 105);
        let packed = ((u[0] as u32) << 18) | ((u[1] as u32) << 12) | ((u[2] as u32) << 6) | u[3] as u32;
        assert_eq!((packed >> 16) & 0xff, 196);
        assert_eq!(packed & 0xffff, 105);
        assert!(u.iter().all(|&x| x <= 63));
    }

    #[test]
    fn ping_addresses_are_parsed_and_sanitised() {
        let u = "/dwl/ping.png?a=1.2.3.4:27015,10.0.0.7:27067&rnd=x";
        assert_eq!(parse_addr_list(u), vec!["1.2.3.4:27015", "10.0.0.7:27067"]);
        // A bad entry is dropped, not fatal to the rest of the batch.
        assert_eq!(
            parse_addr_list("/x?a=evil.example.com:80,1.2.3.4:1,9.9.9.999:2,5.5.5.5:0"),
            vec!["1.2.3.4:1"]
        );
        assert!(parse_addr_list("/x?a=").is_empty());
        assert!(parse_addr_list("/x?b=1").is_empty());
        // Bounded so one request cannot ask for unlimited work.
        let many = (0..200).map(|i| format!("1.2.3.{}:1", i % 256)).collect::<Vec<_>>().join(",");
        assert_eq!(parse_addr_list(&format!("/x?a={}", many)).len(), PING_MAX_ADDRS);
    }

    #[test]
    fn ping_values_survive_the_two_unit_split() {
        for ms in [1u16, 23, 63, 64, 250, 4094, PING_FAILED] {
            let (lo, hi) = ((ms & 63) as u8, (ms >> 6) as u8);
            assert!(hi <= 63, "{} needs more than 12 bits", ms);
            assert_eq!(lo as u16 | ((hi as u16) << 6), ms);
        }
    }

    /// A result only reads back for the batch that is actually current, so a
    /// late poll from a superseded list can't be shown as a live ping.
    #[test]
    fn ping_results_are_scoped_to_their_batch() {
        let seq = {
            let mut b = pings().lock().unwrap();
            b.seq = 11;
            b.results = vec![42, PING_FAILED];
            b.seq
        };
        assert_eq!(ping_result(Some(seq), 0), 42);
        assert_eq!(ping_result(Some(seq), 1), PING_FAILED);
        assert_eq!(ping_result(Some(seq), 9), PING_PENDING, "out of range");
        assert_eq!(ping_result(Some(seq + 1), 0), PING_PENDING, "stale batch");
        assert_eq!(ping_result(None, 0), PING_PENDING);
    }

    #[test]
    fn total_mib_survives_the_two_unit_split() {
        for mib in [0u16, 1, 63, 64, 500, 4095] {
            let (lo, hi) = ((mib & 63) as u8, (mib >> 6) as u8);
            assert_eq!(lo as u16 | ((hi as u16) << 6), mib);
            assert!(hi <= 63);
        }
    }

    #[test]
    fn percent_is_monotonic_and_bounded() {
        assert_eq!(pct63(0, 100), 0);
        assert_eq!(pct63(100, 100), 63);
        assert_eq!(pct63(150, 100), 63, "overshoot clamps");
        assert_eq!(pct63(5, 0), 0, "no divide by zero");
        let mut last = 0;
        for d in 0..=100u64 {
            let p = pct63(d, 100);
            assert!(p >= last);
            last = p;
        }
    }

    /// Byte weighting is the point of the plan: a skipped item must not advance
    /// the bar, and a big item must dominate a small one.
    #[test]
    fn plan_weights_progress_by_bytes() {
        let p = build_plan(&[0, 900, 100]);
        assert_eq!(p.total, 1000);
        assert_eq!(p.prefix, vec![0, 0, 900, 1000], "bytes done before each item");
        assert_eq!(p.files_total, 2, "an already-installed item is not counted");
        // A skipped first item must leave the bar at zero, and the big item
        // must dominate the small one.
        assert_eq!(pct63(p.prefix[1], p.total), 0);
        assert_eq!(pct63(p.prefix[2], p.total), 56);
        assert_eq!(pct63(p.prefix[3], p.total), 63);
    }

    // ── live HTTP round trip ────────────────────────────────────────────────
    // Exercises the real tiny_http path an in-game request takes. Everything
    // above tests bytes in isolation; this is the only check that the addon
    // would actually get a readable image back over a socket.

    fn get(port: u16, path: &str, extra_headers: &str) -> (u16, Vec<u8>) {
        use std::io::{Read, Write};
        let mut s = std::net::TcpStream::connect(("127.0.0.1", port)).expect("connect");
        // Connection: close makes the server hang up after responding, so
        // read_to_end terminates without parsing a body length.
        let req = format!(
            "GET {} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n{}\r\n",
            path, extra_headers
        );
        s.write_all(req.as_bytes()).expect("write");
        let mut buf = Vec::new();
        s.read_to_end(&mut buf).expect("read");
        let split = buf.windows(4).position(|w| w == b"\r\n\r\n").expect("headers end");
        let head = String::from_utf8_lossy(&buf[..split]).to_string();
        let status: u16 = head
            .split_whitespace()
            .nth(1)
            .and_then(|c| c.parse().ok())
            .expect("status code");
        (status, buf[split + 4..].to_vec())
    }

    fn dims(png: &[u8]) -> (u32, u32) {
        assert_eq!(&png[..8], &[0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a]);
        (
            u32::from_be_bytes(png[16..20].try_into().unwrap()),
            u32::from_be_bytes(png[20..24].try_into().unwrap()),
        )
    }

    /// Serve without an AppHandle: every route under test here is pure state.
    /// `simulate` makes `/dwl/prep.png` start a scripted job instead of a real
    /// download, so the whole lifecycle can be driven against the real encoder.
    fn spawn_stateless(server: tiny_http::Server) {
        spawn_stateless_opt(server, false)
    }

    fn spawn_stateless_opt(server: tiny_http::Server, simulate: bool) {
        std::thread::spawn(move || {
            for req in server.incoming_requests() {
                if from_browser(&req) {
                    let _ = req.respond(tiny_http::Response::empty(403));
                    continue;
                }
                let url = req.url().to_string();
                if simulate && path_of(&url) == "/dwl/prep.png" {
                    let seq = match parse_addr(&url) {
                        Some(_) => start_scripted_job(),
                        None => {
                            respond(req, units_png(2, 0));
                            continue;
                        }
                    };
                    respond(req, units_png(1, seq));
                    continue;
                }
                match path_of(&url) {
                    "/dwl/probe.png" => respond(req, png_1bit(PROBE_W, PROBE_H)),
                    "/dwl/hello.png" => respond(req, units_png(PROTO_VERSION, HELLO_MAGIC)),
                    "/dwl/status.png" => {
                        let seq: Option<u8> = query(&url, "j").and_then(|s| s.parse().ok());
                        let i: u8 = query(&url, "i").and_then(|s| s.parse().ok()).unwrap_or(0);
                        let j = job().lock().unwrap();
                        let snap =
                            if seq == Some(j.seq) { j.snap } else { Snapshot::default() };
                        drop(j);
                        respond(
                            req,
                            match i {
                                0 => units_png(snap.state, snap.pct63),
                                1 => units_png(
                                    if snap.state == state::ERROR { snap.err } else { snap.files_done },
                                    snap.files_total,
                                ),
                                _ => units_png(
                                    (snap.total_mib & 63) as u8,
                                    (snap.total_mib >> 6) as u8,
                                ),
                            },
                        );
                    }
                    "/dwl/cancel.png" => {
                        let seq: Option<u8> = query(&url, "j").and_then(|s| s.parse().ok());
                        let mut j = job().lock().unwrap();
                        if seq == Some(j.seq) {
                            j.cancel.store(true, Ordering::Relaxed);
                            j.snap.state = state::CANCELLED;
                        }
                        drop(j);
                        respond(req, units_png(1, 0));
                    }
                    // The list relay, against the real API. `refresh_list_from`
                    // is the same code path the app uses; only the API-URL
                    // lookup (which needs an AppHandle) is short-circuited.
                    "/api/browse/probe.png" => respond(req, png_1bit(PROBE_W, PROBE_H)),
                    "/api/browse/head.png" => {
                        let half: usize = if query(&url, "h") == Some("1") { 1 } else { 0 };
                        let have: i32 =
                            query(&url, "have").and_then(|s| s.parse().ok()).unwrap_or(-1);
                        if half == 0 && !refresh_list_from("https://api.deadworks.net") {
                            respond(req, png_1bit(SENTINEL_DIM, SENTINEL_DIM));
                            continue;
                        }
                        let c = list_cache().lock().unwrap();
                        let image_count = if have >= 0 && have as u8 == c.version {
                            0
                        } else {
                            image_count_for(c.payload.len()) as u16
                        };
                        let u = head_units(c.version, image_count);
                        drop(c);
                        respond(req, units_png(u[half * 2], u[half * 2 + 1]));
                    }
                    "/api/browse/d.png" => {
                        let idx: usize =
                            query(&url, "i").and_then(|s| s.parse().ok()).unwrap_or(0);
                        let c = list_cache().lock().unwrap();
                        let png = units_png(
                            unit_at(&c.payload, idx * 2),
                            unit_at(&c.payload, idx * 2 + 1),
                        );
                        drop(c);
                        respond(req, png);
                    }
                    // Real UDP probes — no AppHandle needed, so the harness can
                    // drive the genuine ping path against genuine servers.
                    "/dwl/ping.png" => {
                        let addrs = parse_addr_list(&url);
                        if addrs.is_empty() {
                            respond(req, units_png(2, 0));
                        } else {
                            println!("[test] pinging {} server(s)", addrs.len());
                            let seq = start_ping_batch(addrs);
                            respond(req, units_png(1, seq));
                        }
                    }
                    "/dwl/pingres.png" => {
                        let seq: Option<u8> = query(&url, "b").and_then(|s| s.parse().ok());
                        let idx: usize =
                            query(&url, "i").and_then(|s| s.parse().ok()).unwrap_or(0);
                        let ms = ping_result(seq, idx);
                        if ms != PING_PENDING {
                            println!("[test] ping[{}] = {} ms", idx, ms);
                        }
                        respond(req, units_png((ms & 63) as u8, (ms >> 6) as u8));
                    }
                    _ => {
                        let _ = req.respond(tiny_http::Response::empty(404));
                    }
                }
            }
        });
    }

    /// Walk a job through every state on a timer, with a realistic plan, so an
    /// external client can be driven end to end without downloading gigabytes.
    fn start_scripted_job() -> u8 {
        let seq = {
            let mut j = job().lock().unwrap();
            j.seq = j.seq.wrapping_add(1) & 63;
            j.snap = Snapshot { state: state::QUEUED, ..Snapshot::default() };
            j.cancel = Arc::new(AtomicBool::new(false));
            j.plan = Plan::default();
            j.seq
        };
        std::thread::spawn(move || {
            let step = std::time::Duration::from_millis(400);
            std::thread::sleep(step);
            publish(seq, |s| s.state = state::RESOLVING);
            std::thread::sleep(step);
            // One skipped item, one big, one small — 1234 MiB in total.
            set_plan(seq, &[0, 1200 * 1024 * 1024, 34 * 1024 * 1024]);
            for pct in [2u8, 10, 24, 40, 55] {
                if job().lock().unwrap().cancel.load(Ordering::Relaxed) {
                    return;
                }
                publish(seq, |s| {
                    s.state = state::DOWNLOADING;
                    s.pct63 = pct;
                    s.files_done = if pct > 30 { 1 } else { 0 };
                });
                std::thread::sleep(step);
            }
            publish(seq, |s| {
                s.state = state::INSTALLING;
                s.pct63 = 60;
                s.files_done = 1;
            });
            std::thread::sleep(step);
            publish(seq, |s| {
                s.state = state::READY;
                s.pct63 = 63;
                s.files_done = s.files_total;
            });
        });
        seq
    }

    /// Not really a test: serves a simulated job on the real port so the
    /// Panorama-simulator harness can drive the actual encoder.
    ///   cargo test --lib -- --ignored --nocapture serves_a_simulated_job
    #[test]
    #[ignore]
    fn serves_a_simulated_job_on_the_real_port() {
        let server = tiny_http::Server::http(("127.0.0.1", PORT_BASE)).expect("bind");
        spawn_stateless_opt(server, true);
        println!("[test] simulated bridge up on {}; serving for 120s", PORT_BASE);
        std::thread::sleep(std::time::Duration::from_secs(120));
    }

    #[test]
    fn serves_readable_images_over_a_socket() {
        // Port 0 so the test never collides with a launcher already running.
        let server = tiny_http::Server::http("127.0.0.1:0").expect("bind");
        let port = server.server_addr().to_ip().expect("ip addr").port();
        spawn_stateless(server);

        let (status, png) = get(port, "/dwl/hello.png", "");
        assert_eq!(status, 200);
        assert_eq!(dims(&png), (unit_dim(PROTO_VERSION), unit_dim(HELLO_MAGIC)));

        let (_, png) = get(port, "/dwl/probe.png", "");
        assert_eq!(dims(&png), (PROBE_W, PROBE_H), "calibration probe");

        // A browser on any website can load 127.0.0.1 images; it must not be
        // able to make the launcher do work.
        let (status, _) = get(port, "/dwl/hello.png", "Sec-Fetch-Site: cross-site\r\n");
        assert_eq!(status, 403, "browser-originated request must be refused");
        let (status, _) = get(port, "/dwl/hello.png", "Origin: https://evil.example\r\n");
        assert_eq!(status, 403, "cross-origin request must be refused");

        let (status, _) = get(port, "/dwl/nope.png", "");
        assert_eq!(status, 404);
    }

    #[test]
    fn status_tracks_the_current_job_only() {
        let server = tiny_http::Server::http("127.0.0.1:0").expect("bind");
        let port = server.server_addr().to_ip().expect("ip addr").port();
        spawn_stateless(server);

        let seq = {
            let mut j = job().lock().unwrap();
            j.seq = 7;
            j.snap = Snapshot {
                state: state::DOWNLOADING,
                pct63: 31,
                files_done: 2,
                files_total: 5,
                total_mib: 1234,
                err: 0,
            };
            j.seq
        };

        let (_, png) = get(port, &format!("/dwl/status.png?j={}&i=0", seq), "");
        assert_eq!(dims(&png), (unit_dim(state::DOWNLOADING), unit_dim(31)));
        let (_, png) = get(port, &format!("/dwl/status.png?j={}&i=1", seq), "");
        assert_eq!(dims(&png), (unit_dim(2), unit_dim(5)));
        let (_, png) = get(port, &format!("/dwl/status.png?j={}&i=2", seq), "");
        assert_eq!(dims(&png), (unit_dim((1234 & 63) as u8), unit_dim((1234 >> 6) as u8)));

        // Wrong sequence number → idle.
        let (_, png) = get(port, &format!("/dwl/status.png?j={}&i=0", seq + 1), "");
        assert_eq!(dims(&png), (unit_dim(state::IDLE), unit_dim(0)));

        // Cancelling the live job flips it, and raises the flag the running
        // download polls.
        let _ = get(port, &format!("/dwl/cancel.png?j={}", seq), "");
        let j = job().lock().unwrap();
        assert_eq!(j.snap.state, state::CANCELLED);
        assert!(j.cancel.load(Ordering::Relaxed));
    }
}
