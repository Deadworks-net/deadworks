//! Bootstrap addon: a single global VPK that must be on disk *before* the game
//! starts, unlike per-server content in [`crate::addons`] which is fetched at
//! connect time and mounted at runtime.
//!
//! It lives in `citadel/deadworks_mods/`, which [`crate::gameinfo`] puts on the
//! engine's startup search path. That placement is what makes it work for
//! Panorama, and also what makes it awkward: the engine holds the file open for
//! the entire session, so it cannot be replaced while the game runs.
//!
//! # How updates land
//!
//! The installed file must be named `pak01_dir.vpk` — that is the only name the
//! engine registers from a search-path directory, so the live name is fixed and
//! cannot be versioned. A new version is downloaded to `bootstrap.pending`
//! instead, which the game never has open, so that step always succeeds.
//! Installing is then a single `rename` onto the live name:
//!
//!   * Game not running → the rename succeeds and the update is live.
//!   * Game running → the rename fails with a sharing violation and changes
//!     nothing. The `.pending` file stays put and is retried later.
//!
//! Because `MoveFileEx(REPLACE_EXISTING)` is all-or-nothing, there is no state
//! where a half-written VPK sits on a search path.
//!
//! Note this needs no game-process detection: attempting the rename *is* the
//! test for whether the file is free, and it is one cheap syscall to retry.
//!
//! # Why callers must await, not race
//!
//! Launching goes through `steam://`, which is fire-and-forget — the moment we
//! hand off we cannot hold the game back. If a download were still in flight
//! the swap and the game's mount would race over a few seconds, and whether an
//! update applied would be a coin flip. [`ensure`] therefore serialises on a
//! global lock: a second caller waits for the in-flight run to finish and then
//! observes its result, rather than starting a competing one.

use std::path::{Path, PathBuf};
use std::time::Duration;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tauri::{AppHandle, Emitter};
use tokio::sync::Mutex;

/// Progress channel, kept distinct from the connect dialog's `download-progress`
/// so background polling never drives the connect UI.
pub const PROGRESS_EVENT: &str = "bootstrap-progress";

/// Status channel. Separate from [`PROGRESS_EVENT`] because that one carries
/// per-chunk download progress, and a listener would otherwise have to tell two
/// unrelated payload shapes apart.
pub const STATUS_EVENT: &str = "bootstrap-status";

/// The engine only registers a VPK in a search-path directory under the
/// `pak01_dir.vpk` name, so the installed file is named for the engine, not for
/// us. Nothing else in this directory may carry a `.vpk` extension.
const LIVE_FILE: &str = "pak01_dir.vpk";

/// Staging name for a downloaded update. Deliberately has no `.vpk` extension
/// at all, so it cannot be picked up however the engine enumerates the
/// directory, and the game never holds it open — which is what makes writing it
/// always succeed even mid-session.
const PENDING_FILE: &str = "bootstrap.pending";

/// Prefix for the transient files `download_and_decompress` creates beside the
/// staging file. All of them end in `.part`.
const TEMP_PREFIX: &str = "bootstrap.";

const STATE_FILE: &str = ".deadworks_bootstrap.json";

/// Poll interval when everything is current.
const IDLE_POLL: Duration = Duration::from_secs(15 * 60);
/// Poll interval while an update is waiting for the game to close. One failed
/// `rename` per tick, so this can be cheap and frequent.
const PENDING_POLL: Duration = Duration::from_secs(30);

/// In-process attempts to install a freshly downloaded update before giving up
/// and leaving it pending. Covers the common "quit the game, immediately hit
/// Launch" case, where the handle takes a moment to drop.
const SWAP_ATTEMPTS: u32 = 4;
const SWAP_BACKOFF: Duration = Duration::from_millis(400);

fn ensure_lock() -> &'static Mutex<()> {
    static LOCK: std::sync::OnceLock<Mutex<()>> = std::sync::OnceLock::new();
    LOCK.get_or_init(|| Mutex::new(()))
}

// ── Types ──

