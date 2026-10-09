mod a2s;
pub(crate) mod fetch;
mod rules;
pub(crate) mod verify;

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use tauri::{Emitter, Manager};
use tokio::io::AsyncWriteExt;

use crate::gameinfo::is_sharing_violation;
use verify::{Expected, HashCache};

const DEFAULT_API_URL: &str = match std::option_env!("DEADWORKS_API_URL") {
    Some(url) => url,
    None => "https://api.deadworks.net",
};

const VPK_MAGIC: [u8; 4] = [0x34, 0x12, 0xAA, 0x55]; // 0x55aa1234 LE

/// Hard cap on VPK size (4 GiB), both as downloaded and once decompressed, to bound
/// what a hostile server or host can make the launcher fetch and write.
pub(crate) const MAX_VPK_BYTES: u64 = 4 * 1024 * 1024 * 1024;

/// The most one join may put on disk, across all of a server's content.
const MAX_JOIN_BYTES: u64 = 16 * 1024 * 1024 * 1024;
/// A download has to bring in at least this much in every window of this length.
const SLOW_WINDOW: Duration = Duration::from_secs(60);
const SLOW_FLOOR_BYTES: u64 = 512 * 1024;

/// How long to wait for a server's A2S_RULES reply before asking the API instead.
const RULES_TIMEOUT: Duration = Duration::from_millis(1500);

// ── Types ──

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DownloadProgress {
    pub name: String,
    pub status: String,
    pub bytes_downloaded: u64,
    pub total_bytes: u64,
    pub item_index: usize,
    pub total_items: usize,
}

#[derive(Debug, Clone, Deserialize)]
struct ManifestItem {
    filename: String,
    kind: String, // "map" | "addon"
    version: u64,
    #[serde(default)]
    compressed_size: u64,
    download_url: String,
    /// When set, the item is checked by content rather than by `version`: the file's SHA-256
    /// has to start with this. Items built from a server's A2S_RULES always carry the hash the
    /// server advertised; the API does not send one yet.
    #[serde(default)]
    sha256: Option<String>,
    /// Uncompressed size in bytes, when known - servers advertise it, since they never see the
    /// compressed copy. Weights progress up front and must match the decompressed file exactly.
    #[serde(default)]
    size: Option<u64>,
    /// Install what the host serves even when it is not the build `sha256` names. Set only
    /// after the player was shown that exact mismatch and chose to join anyway; never from
    /// the wire, which is why it is skipped rather than defaulted.
    #[serde(skip)]
    accept_mismatch: bool,
    /// Whether the download may come from the player's own machine or network: only when the
    /// game server that named the host is itself there. Never set from the wire.
    #[serde(skip)]
    local_source: bool,
}

impl ManifestItem {
    /// The hash this item must match, if it names one worth the name. `starts_with` is how a
    /// prefix is compared, so an empty or stub hash would match any file at all.
    fn wanted_hash(&self) -> Option<&str> {
        self.sha256
            .as_deref()
            .filter(|h| h.len() >= rules::HASH_LEN && h.bytes().all(|b| b.is_ascii_hexdigit()))
    }

    fn expected(&self) -> Option<Expected> {
        self.wanted_hash().map(|sha256| Expected {
            sha256: sha256.to_string(),
            size: self.size,
            enforce: !self.accept_mismatch,
        })
    }
}

/// How hard a content pass insists on getting exactly what the server advertised.
#[derive(Clone, Copy, PartialEq)]
pub(crate) enum Strictness {
    /// A copy that is not the advertised build fails the join.
    Exact,
    /// Install that copy regardless. What the player asked for with CONNECT ANYWAY.
    AcceptMismatch,
}

#[derive(Debug, Deserialize)]
struct ContentManifest {
    items: Vec<ManifestItem>,
}

/// `/api/servers/lookup?address=` returns a whole server record with the content
/// manifest as one field. This is how a caller holding only an `ip:port` — the
/// in-game browser, which never sees server ids — resolves what to download.
#[derive(Debug, Deserialize)]
struct LookupResponse {
    #[serde(default)]
    content: Vec<ManifestItem>,
}

/// Returned when a caller's cancel flag is raised. A plain sentinel string so
/// the `Result<_, String>` signatures the whole module (and the launcher UI)
/// already use stay exactly as they are.
pub(crate) const CANCELLED_MSG: &str = "CANCELLED";

fn cancelled(flag: &Option<Arc<AtomicBool>>) -> bool {
    flag.as_ref().is_some_and(|f| f.load(Ordering::Relaxed))
}

/// Classify a failure into the compact code the in-game image channel can
/// carry. Matching on message text is not pretty, but the alternative would be
/// rewriting the error strings the launcher UI already shows — including the
/// `FILE_IN_USE:` sentinel the connect dialog greps for.
/// The download host is serving a different build than the server runs. Distinct from the
/// generic bad-content code because the in-game UI offers to install it anyway.
pub(crate) const ERR_CONTENT_MISMATCH: u8 = 8;
/// A download itself failed: the host is down, or does not have the file. Kept apart from 2,
/// which blames the Deadworks API, because the host is usually the server operator's own.
pub(crate) const ERR_DOWNLOAD_HOST: u8 = 9;
/// The server's content is more than the launcher will fetch for one join.
pub(crate) const ERR_TOO_MUCH_CONTENT: u8 = 10;

pub(crate) fn error_code(msg: &str) -> u8 {
    if msg.starts_with("FILE_IN_USE:") {
        6
    } else if msg.contains("does not match the version the server runs") {
        // Not the generic bad-content 4: nothing is corrupt here, the download host is
        // just out of sync with the server, and it is the one failure the player can be
        // offered a way through. An addon that predates this code falls back to its
        // generic wording, so this needs no protocol bump. Checked before the rest: the
        // message names the file, and a file can be called anything.
        ERR_CONTENT_MISMATCH
    } else if msg.starts_with("this server asks for") || msg.starts_with("this server's content is more than") {
        ERR_TOO_MUCH_CONTENT
    } else if msg.contains("gameinfo.gi") || msg.contains("bootstrap addon") || msg.contains("requires bootstrap") {
        7
    } else if msg.contains("No online server") || msg.starts_with("API returned HTTP 404") {
        // Only the lookup's own "not found". A download host answering 404 is a failed
        // download, not a server that is offline.
        1
    } else if msg.starts_with("Download request failed")
        || msg.starts_with("Download failed")
        || msg.starts_with("Download error")
    {
        ERR_DOWNLOAD_HOST
    } else if msg.contains("not a valid VPK")
        || msg.contains("Failed to read VPK magic")
        || msg.contains("decompression failed")
        || msg.contains("exceeds maximum size")
    {
        4
    } else if msg.contains("Failed to create")
        || msg.contains("Write error")
        || msg.contains("Failed to install")
        || msg.contains("Failed to write")
        || msg.contains("filename")
    {
        3
    } else if msg.contains("request failed")
        || msg.contains("API returned")
        || msg.contains("Failed to parse manifest")
    {
        2
    } else {
        5
    }
}

