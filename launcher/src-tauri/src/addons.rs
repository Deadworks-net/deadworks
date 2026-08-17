use std::collections::HashMap;
use std::io::Read as _;
use std::path::{Path, PathBuf};

use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use tauri::{Emitter, Manager};
use tokio::io::AsyncWriteExt;

use crate::gameinfo::is_sharing_violation;

const DEFAULT_API_URL: &str = match std::option_env!("DEADWORKS_API_URL") {
    Some(url) => url,
    None => "https://api.deadworks.net",
};

const VPK_MAGIC: [u8; 4] = [0x34, 0x12, 0xAA, 0x55]; // 0x55aa1234 LE

/// Hard cap on decompressed VPK size (4 GiB) to bound bz2-bomb damage.
const MAX_VPK_BYTES: u64 = 4 * 1024 * 1024 * 1024;

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
}

#[derive(Debug, Deserialize)]
struct ContentManifest {
    items: Vec<ManifestItem>,
}

#[derive(Debug, Default, Serialize, Deserialize, Clone)]
struct VersionEntry {
    kind: String,
    version: u64,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct VersionsState {
    #[serde(default)]
    managed: HashMap<String, VersionEntry>, // filename → entry
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
            '/' | '\\' | ':' | '\0' | '*' | '?' | '"' | '<' | '>' | '|' => {
                return Err(format!("filename contains reserved character: {}", name));
            }
            _ => {}
        }
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
    std::fs::write(&path, &bytes)
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

/// Download `.vpk.bz2` from `url` into `dest_vpk` as a fully decompressed `.vpk`.
/// Enforces `MAX_VPK_BYTES` during decompression so a malicious manifest cannot
/// mount a bz2 bomb. Uses a temp `.part` file beside the destination, then
/// atomic rename.
///
/// `channel` is the Tauri event name progress is emitted on, and `emitter` is
/// either a `Window` (server content, driven by a visible connect dialog) or an
/// `AppHandle` (the bootstrap poller, which runs with no window in the tray).
#[allow(clippy::too_many_arguments)]
pub(crate) async fn download_and_decompress<E>(
    url: &str,
    dest_vpk: &Path,
    item_name: &str,
    index: usize,
    total: usize,
    expected_uncompressed_hint: u64,
    channel: &str,
    window: &E,
) -> Result<(), String>
where
    E: Emitter<tauri::Wry> + Clone + Send + Sync + 'static,
{
    let response = reqwest::get(url)
        .await
        .map_err(|e| format!("Download request failed for {}: {}", item_name, e))?;
    if !response.status().is_success() {
        return Err(format!(
            "Download failed for {}: HTTP {}",
            item_name,
            response.status()
        ));
    }

    let total_compressed = response.content_length().unwrap_or(0);
    let bz2_tmp = dest_vpk.with_extension("vpk.bz2.part");

    // Stream compressed bytes to temp file
    {
        let mut file = tokio::fs::File::create(&bz2_tmp)
            .await
            .map_err(|e| format!("Failed to create temp file: {}", e))?;

        let mut stream = response.bytes_stream();
        let mut downloaded: u64 = 0;
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|e| format!("Download error for {}: {}", item_name, e))?;
            downloaded += chunk.len() as u64;
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

    // Decompress into a sibling .vpk.part file.
    let vpk_tmp = dest_vpk.with_extension("vpk.part");
    let bz2_tmp_clone = bz2_tmp.clone();
    let vpk_tmp_clone = vpk_tmp.clone();
    let name = item_name.to_string();
    let win = window.clone();
    let chan = channel.to_string();

    tokio::task::spawn_blocking(move || -> Result<(), String> {
        let input = std::fs::File::open(&bz2_tmp_clone)
            .map_err(|e| format!("Failed to open compressed temp: {}", e))?;
        let mut decoder = bzip2::read::BzDecoder::new(std::io::BufReader::new(input));
        let out_file = std::fs::File::create(&vpk_tmp_clone)
            .map_err(|e| format!("Failed to create {}: {}", vpk_tmp_clone.display(), e))?;
        let mut output = std::io::BufWriter::new(out_file);

        let mut buf = [0u8; 256 * 1024];
        let mut written: u64 = 0;
        loop {
            let n = decoder
                .read(&mut buf)
                .map_err(|e| format!("bz2 decompression failed for {}: {}", name, e))?;
            if n == 0 {
                break;
            }
            written += n as u64;
            if written > MAX_VPK_BYTES {
                return Err(format!(
                    "decompressed payload for {} exceeds maximum size ({} bytes)",
                    name, MAX_VPK_BYTES
                ));
            }
            std::io::Write::write_all(&mut output, &buf[..n])
                .map_err(|e| format!("Write error: {}", e))?;
            let _ = win.emit(
                chan.as_str(),
                DownloadProgress {
                    name: name.clone(),
                    status: "decompressing".into(),
                    bytes_downloaded: written,
                    total_bytes: expected_uncompressed_hint,
                    item_index: index,
                    total_items: total,
                },
            );
        }
        std::io::Write::flush(&mut output).map_err(|e| format!("Flush error: {}", e))?;
        Ok(())
    })
    .await
    .map_err(|e| format!("Decompress task failed: {}", e))??;

    let _ = std::fs::remove_file(&bz2_tmp);

    verify_vpk_magic(&vpk_tmp)?;

    // Atomic rename onto the canonical path. This is the step that can fail
    // with a sharing violation if the engine has the old file open.
    match std::fs::rename(&vpk_tmp, dest_vpk) {
        Ok(()) => Ok(()),
        Err(e) if is_sharing_violation(&e) => {
            let _ = std::fs::remove_file(&vpk_tmp);
            Err(format!(
                "FILE_IN_USE: {} is currently loaded by Deadlock. Please fully disconnect or quit the game and try again.",
                dest_vpk
                    .file_name()
                    .map(|n| n.to_string_lossy().to_string())
                    .unwrap_or_default()
            ))
        }
        Err(e) => {
            let _ = std::fs::remove_file(&vpk_tmp);
            Err(format!("Failed to install {}: {}", dest_vpk.display(), e))
        }
    }
}

// ── Main command ──

#[tauri::command]
pub async fn prepare_and_connect(
    window: tauri::Window,
    server_id: String,
    addr: String,
) -> Result<crate::connect::ConnectResult, String> {
    let api_url = resolve_api_url(window.app_handle());

    let _ = window.emit(
        "download-progress",
        serde_json::json!({ "name": "", "status": "fetching", "bytes_downloaded": 0, "total_bytes": 0, "item_index": 0, "total_items": 0 }),
    );

    let manifest = fetch_manifest(&api_url, &server_id).await?;

    // Validate every item up-front so a bad manifest is rejected before we
    // touch the filesystem.
    for item in &manifest.items {
        validate_filename(&item.filename)?;
    }

    let game_dir = find_game_dir()?;
    let has_addons = manifest.items.iter().any(|i| i.kind == "addon");

    // Re-assert our SearchPaths block before every connect. Deadlock Mod
    // Manager regenerates the whole block on each of its launches and Steam's
    // file verification restores the vanilla file, so a patch applied at
    // startup may already be gone.
    if let Err(e) = crate::gameinfo::ensure_patched(&game_dir) {
        // The write failed, but the entries may already be live from an
        // earlier run — only block the connect if what we need is missing.
        let usable = crate::gameinfo::status(&game_dir)
            .map(|s| s.has_mods_search_path && (!has_addons || s.has_addonroot))
            .unwrap_or(false);
        if !usable {
            return Err(format!("Could not prepare gameinfo.gi: {}", e));
        }
        eprintln!("[connect] gameinfo.gi not rewritten ({e}); existing entries are current");
    }

    // The bootstrap addon has to be on disk before the game starts, so this is
    // awaited here rather than left to the background poller. `require_present`
    // is on: joining with no bootstrap at all is a hard failure, unlike joining
    // with a merely-stale one.
    let bootstrap_status =
        crate::bootstrap::ensure(window.app_handle(), &game_dir, true).await?;
    // Gate on what will actually be mounted, not on what is staged — a
    // search-path VPK cannot be swapped under a running game.
    crate::bootstrap::gate(&bootstrap_status)?;

    if manifest.items.is_empty() {
        return crate::connect::connect_to_server_inner(&addr);
    }

    let addons_dir = game_dir.join("citadel").join("deadworks_addons").join("vpks");
    let maps_dir = game_dir.join("citadel").join("maps");
    ensure_dir(&addons_dir)?;
    ensure_dir(&maps_dir)?;

    let mut state = load_versions(&game_dir);
    let total_items = manifest.items.len();

    for (idx, item) in manifest.items.iter().enumerate() {
        let target_dir = target_dir_for(&item.kind, &game_dir)?;
        let vpk_filename = format!("{}.vpk", item.filename);
        let dest_vpk = target_dir.join(&vpk_filename);

        let display_name = if item.kind == "map" {
            format!("Map: {}", item.filename)
        } else {
            item.filename.clone()
        };

        // Skip if the file already exists and the local version matches.
        let already_current = dest_vpk.exists()
            && state
                .managed
                .get(&item.filename)
                .map(|e| e.version == item.version && e.kind == item.kind)
                .unwrap_or(false);

        if already_current {
            let _ = window.emit(
                "download-progress",
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
            "download-progress",
            DownloadProgress {
                name: display_name.clone(),
                status: "checking".into(),
                bytes_downloaded: 0,
                total_bytes: item.compressed_size,
                item_index: idx,
                total_items,
            },
        );

        download_and_decompress(
            &item.download_url,
            &dest_vpk,
            &display_name,
            idx,
            total_items,
            item.compressed_size.saturating_mul(3),
            "download-progress",
            &window,
        )
        .await?;

        state.managed.insert(
            item.filename.clone(),
            VersionEntry {
                kind: item.kind.clone(),
                version: item.version,
            },
        );
        save_versions(&game_dir, &state)?;

        let _ = window.emit(
            "download-progress",
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

    let _ = window.emit(
        "download-progress",
        serde_json::json!({ "name": "", "status": "connecting", "bytes_downloaded": 0, "total_bytes": 0, "item_index": 0, "total_items": 0 }),
    );

    crate::connect::connect_to_server_inner(&addr)
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

}