#[derive(Debug, Clone, Deserialize)]
struct Manifest {
    version: u32,
    #[serde(default)]
    compressed_size: u64,
    sha256: String,
    download_url: String,
    /// Oldest install still allowed to connect. Global; server admins never set
    /// or see this.
    #[serde(default)]
    min_version: u32,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
struct State {
    #[serde(default)]
    installed_version: u32,
    #[serde(default)]
    installed_sha256: String,
    #[serde(default)]
    pending_version: u32,
    #[serde(default)]
    pending_sha256: String,
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct Status {
    /// What the game will actually mount at its next start.
    pub installed_version: u32,
    /// Downloaded and verified, waiting for the game to release the live file.
    pub pending_version: u32,
    /// Latest published version; 0 when the manifest could not be fetched.
    pub latest_version: u32,
    /// Oldest install allowed to connect; 0 when unknown.
    pub min_version: u32,
    /// An update is staged and only a game restart is standing in its way.
    pub restart_required: bool,
}

// ── Paths ──

fn mods_dir(game_dir: &Path) -> PathBuf {
    game_dir.join("citadel").join("deadworks_mods")
}

fn live_path(dir: &Path) -> PathBuf {
    dir.join(LIVE_FILE)
}

fn pending_path(dir: &Path) -> PathBuf {
    dir.join(PENDING_FILE)
}

// ── State file ──

fn load_state(dir: &Path) -> State {
    std::fs::read(dir.join(STATE_FILE))
        .ok()
        .and_then(|b| serde_json::from_slice(&b).ok())
        .unwrap_or_default()
}

fn save_state(dir: &Path, state: &State) {
    let path = dir.join(STATE_FILE);
    match serde_json::to_vec_pretty(state) {
        Ok(bytes) => {
            if let Err(e) = std::fs::write(&path, bytes) {
                println!("[bootstrap] could not write {}: {}", path.display(), e);
            }
        }
        Err(e) => println!("[bootstrap] could not serialise state: {}", e),
    }
}

fn status_of(state: &State, latest: u32, min: u32) -> Status {
    Status {
        installed_version: state.installed_version,
        pending_version: state.pending_version,
        latest_version: latest,
        min_version: min,
        restart_required: state.pending_version > state.installed_version,
    }
}

/// Announce a status and hand it back, so every exit from [`ensure`] tells the
/// UI what it settled on. The clearing case matters as much as the pending one:
/// the restart modal has to close itself once a staged update finally lands.
fn publish(app: &AppHandle, status: Status) -> Status {
    let _ = app.emit(STATUS_EVENT, &status);
    status
}

// ── Install ──

/// Try to promote the staged update. Returns true when the swap happened.
///
/// A sharing violation here is the expected, benign outcome while the game is
/// running — not an error worth surfacing.
fn try_swap(dir: &Path, state: &mut State) -> bool {
    if state.pending_version == 0 {
        return false;
    }
    let pending = pending_path(dir);
    if !pending.exists() {
        // State claims a staged update that is no longer on disk.
        state.pending_version = 0;
        state.pending_sha256.clear();
        save_state(dir, state);
        return false;
    }

    match std::fs::rename(&pending, live_path(dir)) {
        Ok(()) => {
            state.installed_version = state.pending_version;
            state.installed_sha256 = std::mem::take(&mut state.pending_sha256);
            state.pending_version = 0;
            save_state(dir, state);
            println!("[bootstrap] installed v{}", state.installed_version);
            true
        }
        Err(e) if crate::gameinfo::is_sharing_violation(&e) => false,
        Err(e) => {
            println!("[bootstrap] swap failed: {}", e);
            false
        }
    }
}

async fn try_swap_with_retry(dir: &Path, state: &mut State) -> bool {
    for attempt in 0..SWAP_ATTEMPTS {
        if try_swap(dir, state) {
            return true;
        }
        if attempt + 1 < SWAP_ATTEMPTS {
            tokio::time::sleep(SWAP_BACKOFF).await;
        }
    }
    false
}

fn sha256_file(path: &Path) -> Result<String, String> {
    use std::io::Read;
    let file = std::fs::File::open(path)
        .map_err(|e| format!("Failed to open {} for hashing: {}", path.display(), e))?;
    let mut reader = std::io::BufReader::new(file);
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 256 * 1024];
    loop {
        let n = reader
            .read(&mut buf)
            .map_err(|e| format!("Read error while hashing: {}", e))?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(format!("{:x}", hasher.finalize()))
}

/// Leftovers from an interrupted download. None of these end in `.vpk`, so they
/// were never registered by the engine — this is tidiness, not correctness.
fn sweep_stray_parts(dir: &Path) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if name.starts_with(TEMP_PREFIX) && name.ends_with(".part") {
            let _ = std::fs::remove_file(entry.path());
        }
    }
}