#[derive(Debug, Default, Serialize, Deserialize, Clone)]
struct VersionEntry {
    kind: String,
    version: u64,
    /// SHA-256 of the installed file; absent for entries written before hashes were tracked.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    sha256: Option<String>,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct VersionsState {
    /// What the launcher installed, keyed `kind/filename`. Older files are keyed by filename
    /// alone, which let an addon and a map of the same name overwrite each other's entry.
    #[serde(default)]
    managed: HashMap<String, VersionEntry>,
}

impl VersionsState {
    fn key(kind: &str, filename: &str) -> String {
        format!("{kind}/{filename}")
    }

    fn entry(&self, kind: &str, filename: &str) -> Option<&VersionEntry> {
        self.managed
            .get(&Self::key(kind, filename))
            .or_else(|| self.managed.get(filename).filter(|e| e.kind == kind))
    }

    fn record(&mut self, filename: &str, entry: VersionEntry) {
        if self.managed.get(filename).is_some_and(|old| old.kind == entry.kind) {
            self.managed.remove(filename);
        }
        self.managed.insert(Self::key(&entry.kind, filename), entry);
    }
}

// ── Validation ──

/// Reject filenames that could cause path traversal or absolute writes. Only
/// a single path component with no reserved characters is allowed.
fn validate_filename(name: &str) -> Result<(), String> {
    if name.is_empty() {
        return Err("empty filename".into());
    }
    if name.len() > 128 {
        return Err(format!("filename too long: {}", name));
    }
    if name == "." || name == ".." {
        return Err(format!("invalid filename: {}", name));
    }
    for c in name.chars() {
        match c {
            '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|' => {
                return Err(format!("filename contains reserved character: {}", name));
            }
            c if c.is_control() => {
                return Err(format!("filename contains reserved character: {:?}", name));
            }
            _ => {}
        }
    }
    // Windows treats these as devices whatever the extension, so `con.vpk` can
    // never be a file.
    const DEVICES: [&str; 22] = [
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7",
        "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];
    let stem = name.split('.').next().unwrap_or(name);
    if DEVICES.iter().any(|d| stem.eq_ignore_ascii_case(d)) {
        return Err(format!("filename is a reserved device name: {}", name));
    }
    Ok(())
}

// ── Helpers ──

fn find_game_dir() -> Result<PathBuf, String> {
    if let Some(override_dir) = crate::connect::get_game_dir_override() {
        return Ok(override_dir);
    }
    crate::connect::find_deadlock_game_dir()
}

fn ensure_dir(path: &Path) -> Result<(), String> {
    std::fs::create_dir_all(path)
        .map_err(|e| format!("Failed to create directory {}: {}", path.display(), e))
}

fn target_dir_for(kind: &str, game_dir: &Path) -> Result<PathBuf, String> {
    let citadel = game_dir.join("citadel");
    match kind {
        "map" => Ok(citadel.join("maps")),
        "addon" => Ok(citadel.join("deadworks_addons").join("vpks")),
        other => Err(format!("Unknown content kind: {}", other)),
    }
}

fn versions_path(game_dir: &Path) -> PathBuf {
    game_dir
        .join("citadel")
        .join("deadworks_cache")
        .join("versions.json")
}

fn load_versions(game_dir: &Path) -> VersionsState {
    let path = versions_path(game_dir);
    std::fs::read(&path)
        .ok()
        .and_then(|bytes| serde_json::from_slice::<VersionsState>(&bytes).ok())
        .unwrap_or_default()
}

fn save_versions(game_dir: &Path, state: &VersionsState) -> Result<(), String> {
    let path = versions_path(game_dir);
    if let Some(parent) = path.parent() {
        ensure_dir(parent)?;
    }
    let bytes = serde_json::to_vec_pretty(state)
        .map_err(|e| format!("Failed to serialize versions.json: {}", e))?;
    // Whole or not at all: a half-written file reads back as "nothing was installed by us".
    let tmp = path.with_extension("json.part");
    std::fs::write(&tmp, &bytes)
        .and_then(|()| std::fs::rename(&tmp, &path))
        .map_err(|e| format!("Failed to write versions.json: {}", e))
}

fn verify_vpk_magic(path: &Path) -> Result<(), String> {
    use std::io::Read;
    let mut f = std::fs::File::open(path)
        .map_err(|e| format!("Failed to open {} for magic check: {}", path.display(), e))?;
    let mut buf = [0u8; 4];
    f.read_exact(&mut buf).map_err(|e| {
        format!("Failed to read VPK magic from {}: {}", path.display(), e)
    })?;
    if buf != VPK_MAGIC {
        return Err(format!(
            "decompressed payload for {} is not a valid VPK (magic mismatch)",
            path.display()
        ));
    }
    Ok(())
}

/// Resolve the manifest API URL from persisted settings rather than trusting
/// the webview to supply one. Production builds ignore the `local` endpoint.
pub(crate) fn resolve_api_url(app: &tauri::AppHandle) -> String {
    use tauri_plugin_store::StoreBuilder;
    if cfg!(debug_assertions) {
        if let Ok(store) = StoreBuilder::new(app, "settings.json").build() {
            let endpoint = store
                .get("api_endpoint")
                .and_then(|v| v.as_str().map(String::from))
                .unwrap_or_default();
            if endpoint == "local" {
                return "http://localhost:8787".to_string();
            }
        }
    }
    DEFAULT_API_URL.to_string()
}

async fn fetch_manifest(api_url: &str, server_id: &str) -> Result<ContentManifest, String> {
    let url = format!("{}/api/servers/{}/content", api_url, server_id);
    let resp = reqwest::get(&url)
        .await
        .map_err(|e| format!("API request failed: {}", e))?;
    if !resp.status().is_success() {
        return Err(format!("API returned HTTP {}", resp.status()));
    }
    resp.json::<ContentManifest>()
        .await
        .map_err(|e| format!("Failed to parse manifest: {}", e))
}

/// Resolve a manifest from a bare `ip:port`, which is all the in-game browser
/// knows about a server.
async fn lookup_manifest(api_url: &str, addr: &str) -> Result<ContentManifest, String> {
    let url = format!("{}/api/servers/lookup?address={}", api_url, addr);
    let resp = reqwest::get(&url)
        .await
        .map_err(|e| format!("API request failed: {}", e))?;
    if resp.status().as_u16() == 404 {
        return Err("No online server found at that address (HTTP 404)".into());
    }
    if !resp.status().is_success() {
        return Err(format!("API returned HTTP {}", resp.status()));
    }
    let detail = resp
        .json::<LookupResponse>()
        .await
        .map_err(|e| format!("Failed to parse manifest: {}", e))?;
    Ok(ContentManifest { items: detail.content })
}

/// Download `.vpk.bz2` from `url` into `dest_vpk` as a fully decompressed `.vpk`,
/// returning its SHA-256. Enforces `MAX_VPK_BYTES` on the download and during
/// decompression so a malicious manifest cannot mount a bz2 bomb. When
/// `expected` is given - what a server advertised - the result has to match it.
/// Uses a temp `.part` file beside the destination, then atomic rename.
///
/// `channel` is the Tauri event name progress is emitted on, and `emitter` is
/// either a `Window` (server content, driven by a visible connect dialog) or an
/// `AppHandle` (the bootstrap poller and the in-game bridge, which run with no
/// window in the tray).
///
/// `cancel`, when supplied, is polled per chunk so a multi-gigabyte download can
/// actually be abandoned rather than merely hidden.
#[allow(clippy::too_many_arguments)]
pub(crate) async fn download_and_decompress<E>(
    url: &str,
    dest_vpk: &Path,
    item_name: &str,
    index: usize,
    total: usize,
    expected_uncompressed_hint: u64,
    expected: Option<Expected>,
    channel: &str,
    window: &E,
    cancel: Option<Arc<AtomicBool>>,
    local_source: bool,
    max_bytes: u64,
) -> Result<String, String>
where
    E: Emitter<tauri::Wry> + Clone + Send + Sync + 'static,
{
    let response = fetch::get(url, local_source)
        .await
        .map_err(|e| format!("Download request failed for {}: {}", item_name, e))?;
    if !response.status().is_success() {
        return Err(format!(
            "Download failed for {}: HTTP {}",
            item_name,
            response.status()
        ));
    }

    let too_big = || {
        format!(
            "compressed payload for {} exceeds maximum size ({} bytes)",
            item_name, max_bytes
        )
    };
    let total_compressed = response.content_length().unwrap_or(0);
    if total_compressed > max_bytes {
        return Err(too_big());
    }
    let bz2_tmp = dest_vpk.with_extension("vpk.bz2.part");
    let vpk_tmp = dest_vpk.with_extension("vpk.part");
    // Whichever way this ends, neither temp file outlives it: the compressed one is always
    // finished with, and the unpacked one is renamed into place on success.
    let _temps = TempFiles(vec![bz2_tmp.clone(), vpk_tmp.clone()]);

    // Stream compressed bytes to temp file
    {
        let mut file = tokio::fs::File::create(&bz2_tmp)
            .await
            .map_err(|e| format!("Failed to create temp file: {}", e))?;

        let mut stream = response.bytes_stream();
        let mut downloaded: u64 = 0;
        // The host sets the pace, so it is held to one: a download that trickles would otherwise
        // keep a join (and the install lock) alive for as long as its host liked.
        let mut window_started = std::time::Instant::now();
        let mut window_bytes: u64 = 0;
        loop {
            // Woken every second whether or not anything arrived, so cancelling takes effect
            // against a host that has gone quiet.
            let next = tokio::time::timeout(Duration::from_secs(1), stream.next()).await;
            if cancelled(&cancel) {
                return Err(CANCELLED_MSG.into());
            }
            if window_started.elapsed() >= SLOW_WINDOW {
                if window_bytes < SLOW_FLOOR_BYTES {
                    return Err(format!("Download error for {}: the download host is sending too slowly", item_name));
                }
                window_started = std::time::Instant::now();
                window_bytes = 0;
            }
            let chunk = match next {
                Err(_) => continue,
                Ok(None) => break,
                Ok(Some(chunk)) => chunk,
            };
            let chunk = chunk.map_err(|e| format!("Download error for {}: {}", item_name, e))?;
            downloaded += chunk.len() as u64;
            window_bytes += chunk.len() as u64;
            // Content-Length is only a claim; hold the stream itself to the cap.
            if downloaded > max_bytes {
                return Err(too_big());
            }
            file.write_all(&chunk)
                .await
                .map_err(|e| format!("Write error: {}", e))?;
            let _ = window.emit(
                channel,
                DownloadProgress {
                    name: item_name.to_string(),
                    status: "downloading".into(),
                    bytes_downloaded: downloaded,
                    total_bytes: total_compressed,
                    item_index: index,
                    total_items: total,
                },
            );
        }
        file.flush().await.map_err(|e| format!("Flush error: {}", e))?;
    }

    // Decompress into a sibling .vpk.part file, hashing as it goes.
    let bz2_tmp_clone = bz2_tmp.clone();
    let vpk_tmp_clone = vpk_tmp.clone();
    let name = item_name.to_string();
    let win = window.clone();
    let chan = channel.to_string();
    let cancel_bg = cancel.clone();
    // An item with no size given at all gets an estimate from what came down.
    let hint = if expected_uncompressed_hint > 0 {
        expected_uncompressed_hint
    } else {
        total_compressed.saturating_mul(3)
    };

    let sha = tokio::task::spawn_blocking(move || {
        verify::decompress_verified(
            &bz2_tmp_clone,
            &vpk_tmp_clone,
            &name,
            max_bytes,
            expected.as_ref(),
            |written| {
                if cancelled(&cancel_bg) {
                    return Err(CANCELLED_MSG.into());
                }
                let _ = win.emit(
                    chan.as_str(),
                    DownloadProgress {
                        name: name.clone(),
                        status: "decompressing".into(),
                        bytes_downloaded: written,
                        total_bytes: hint,
                        item_index: index,
                        total_items: total,
                    },
                );
                Ok(())
            },
        )
    })
    .await
    .map_err(|e| format!("Decompress task failed: {}", e))
    .and_then(|r| r)?;

    verify_vpk_magic(&vpk_tmp)?;

    // Atomic rename onto the canonical path. This is the step that can fail
    // with a sharing violation if the engine has the old file open.
    match std::fs::rename(&vpk_tmp, dest_vpk) {
        Ok(()) => Ok(sha),
        Err(e) if is_sharing_violation(&e) => {
            Err(format!(
                "FILE_IN_USE: {} is currently loaded by Deadlock. Please fully disconnect or quit the game and try again.",
                dest_vpk
                    .file_name()
                    .map(|n| n.to_string_lossy().to_string())
                    .unwrap_or_default()
            ))
        }
        Err(e) => Err(format!("Failed to install {}: {}", dest_vpk.display(), e)),
    }
}

/// Temp files of one download, removed when it ends. A file that was renamed into place is
/// simply no longer there to remove.
struct TempFiles(Vec<PathBuf>);

impl Drop for TempFiles {
    fn drop(&mut self) {
        for path in &self.0 {
            let _ = std::fs::remove_file(path);
        }
    }
}

/// One install at a time. Two passes for the same server - a join started again while the last
/// one was still downloading - would otherwise write the same temp files and the same
/// versions.json over each other.
static INSTALLING: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

// ── Main command ──

/// The join a launcher window is waiting on, so its CANCEL can stop it. Starting another
/// cancels the one before: nothing is left running behind a dialog that has gone.
static WINDOW_JOIN: Mutex<Option<Arc<AtomicBool>>> = Mutex::new(None);

fn begin_window_join() -> Arc<AtomicBool> {
    let flag = Arc::new(AtomicBool::new(false));
    if let Some(previous) = WINDOW_JOIN.lock().unwrap().replace(flag.clone()) {
        previous.store(true, Ordering::Relaxed);
    }
    flag
}

/// Stops the join started by `prepare_and_connect` or `connect_anyway`: the downloads end, and
/// the game is not launched when they would have finished.
#[tauri::command]
pub fn cancel_connect() {
    if let Some(flag) = WINDOW_JOIN.lock().unwrap().as_ref() {
        flag.store(true, Ordering::Relaxed);
    }
}

#[tauri::command]
pub async fn prepare_and_connect(
    window: tauri::Window,
    server_id: String,
    addr: String,
) -> Result<crate::connect::ConnectResult, String> {
    let cancel = begin_window_join();
    prepare(&window, &server_id, &addr, Strictness::Exact, &cancel).await?;
    if cancel.load(Ordering::Relaxed) {
        return Err(CANCELLED_MSG.into());
    }
    let _ = window.emit("download-progress", connecting_event());
    crate::connect::connect_to_server_inner(&addr)
}

/// Join after the content check failed and the player chose to go regardless.
///
/// Takes the whole pass again with a mismatch tolerated, because the usual cause is a host
/// left on an older build than the server runs. The operator picks both the hash and the
/// host, so the hash pins which build rather than guarding against anyone, and the wrong
/// models beat no content at all. Anything that still fails is logged rather than raised:
/// the player has already answered the only question this had to ask them.
#[tauri::command]
pub async fn connect_anyway(
    window: tauri::Window,
    server_id: String,
    addr: String,
    accept_mismatch: bool,
) -> Result<crate::connect::ConnectResult, String> {
    // Only a mismatch the player was just shown is theirs to wave through. After any other
    // failure this is one more ordinary attempt, whose errors no longer stop the join.
    let strictness = if accept_mismatch { Strictness::AcceptMismatch } else { Strictness::Exact };
    let cancel = begin_window_join();
    let prepared = prepare(&window, &server_id, &addr, strictness, &cancel).await;
    // "Regardless" covers what went wrong with the content, not a player who has since left.
    if cancel.load(Ordering::Relaxed) {
        return Err(CANCELLED_MSG.into());
    }
    match prepared {
        // Only a pass that got everything gets to say so; the dialog reads this as "done".
        Ok(()) => {
            let _ = window.emit("download-progress", connecting_event());
        }
        Err(e) => eprintln!("[content] joining {addr} regardless: {e}"),
    }
    crate::connect::connect_to_server_inner(&addr)
}

/// The event that tells the connect dialog the content work is over.
fn connecting_event() -> serde_json::Value {
    progress_event("connecting")
}

/// A status for the connect dialog that is about the join as a whole, not one file.
fn progress_event(status: &str) -> serde_json::Value {
    serde_json::json!({ "name": "", "status": status, "bytes_downloaded": 0, "total_bytes": 0, "item_index": 0, "total_items": 0 })
}

/// Everything that has to be true on disk before `addr` is worth connecting to.
async fn prepare(
    window: &tauri::Window,
    server_id: &str,
    addr: &str,
    strictness: Strictness,
    cancel: &Arc<AtomicBool>,
) -> Result<(), String> {
    let api_url = resolve_api_url(window.app_handle());

    let _ = window.emit(
        "download-progress",
        serde_json::json!({ "name": "", "status": "fetching", "bytes_downloaded": 0, "total_bytes": 0, "item_index": 0, "total_items": 0 }),
    );

    // No id: a server found on Steam's list, or one joined by its address. The server is asked
    // first either way; the registry is only looked in, by address, when the server names no
    // download host of its own - which is what a server still on the registry looks like.
    let record = Some(if server_id.is_empty() { ApiRecord::Address(addr) } else { ApiRecord::Id(server_id) });
    let Resolved { items, source, incomplete } = resolve_items(addr, &api_url, record.as_ref(), strictness).await?;

    // Validate every item up-front so a bad manifest is rejected before we
    // touch the filesystem.
    for item in &items {
        validate_filename(&item.filename)?;
    }

    let game_dir = find_game_dir()?;
    prepare_gameinfo(&game_dir, &items)?;

    // The bootstrap addon has to be on disk before the game starts, so this is
    // awaited here rather than left to the background poller. `require_present`
    // is on: joining with no bootstrap at all is a hard failure, unlike joining
    // with a merely-stale one.
    let bootstrap_status =
        crate::bootstrap::ensure(window.app_handle(), &game_dir, true).await?;
    // Gate on what will actually be mounted, not on what is staged — a
    // search-path VPK cannot be swapped under a running game.
    crate::bootstrap::gate(&bootstrap_status)?;

    if items.is_empty() {
        if incomplete {
            let _ = window.emit("download-progress", progress_event("incomplete"));
        }
        return Ok(());
    }

    let installed = install_items(&items, &game_dir, "download-progress", window, Some(cancel.clone()), None).await;
    match installed {
        // What the server advertised could not be installed, so use the API's record if
        // it has one that covers the same content. Otherwise the failure is the answer.
        Err(e) if source == Source::Advertised && can_fall_back(&e) => {
            let Some(items) = api_stand_in(record.as_ref(), &api_url, &items, &game_dir).await else {
                return Err(e);
            };
            eprintln!("[content] {e}; falling back to the Deadworks API");
            let stand_in = async {
                for item in &items {
                    validate_filename(&item.filename)?;
                }
                prepare_gameinfo(&game_dir, &items)?;
                install_items(&items, &game_dir, "download-progress", window, Some(cancel.clone()), None).await
            };
            // The record did not have those builds either: the first failure is the one
            // the player can act on, so that is the one they see.
            if let Err(second) = stand_in.await {
                eprintln!("[content] the Deadworks API's copy did not work either: {second}");
                return Err(e);
            }
        }
        other => other?,
    }

    if incomplete {
        let _ = window.emit("download-progress", progress_event("incomplete"));
    }
    Ok(())
}

/// Re-assert our SearchPaths block before a connect. Deadlock Mod Manager
/// regenerates the whole block on each of its launches and Steam's file
/// verification restores the vanilla file, so a patch applied at startup may
/// already be gone.
fn prepare_gameinfo(game_dir: &Path, items: &[ManifestItem]) -> Result<(), String> {
    let has_addons = items.iter().any(|i| i.kind == "addon");
    if let Err(e) = crate::gameinfo::ensure_patched(game_dir) {
        // The write failed, but the entries may already be live from an
        // earlier run — only block the connect if what we need is missing.
        let usable = crate::gameinfo::status(game_dir)
            .map(|s| s.has_mods_search_path && (!has_addons || s.has_addonroot))
            .unwrap_or(false);
        if !usable {
            return Err(format!("Could not prepare gameinfo.gi: {}", e));
        }
        eprintln!("[connect] gameinfo.gi not rewritten ({e}); existing entries are current");
    }
    Ok(())
}

/// Whether a failed install of what a server advertised is worth retrying from
/// the API's record. Not when the user cancelled, the game holds a file open, or
/// gameinfo.gi needs fixing: those fail the same way whatever the source.
fn can_fall_back(e: &str) -> bool {
    e != CANCELLED_MSG && !e.starts_with("FILE_IN_USE:") && !e.contains("gameinfo.gi")
}

/// The API's record of a server, when there is one that can stand in for what the server
/// advertised.
async fn api_stand_in(
    record: Option<&ApiRecord<'_>>,
    api_url: &str,
    advertised: &[ManifestItem],
    game_dir: &Path,
) -> Option<Vec<ManifestItem>> {
    let mut items = record?.items(api_url).await.ok()?;
    let state = load_versions(game_dir);
    if !api_covers(&items, advertised, game_dir, &state) {
        return None;
    }
    // The record's copies are versioned by an upload counter. Standing in for an advertised
    // build, they have to be that build, or the join would quietly run on something else.
    for item in &mut items {
        if let Some(wanted) = advertised.iter().find(|a| a.filename == item.filename && a.kind == item.kind) {
            item.sha256 = wanted.sha256.clone();
            item.size = wanted.size;
            item.accept_mismatch = wanted.accept_mismatch;
        }
    }
    Some(items)
}

/// Whether the API's record names everything a server advertised. A server that hosts its own
/// content usually has little or nothing uploaded, and "falling back" to that would join
/// without the content and hide why. A map already on disk that the launcher did not install
/// is left alone by an advertised install, so the record does not have to name it.
fn api_covers(
    api: &[ManifestItem],
    advertised: &[ManifestItem],
    game_dir: &Path,
    state: &VersionsState,
) -> bool {
    advertised.iter().all(|wanted| {
        let named = api.iter().any(|i| i.filename == wanted.filename && i.kind == wanted.kind);
        let kept_local_map = wanted.kind == "map"
            && state.entry(&wanted.kind, &wanted.filename).is_none()
            && target_dir_for(&wanted.kind, game_dir)
                .is_ok_and(|dir| dir.join(format!("{}.vpk", wanted.filename)).exists());
        named || kept_local_map
    })
}

/// A server's record at the Deadworks API: where its content list came from before servers
/// advertised it themselves. Deprecated, but still the source for every server that has not
/// taken over hosting its own content, so that nothing changes for those.
enum ApiRecord<'a> {
    /// The id the server list gave the launcher.
    Id(&'a str),
    /// All the in-game browser knows about a server.
    Address(&'a str),
}

impl ApiRecord<'_> {
    async fn items(&self, api_url: &str) -> Result<Vec<ManifestItem>, String> {
        let manifest = match self {
            ApiRecord::Id(id) => fetch_manifest(api_url, id).await?,
            ApiRecord::Address(addr) => lookup_manifest(api_url, addr).await?,
        };
        Ok(manifest.items)
    }
}

#[derive(Debug, PartialEq)]
enum Source {
    Api,
    Advertised,
}

/// What to install before joining a server, and which source said so.
struct Resolved {
    items: Vec<ManifestItem>,
    source: Source,
    /// The server said it runs more than it could list, so `items` is not everything.
    incomplete: bool,
}

/// Which source describes a server's content while both exist. Naming a dw_fastdl host is how
/// a server takes over: what it advertises is then the whole truth. One that names none still
/// keeps its content on Deadworks' hosting, where the API's record is the list that matches
/// what is stored - asking the server instead would only fetch the same files a second way and
/// fail on any that were not re-uploaded. Its advertisement is used when there is no record.
fn preferred_source(advertised: Option<&rules::Advertised>, has_record: bool) -> Option<Source> {
    match advertised {
        Some(a) if a.fastdl.is_some() => Some(Source::Advertised),
        _ if has_record => Some(Source::Api),
        Some(_) => Some(Source::Advertised),
        None => None,
    }
}

async fn resolve_items(
    addr: &str,
    api_url: &str,
    record: Option<&ApiRecord<'_>>,
    strictness: Strictness,
) -> Result<Resolved, String> {
    let advertised = query_advertised(addr).await;
    if preferred_source(advertised.as_ref(), record.is_some()) == Some(Source::Api) {
        if let Some(record) = record {
            match record.items(api_url).await {
                Ok(items) => return Ok(Resolved { items, source: Source::Api, incomplete: false }),
                Err(e) if advertised.is_none() => return Err(e),
                Err(e) => eprintln!("[content] No API record of {addr} ({e}); using what it advertises"),
            }
        }
    }
    let Some(advertised) = advertised else {
        return Err(format!(
            "{addr} does not advertise its content, and the Deadworks API has no record of it"
        ));
    };
    if !advertised.complete {
        // Entries past the reply's size cap, or ones the server could not hash, are not known.
        eprintln!("[content] {addr} did not advertise all of its content; installing what it listed");
    }
    Ok(Resolved {
        items: items_from_advertised(&advertised, api_url, strictness, fetch::server_is_local(addr)),
        source: Source::Advertised,
        incomplete: !advertised.complete,
    })
}

/// What the server at `addr` advertises in A2S_RULES. None when it does not answer (an older
/// server, or one started with -nomaster) or does not use the format.
async fn query_advertised(addr: &str) -> Option<rules::Advertised> {
    let query_addr = addr.to_string();
    let stranger = !fetch::server_is_local(addr);
    let reply = tokio::task::spawn_blocking(move || a2s::query_rules(&query_addr, RULES_TIMEOUT, stranger))
        .await
        .ok()?;
    let advertised = match reply {
        Ok(reply) => rules::parse(&reply)?,
        Err(e) => {
            eprintln!("[content] {e}");
            return None;
        }
    };
    if !advertised.rejected.is_empty() {
        eprintln!(
            "[content] Ignoring content with unsafe names from {}: {}",
            addr,
            advertised.rejected.join(", ")
        );
    }
    Some(advertised)
}

/// Install items for what a server advertised: fetched from its dw_fastdl host,
/// or from the Deadworks-hosted copy when it names none, and each checked against
/// the hash the server published - unless `strictness` says the player has already been
/// shown a mismatch and chosen to take the copy anyway. An entry the server could not hash
/// is left out either way; there is nothing to fetch it by.
fn items_from_advertised(
    advertised: &rules::Advertised,
    api_url: &str,
    strictness: Strictness,
    server_is_local: bool,
) -> Vec<ManifestItem> {
    let base = advertised
        .fastdl
        .clone()
        .unwrap_or_else(|| format!("{}/fastdl", api_url.trim_end_matches('/')));
    advertised
        .entries
        .iter()
        .filter_map(|entry| {
            Some(ManifestItem {
                filename: entry.name.clone(),
                kind: entry.kind.as_str().to_string(),
                version: 0,
                compressed_size: 0,
                download_url: rules::fastdl_url(&base, entry)?,
                sha256: entry.hash.clone(),
                size: entry.size,
                accept_mismatch: strictness == Strictness::AcceptMismatch,
                local_source: server_is_local,
            })
        })
        .collect()
}

/// Whether `item` is already installed at `dest`, so there is nothing to fetch. Verifying a large
/// file means reading all of it, so `progress` is called with (bytes read, total) while that runs.
async fn already_current(
    item: &ManifestItem,
    dest: &Path,
    state: &VersionsState,
    hashes: &Arc<Mutex<HashCache>>,
    progress: &Arc<dyn Fn(u64, u64) + Send + Sync>,
) -> Result<bool, String> {
    let managed = state.entry(&item.kind, &item.filename);
    let Some(wanted) = item.wanted_hash() else {
        // Versioned by the API's upload counter.
        return Ok(dest.exists() && managed.is_some_and(|e| e.version == item.version));
    };

    // Checked by content: the item names the exact build it wants. A length that
    // differs from the advertised size settles it without reading the file.
    let local_len = std::fs::metadata(dest).ok().map(|m| m.len());
    let matches = match (local_len, item.size) {
        (None, _) => false,
        (Some(len), Some(size)) if len != size => false,
        (Some(len), _) => local_sha256(hashes, dest, progress, len)
            .await?
            .is_some_and(|sha| sha.starts_with(&wanted.to_ascii_lowercase())),
    };
    if matches {
        return Ok(true);
    }
    // A map the launcher did not install - a stock map, or one the player put
    // there - is never replaced on a server's say-so; a mismatch just means the
    // server runs another build of it.
    if local_len.is_some() && item.kind == "map" && managed.is_none() {
        eprintln!(
            "[content] Keeping the local map {}: the server runs a different build of it",
            item.filename
        );
        return Ok(true);
    }
    Ok(false)
}

/// What progress events call an item.
fn display_name_for(kind: &str, filename: &str) -> String {
    if kind == "map" {
        format!("Map: {}", filename)
    } else {
        filename.to_string()
    }
}

/// SHA-256 of an installed file (None if it is absent), computed off the async runtime.
async fn local_sha256(
    hashes: &Arc<Mutex<HashCache>>,
    path: &Path,
    progress: &Arc<dyn Fn(u64, u64) + Send + Sync>,
    total: u64,
) -> Result<Option<String>, String> {
    let (hashes, path, progress) = (hashes.clone(), path.to_path_buf(), progress.clone());
    tokio::task::spawn_blocking(move || {
        hashes
            .lock()
            .unwrap()
            .sha256(&path, |read| progress(read, total))
    })
    .await
    .map_err(|e| format!("Hash task failed: {}", e))?
}

/// Download and install everything in `items` that is missing or out of date.
///
/// Shared by the launcher UI and the in-game bridge; the two differ only in
/// where progress is emitted and in what happens afterwards (the bridge does
/// not connect — the running game does that itself).
///
/// `plan_out`, when supplied, is handed each item's download size (0 for an item
/// already installed at the right version) before the first byte moves. That
/// lets a caller weight a single progress bar by bytes instead of item count,
/// which matters because content items differ in size by orders of magnitude.
async fn install_items<E>(
    items: &[ManifestItem],
    game_dir: &Path,
    channel: &str,
    window: &E,
    cancel: Option<Arc<AtomicBool>>,
    // Send + Sync because this future is spawned onto the async runtime and the
    // callback is held across awaits.
    plan_out: Option<&(dyn Fn(&[u64]) + Send + Sync)>,
) -> Result<(), String>
where
    E: Emitter<tauri::Wry> + Clone + Send + Sync + 'static,
{
    let _one_at_a_time = INSTALLING.lock().await;
    let addons_dir = game_dir.join("citadel").join("deadworks_addons").join("vpks");
    let maps_dir = game_dir.join("citadel").join("maps");
    ensure_dir(&addons_dir)?;
    ensure_dir(&maps_dir)?;

    let mut state = load_versions(game_dir);
    let hashes = Arc::new(Mutex::new(HashCache::load(game_dir)));
    let total_items = items.len();

    // Decide up front what actually needs fetching, so a caller can size its
    // progress bar before anything is downloaded.
    let mut sizes: Vec<u64> = Vec::with_capacity(total_items);
    for (idx, item) in items.iter().enumerate() {
        let dest_vpk = target_dir_for(&item.kind, game_dir)?.join(format!("{}.vpk", item.filename));

        // Verifying reads the whole file, which for a map is gigabytes, so report it rather than
        // leaving the dialog silent on "Checking server content".
        let display_name = display_name_for(&item.kind, &item.filename);
        let emitter = window.clone();
        let chan = channel.to_string();
        let progress: Arc<dyn Fn(u64, u64) + Send + Sync> = Arc::new(move |read, total| {
            let _ = emitter.emit(
                chan.as_str(),
                DownloadProgress {
                    name: display_name.clone(),
                    status: "checking".into(),
                    bytes_downloaded: read,
                    total_bytes: total,
                    item_index: idx,
                    total_items,
                },
            );
        });
        progress(0, 0);

        let current = already_current(item, &dest_vpk, &state, &hashes, &progress).await?;
        // Weighted by download size where the API gave one, else by the size a
        // server advertised - every item in one install comes from the same
        // source, so either keeps them in proportion. 0 marks "nothing to do";
        // anything real counts at least 1 byte so it still advances the bar.
        let weight = if item.compressed_size > 0 {
            item.compressed_size
        } else {
            item.size.unwrap_or(0)
        };
        sizes.push(if current { 0 } else { weight.max(1) });
    }
    // Checking may have hashed files; keep that work for next time.
    hashes.lock().unwrap().save();
    // Every file is capped on its own; this caps what one server can ask for in total.
    let asked: u64 = sizes.iter().fold(0, |sum, size| sum.saturating_add(*size));
    if asked > MAX_JOIN_BYTES {
        return Err(format!(
            "this server asks for {} GiB of downloads, more than the launcher will fetch for one join",
            asked / (1024 * 1024 * 1024)
        ));
    }
    if let Some(f) = plan_out {
        f(&sizes);
    }

    // The sizes above are the server's word, and an entry may give none. What is written is
    // counted as it lands, so the cap holds whatever was advertised.
    let mut budget = MAX_JOIN_BYTES;

    for (idx, item) in items.iter().enumerate() {
        if cancelled(&cancel) {
            return Err(CANCELLED_MSG.into());
        }
        let target_dir = target_dir_for(&item.kind, game_dir)?;
        let vpk_filename = format!("{}.vpk", item.filename);
        let dest_vpk = target_dir.join(&vpk_filename);

        let display_name = display_name_for(&item.kind, &item.filename);

        if sizes[idx] == 0 {
            let _ = window.emit(
                channel,
                DownloadProgress {
                    name: display_name.clone(),
                    status: "ready".into(),
                    bytes_downloaded: item.compressed_size,
                    total_bytes: item.compressed_size,
                    item_index: idx,
                    total_items,
                },
            );
            continue;
        }

        let _ = window.emit(
            channel,
            DownloadProgress {
                name: display_name.clone(),
                status: "checking".into(),
                bytes_downloaded: 0,
                total_bytes: item.compressed_size,
                item_index: idx,
                total_items,
            },
        );

        let sha = download_and_decompress(
            &item.download_url,
            &dest_vpk,
            &display_name,
            idx,
            total_items,
            item.size.unwrap_or(item.compressed_size.saturating_mul(3)),
            item.expected(),
            channel,
            window,
            cancel.clone(),
            item.local_source,
            MAX_VPK_BYTES.min(budget),
        )
        .await?;
        budget = budget.saturating_sub(std::fs::metadata(&dest_vpk).map(|m| m.len()).unwrap_or(0));
        if budget == 0 && items[idx + 1..].iter().zip(&sizes[idx + 1..]).any(|(_, size)| *size > 0) {
            return Err(format!(
                "this server's content is more than the {} GiB the launcher will install for one join",
                MAX_JOIN_BYTES / (1024 * 1024 * 1024)
            ));
        }

        {
            let mut hashes = hashes.lock().unwrap();
            hashes.record(&dest_vpk, &sha);
            hashes.save();
        }
        state.record(
            &item.filename,
            VersionEntry {
                kind: item.kind.clone(),
                version: item.version,
                sha256: Some(sha),
            },
        );
        save_versions(game_dir, &state)?;

        let _ = window.emit(
            channel,
            DownloadProgress {
                name: display_name.clone(),
                status: "ready".into(),
                bytes_downloaded: item.compressed_size,
                total_bytes: item.compressed_size,
                item_index: idx,
                total_items,
            },
        );
    }

    Ok(())
}

/// What a finished install has to say for itself.
pub(crate) struct Installed {
    /// The server runs more content than it could list, so not all of it was installed.
    pub incomplete: bool,
}

/// In-game bridge entry point: prepare a server's content given only its
/// `ip:port`, and stop there.
///
/// Deliberately does NOT connect and does NOT surface a window. The game is
/// already running and issues its own `connect` once this reports ready — that
/// is what makes a join from the in-game browser silent, where the launcher's
/// own path ends in `steam://connect` and hands off to Steam.
///
/// The bootstrap gate is skipped on purpose: the addon making this request is,
/// by definition, a mounted and running bootstrap, and a staged update cannot be
/// applied to the live game anyway. Version skew is caught by the bridge's own
/// protocol handshake instead.
pub(crate) async fn install_for_address(
    app: &tauri::AppHandle,
    addr: &str,
    channel: &str,
    cancel: Arc<AtomicBool>,
    plan_out: &(dyn Fn(&[u64]) + Send + Sync),
    strictness: Strictness,
) -> Result<Installed, String> {
    let api_url = resolve_api_url(app);

    let record = ApiRecord::Address(addr);
    let Resolved { items, source, incomplete } = resolve_items(addr, &api_url, Some(&record), strictness).await?;
    match install_resolved(app, &items, channel, cancel.clone(), plan_out).await {
        // As in `prepare`: the API's record only if it covers what the server advertised.
        Err(e) if source == Source::Advertised && can_fall_back(&e) => {
            let Ok(game_dir) = find_game_dir() else {
                return Err(e);
            };
            let Some(items) = api_stand_in(Some(&record), &api_url, &items, &game_dir).await else {
                return Err(e);
            };
            eprintln!("[content] {e}; falling back to the Deadworks API");
            install_resolved(app, &items, channel, cancel, plan_out)
                .await
                .map(|()| Installed { incomplete: false })
                .map_err(|second| {
                    eprintln!("[content] the Deadworks API's copy did not work either: {second}");
                    e
                })
        }
        other => other.map(|()| Installed { incomplete }),
    }
}

/// `install_for_address` for one resolved list of items.
async fn install_resolved(
    app: &tauri::AppHandle,
    items: &[ManifestItem],
    channel: &str,
    cancel: Arc<AtomicBool>,
    plan_out: &(dyn Fn(&[u64]) + Send + Sync),
) -> Result<(), String> {
    for item in items {
        validate_filename(&item.filename)?;
    }

    if items.is_empty() {
        plan_out(&[]);
        return Ok(());
    }

    let game_dir = find_game_dir()?;

    // Addons only mount through the addonroot entry, and the running game read
    // gameinfo.gi at startup — so if it is missing now, nothing we download can
    // appear this session. Patch it for next time and say so plainly.
    if items.iter().any(|i| i.kind == "addon") {
        let live_ok = crate::gameinfo::status(&game_dir)
            .map(|s| s.has_addonroot)
            .unwrap_or(false);
        if !live_ok {
            let _ = crate::gameinfo::ensure_patched(&game_dir);
            return Err(
                "gameinfo.gi was missing the addonroot entry. It has been repaired, but \
                 Deadlock must be restarted before this server's addons can load."
                    .into(),
            );
        }
    }

    install_items(items, &game_dir, channel, app, Some(cancel), Some(plan_out)).await
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_filename_with_separators() {
        assert!(validate_filename("a/b").is_err());
        assert!(validate_filename("a\\b").is_err());
        assert!(validate_filename("../etc/passwd").is_err());
        assert!(validate_filename("C:\\Windows\\foo").is_err());
    }

    #[test]
    fn rejects_filename_dots() {
        assert!(validate_filename(".").is_err());
        assert!(validate_filename("..").is_err());
    }

    #[test]
    fn rejects_empty_or_oversized_filename() {
        assert!(validate_filename("").is_err());
        assert!(validate_filename(&"a".repeat(129)).is_err());
    }

    #[test]
    fn accepts_plain_filename() {
        assert!(validate_filename("my_map_v2").is_ok());
        assert!(validate_filename("addon-1.2.3").is_ok());
    }

    #[test]
    fn rejects_windows_device_names_and_control_characters() {
        for bad in ["con", "NUL", "com1", "lpt9.old", "tab\there", "nul\0"] {
            assert!(validate_filename(bad).is_err(), "{bad:?}");
        }
        assert!(validate_filename("console").is_ok());
    }

    fn advertised(pairs: &[(&str, &str)]) -> rules::Advertised {
        let reply = pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect();
        rules::parse(&reply).unwrap()
    }

    #[test]
    fn advertised_content_comes_from_the_servers_host_checked_by_hash() {
        let adv = advertised(&[
            ("dw_ver", "1"),
            ("dw_addons", "turbo:9330149c74886724:11965,ware"),
            ("dw_maps", "dl_midtown:faf73184b902d52d:1053579891"),
            ("dw_fastdl", "https://dl.example.com/dw"),
        ]);
        let items = items_from_advertised(&adv, "https://api.deadworks.net", Strictness::Exact, false);
        // "ware" has no hash, so there is nothing to fetch it by or check it against.
        assert_eq!(items.len(), 2);
        assert_eq!(items[0].download_url, "https://dl.example.com/dw/addons/turbo_9330149c74886724.vpk.bz2");
        assert_eq!(
            items[0].expected(),
            Some(Expected { sha256: "9330149c74886724".into(), size: Some(11965), enforce: true })
        );
        assert_eq!(items[1].kind, "map");
        assert_eq!(items[1].download_url, "https://dl.example.com/dw/maps/dl_midtown_faf73184b902d52d.vpk.bz2");
        assert_eq!(items[1].size, Some(1053579891));
    }

    #[test]
    fn advertised_content_without_a_host_comes_from_deadworks() {
        let adv = advertised(&[("dw_ver", "1"), ("dw_addons", "turbo:9330149c74886724")]);
        let items = items_from_advertised(&adv, "https://api.deadworks.net/", Strictness::Exact, false);
        assert_eq!(items[0].download_url, "https://api.deadworks.net/fastdl/addons/turbo_9330149c74886724.vpk.bz2");
    }

    /// CONNECT ANYWAY takes the same list from the same host - only the hash stops being
    /// a reason to throw the download away.
    #[test]
    fn accepting_a_mismatch_changes_nothing_but_whether_the_hash_is_enforced() {
        let adv = advertised(&[("dw_ver", "1"), ("dw_addons", "turbo:9330149c74886724:11965")]);
        let strict = items_from_advertised(&adv, "https://api.deadworks.net", Strictness::Exact, false);
        let lenient = items_from_advertised(&adv, "https://api.deadworks.net", Strictness::AcceptMismatch, true);

        assert_eq!(strict[0].download_url, lenient[0].download_url);
        assert_eq!(
            lenient[0].expected(),
            Some(Expected { sha256: "9330149c74886724".into(), size: Some(11965), enforce: false })
        );
    }

    /// A mismatch is its own code, because the in-game UI offers a way through it and
    /// must not word it as corruption.
    #[test]
    fn a_stale_download_host_is_told_apart_from_a_corrupt_download() {
        assert_eq!(
            error_code("downloaded turbo does not match the version the server runs (got a, expected b)"),
            ERR_CONTENT_MISMATCH
        );
        assert_eq!(error_code("compressed payload for turbo exceeds maximum size (4294967296 bytes)"), 4);
        // A download host that is missing the file or is down is not "server offline" or "API down".
        assert_eq!(error_code("Download failed for turbo: HTTP 404 Not Found"), ERR_DOWNLOAD_HOST);
        assert_eq!(error_code("Download request failed for turbo: error sending request"), ERR_DOWNLOAD_HOST);
        assert_eq!(error_code("No online server found at that address (HTTP 404)"), 1);
        assert_eq!(error_code("API returned HTTP 404 Not Found"), 1);
        assert_eq!(error_code("API returned HTTP 502 Bad Gateway"), 2);
        // A file's name cannot steer the classification.
        assert_eq!(
            error_code("downloaded bootstrap_gameinfo.gi does not match the version the server runs (got a, expected b)"),
            ERR_CONTENT_MISMATCH
        );
        assert_eq!(error_code("Download failed for bootstrap: HTTP 404 Not Found"), ERR_DOWNLOAD_HOST);
        assert_eq!(error_code("Failed to read VPK magic: failed to fill whole buffer"), 4);
        assert_eq!(error_code("this server asks for 40 GiB of downloads, more than the launcher will fetch for one join"), ERR_TOO_MUCH_CONTENT);
        assert_eq!(error_code("this server's content is more than the 16 GiB the launcher will install for one join"), ERR_TOO_MUCH_CONTENT);
        assert_eq!(error_code("decompressed payload for turbo is not a valid VPK (magic mismatch)"), 4);
    }

    fn item(filename: &str, kind: &str) -> ManifestItem {
        ManifestItem {
            filename: filename.into(),
            kind: kind.into(),
            version: 0,
            compressed_size: 0,
            download_url: String::new(),
            sha256: None,
            size: None,
            accept_mismatch: false,
            local_source: false,
        }
    }

    /// Nothing changes for a server until it names a download host of its own.
    #[test]
    fn a_server_takes_over_from_the_api_by_naming_its_own_host() {
        let hosted_by_deadworks = advertised(&[("dw_ver", "1"), ("dw_addons", "turbo:9330149c74886724")]);
        let self_hosted = advertised(&[
            ("dw_ver", "1"),
            ("dw_addons", "turbo:9330149c74886724"),
            ("dw_fastdl", "https://dl.example.com"),
        ]);
        assert_eq!(preferred_source(None, true), Some(Source::Api));
        assert_eq!(preferred_source(Some(&hosted_by_deadworks), true), Some(Source::Api));
        assert_eq!(preferred_source(Some(&self_hosted), true), Some(Source::Advertised));
        // With no record there is only the server's word, whoever hosts the files.
        assert_eq!(preferred_source(Some(&hosted_by_deadworks), false), Some(Source::Advertised));
        assert_eq!(preferred_source(Some(&self_hosted), false), Some(Source::Advertised));
        assert_eq!(preferred_source(None, false), None);
    }

    #[test]
    fn the_api_record_only_stands_in_when_it_names_what_was_advertised() {
        let game_dir = std::env::temp_dir().join(format!("dw-covers-{}", std::process::id()));
        let maps = game_dir.join("citadel").join("maps");
        std::fs::create_dir_all(&maps).unwrap();
        std::fs::write(maps.join("dl_midtown.vpk"), b"stock").unwrap();
        std::fs::write(maps.join("ware_arena.vpk"), b"ours").unwrap();
        let mut state = VersionsState::default();
        state.record("ware_arena", VersionEntry { kind: "map".into(), ..Default::default() });

        let covers = |api: &[ManifestItem], advertised: &[ManifestItem]| api_covers(api, advertised, &game_dir, &state);
        let advertised = [item("turbo", "addon"), item("dl_midtown", "map")];

        // The stock map is on disk and not ours to replace, so the record need not name it.
        assert!(covers(&[item("turbo", "addon")], &advertised));
        // A server that hosts its own content has nothing uploaded: no stand-in, no silent join.
        assert!(!covers(&[], &advertised));
        // Same name, different kind is a different file.
        assert!(!covers(&[item("turbo", "map")], &advertised));
        // A map the launcher installed is fetched like any other content, so it must be named.
        assert!(!covers(&[], &[item("ware_arena", "map")]));
        assert!(covers(&[item("ware_arena", "map")], &[item("ware_arena", "map")]));

        std::fs::remove_dir_all(&game_dir).unwrap();
    }

    #[test]
    fn only_source_specific_failures_fall_back_to_the_api() {
        assert!(can_fall_back("Download failed for turbo: HTTP 404 Not Found"));
        assert!(can_fall_back("downloaded turbo does not match the version the server runs (got a, expected b)"));
        assert!(!can_fall_back(CANCELLED_MSG));
        assert!(!can_fall_back("FILE_IN_USE: turbo.vpk is currently loaded by Deadlock."));
        assert!(!can_fall_back("Could not prepare gameinfo.gi: access denied"));
    }

}