/// `Ok(None)` means the API is reachable and reports that no bootstrap is
/// published. That is a legitimate state — an API deployed ahead of the first
/// publish, or the feature deliberately switched off — and is deliberately
/// distinguished from a transport failure, which is `Err`. Collapsing the two
/// would make a launcher released before the first publish unable to connect
/// anywhere.
async fn fetch_manifest(api_url: &str) -> Result<Option<Manifest>, String> {
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(15))
        .build()
        .map_err(|e| format!("Failed to build HTTP client: {}", e))?;
    let resp = client
        .get(format!("{}/api/bootstrap", api_url))
        .send()
        .await
        .map_err(|e| format!("request failed: {}", e))?;
    if resp.status() == reqwest::StatusCode::NOT_FOUND {
        return Ok(None);
    }
    if !resp.status().is_success() {
        return Err(format!("API returned HTTP {}", resp.status()));
    }
    resp.json::<Manifest>()
        .await
        .map(Some)
        .map_err(|e| format!("could not parse manifest: {}", e))
}

// ── Public API ──

/// Bring the bootstrap addon up to date, staging the update if the game holds
/// the live file.
///
/// `require_present` makes a missing bootstrap fatal. Pass it on paths that are
/// about to launch the game: a *stale* bootstrap is tolerable and must never
/// block a launch on a slow API, but having *none at all* means the feature is
/// simply absent, which is a different failure and worth stopping for.
pub async fn ensure(
    app: &AppHandle,
    game_dir: &Path,
    require_present: bool,
) -> Result<Status, String> {
    // Serialises against any run already in progress; the second caller falls
    // through to the up-to-date checks below and returns immediately.
    let _guard = ensure_lock().lock().await;

    let dir = mods_dir(game_dir);
    std::fs::create_dir_all(&dir)
        .map_err(|e| format!("Failed to create {}: {}", dir.display(), e))?;
    sweep_stray_parts(&dir);

    let mut state = load_state(&dir);

    // The game may have exited since the last attempt.
    try_swap(&dir, &mut state);

    let manifest = match fetch_manifest(&crate::addons::resolve_api_url(app)).await {
        Ok(Some(m)) => m,
        Ok(None) => {
            // Nothing published. There is nothing to install and nothing to
            // gate on, so this must never block a launch or a connect —
            // including on the very first release, before the first publish.
            return Ok(publish(app, status_of(&state, 0, 0)));
        }
        Err(e) => {
            if state.installed_version == 0 && require_present {
                return Err(format!(
                    "Could not reach the Deadworks API to install the bootstrap addon: {e}"
                ));
            }
            // Offline with something already installed: keep running on it.
            println!("[bootstrap] manifest unavailable ({e}); keeping installed version");
            return Ok(publish(app, status_of(&state, 0, 0)));
        }
    };

    let live = live_path(&dir);
    if state.installed_version == manifest.version && live.exists() {
        return Ok(publish(
            app,
            status_of(&state, manifest.version, manifest.min_version),
        ));
    }

    // Re-use an already-staged download of the same version rather than
    // re-fetching it every poll while the game is running.
    let pending = pending_path(&dir);
    let staged = state.pending_version == manifest.version && pending.exists();

    if !staged {
        let _ = std::fs::remove_file(&pending);
        crate::addons::download_and_decompress(
            &manifest.download_url,
            &pending,
            "Deadworks bootstrap",
            0,
            1,
            manifest.compressed_size.saturating_mul(3),
            PROGRESS_EVENT,
            app,
        )
        .await?;

        // The VPK magic check inside the download only proves it is a VPK.
        // This file is mounted into a search path the game runs UI code from,
        // so verify it is the exact artifact the manifest describes.
        let actual = sha256_file(&pending)?;
        if !actual.eq_ignore_ascii_case(&manifest.sha256) {
            let _ = std::fs::remove_file(&pending);
            return Err(
                "The bootstrap addon failed its integrity check and was discarded.".into(),
            );
        }

        state.pending_version = manifest.version;
        state.pending_sha256 = actual;
        save_state(&dir, &state);
    }

    try_swap_with_retry(&dir, &mut state).await;

    let status = status_of(&state, manifest.version, manifest.min_version);
    if status.restart_required {
        println!(
            "[bootstrap] v{} staged; waiting for Deadlock to close",
            status.pending_version
        );
    }
    Ok(publish(app, status))
}

/// Refuse to proceed when the version that will actually be mounted is too old.
///
/// A stale bootstrap cannot be fixed in place while the game is running, so the
/// only honest options are to block with an actionable message or to join with
/// the wrong build. This picks the former.
pub fn gate(status: &Status) -> Result<(), String> {
    if status.min_version == 0 || status.installed_version >= status.min_version {
        return Ok(());
    }
    if status.pending_version >= status.min_version {
        return Err(
            "A required Deadworks update is downloaded but Deadlock is still running. \
             Fully quit the game, then connect again."
                .into(),
        );
    }
    Err(format!(
        "Deadworks requires bootstrap v{} but v{} is installed. \
         Fully quit Deadlock and try again so the update can be applied.",
        status.min_version, status.installed_version
    ))
}

/// The on-disk picture with no network call, for the UI to surface a pending
/// "restart Deadlock to finish updating" state.
#[tauri::command]
pub fn bootstrap_status() -> Status {
    match crate::connect::resolve_game_dir() {
        Ok(game_dir) => status_of(&load_state(&mods_dir(&game_dir)), 0, 0),
        Err(_) => Status::default(),
    }
}

/// Install a staged update now, behind the restart modal's RETRY button.
///
/// No network: the pending file is already downloaded and hash-verified, so the
/// only thing in the way is the game's handle on the live file. A result that is
/// still `restart_required` therefore means Deadlock is still running — a state
/// for the UI to say out loud, not an error. Takes the same lock as [`ensure`]
/// so a click cannot race the poller mid-swap.
#[tauri::command]
pub async fn retry_bootstrap_install(app: AppHandle) -> Result<Status, String> {
    let _guard = ensure_lock().lock().await;

    let game_dir = crate::connect::resolve_game_dir()?;
    let dir = mods_dir(&game_dir);
    let mut state = load_state(&dir);
    try_swap_with_retry(&dir, &mut state).await;

    Ok(publish(&app, status_of(&state, 0, 0)))
}

/// Background keeper: checks at startup and then on an interval, and retries a
/// staged swap often enough to land shortly after the game exits.
pub fn spawn_poller(app: AppHandle) {
    tauri::async_runtime::spawn(async move {
        loop {
            let delay = match crate::connect::resolve_game_dir() {
                Ok(game_dir) => match ensure(&app, &game_dir, false).await {
                    Ok(status) if status.restart_required => PENDING_POLL,
                    Ok(_) => IDLE_POLL,
                    Err(e) => {
                        println!("[bootstrap] check failed: {}", e);
                        PENDING_POLL
                    }
                },
                // Deadlock not installed (or not detected yet) — nothing to do,
                // but keep checking in case it appears.
                Err(_) => IDLE_POLL,
            };
            tokio::time::sleep(delay).await;
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dir() -> PathBuf {
        let base = std::env::temp_dir().join(format!("dw-bootstrap-test-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&base).unwrap();
        base
    }

    #[test]
    fn swap_installs_and_clears_pending() {
        let d = dir();
        std::fs::write(pending_path(&d), b"VPK-ish").unwrap();
        let mut state = State {
            pending_version: 3,
            pending_sha256: "abc".into(),
            ..Default::default()
        };

        assert!(try_swap(&d, &mut state));
        assert_eq!(state.installed_version, 3);
        assert_eq!(state.installed_sha256, "abc");
        assert_eq!(state.pending_version, 0);
        assert!(live_path(&d).exists());
        assert!(!pending_path(&d).exists());
    }

    #[test]
    fn swap_is_a_noop_without_a_pending_file() {
        let d = dir();
        let mut state = State {
            pending_version: 3,
            ..Default::default()
        };
        assert!(!try_swap(&d, &mut state));
        // Stale claim is cleared so we re-download rather than waiting forever.
        assert_eq!(state.pending_version, 0);
        assert!(!live_path(&d).exists());
    }

    #[test]
    fn gate_allows_when_installed_meets_minimum() {
        let s = Status { installed_version: 5, min_version: 5, ..Default::default() };
        assert!(gate(&s).is_ok());
        let s = Status { installed_version: 9, min_version: 5, ..Default::default() };
        assert!(gate(&s).is_ok());
        // Unset minimum must never block.
        let s = Status { installed_version: 0, min_version: 0, ..Default::default() };
        assert!(gate(&s).is_ok());
    }

    #[test]
    fn gate_blocks_on_stale_install_and_says_why() {
        let s = Status { installed_version: 4, min_version: 5, ..Default::default() };
        let err = gate(&s).unwrap_err();
        assert!(err.contains("v5"), "should name the required version: {err}");

        // Staged but not applied: the fix is a restart, and the message says so.
        let s = Status { installed_version: 4, pending_version: 5, min_version: 5, ..Default::default() };
        let err = gate(&s).unwrap_err();
        assert!(err.contains("quit"), "should tell the user to quit: {err}");
    }

    #[test]
    fn restart_required_only_when_pending_is_newer() {
        let s = status_of(&State { installed_version: 4, pending_version: 5, ..Default::default() }, 5, 0);
        assert!(s.restart_required);
        let s = status_of(&State { installed_version: 5, ..Default::default() }, 5, 0);
        assert!(!s.restart_required);
    }

    #[test]
    fn installed_file_uses_the_only_name_the_engine_registers() {
        let d = dir();
        assert_eq!(live_path(&d).file_name().unwrap(), "pak01_dir.vpk");
    }

    #[test]
    fn no_transient_file_can_be_mistaken_for_the_live_one() {
        // Only `pak01_dir.vpk` may exist with a .vpk extension in this
        // directory. `download_and_decompress` derives its temp names from the
        // destination via `with_extension`, so check what it would actually
        // produce rather than trusting the constants.
        let d = dir();
        let pending = pending_path(&d);
        let transient = [
            pending.clone(),
            pending.with_extension("vpk.bz2.part"),
            pending.with_extension("vpk.part"),
        ];
        for path in transient {
            let name = path.file_name().unwrap().to_string_lossy().into_owned();
            assert!(!name.ends_with(".vpk"), "{name} would be seen as a VPK");
            assert_ne!(name, LIVE_FILE);
        }
    }

    #[test]
    fn sweep_removes_only_our_leftovers() {
        let d = dir();
        let leftover = pending_path(&d).with_extension("vpk.part");
        std::fs::write(&leftover, b"x").unwrap();
        std::fs::write(live_path(&d), b"x").unwrap();
        std::fs::write(d.join("someone_elses_mod.vpk"), b"x").unwrap();

        sweep_stray_parts(&d);

        assert!(!leftover.exists());
        assert!(live_path(&d).exists(), "must never delete the live VPK");
        assert!(d.join("someone_elses_mod.vpk").exists());
    }

    #[test]
    fn state_roundtrips_and_tolerates_missing_fields() {
        let d = dir();
        let state = State { installed_version: 7, installed_sha256: "ff".into(), ..Default::default() };
        save_state(&d, &state);
        assert_eq!(load_state(&d).installed_version, 7);

        std::fs::write(d.join(STATE_FILE), b"{}").unwrap();
        assert_eq!(load_state(&d).installed_version, 0);

        // Corrupt state must not panic or wedge the updater.
        std::fs::write(d.join(STATE_FILE), b"not json").unwrap();
        assert_eq!(load_state(&d).installed_version, 0);
    }
}
