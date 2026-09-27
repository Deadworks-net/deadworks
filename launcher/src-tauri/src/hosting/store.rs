//! On-disk layout of a hosting root and the state persisted in it.
//!
//! ```text
//! <root>\
//!   hosting.json            HostingState
//!   base.files.json         shipped files of the base install (path, size, sha1)
//!   base\game\...           the shared game install, read-only
//!   cache\deadworks\<tag>\  extracted Deadworks releases
//!   cache\dotnet\<ver>\     private .NET runtime
//!   cache\steamcmd\         SteamCMD, only when that source is used
//!   plugins\<id>\           plugin library
//!   servers\<id>\           server.json, logs\, content\{addons,maps}\, game\ (runnable tree)
//! ```

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use super::manifest::{parse_sha1_hex, sha1_hex, DepotFile};
use super::types::{BaseSource, CopyMode, ServerConfig};

pub const FOLDER_NAME: &str = "Deadworks Servers";

#[derive(Debug, Clone)]
pub struct Layout {
    pub root: PathBuf,
}

impl Layout {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }
    pub fn state_file(&self) -> PathBuf {
        self.root.join("hosting.json")
    }
    pub fn base_files_file(&self) -> PathBuf {
        self.root.join("base.files.json")
    }
    /// The install dir of the base: holds `game\...` like a Steam install.
    pub fn base_dir(&self) -> PathBuf {
        self.root.join("base")
    }
    pub fn base_game_dir(&self) -> PathBuf {
        self.base_dir().join("game")
    }
    pub fn deadworks_cache(&self) -> PathBuf {
        self.root.join("cache").join("deadworks")
    }
    pub fn dotnet_cache(&self) -> PathBuf {
        self.root.join("cache").join("dotnet")
    }
    pub fn steamcmd_dir(&self) -> PathBuf {
        self.root.join("cache").join("steamcmd")
    }
    pub fn downloads_dir(&self) -> PathBuf {
        self.root.join("cache").join("downloads")
    }
    pub fn plugins_dir(&self) -> PathBuf {
        self.root.join("plugins")
    }
    pub fn servers_dir(&self) -> PathBuf {
        self.root.join("servers")
    }
    pub fn server_dir(&self, id: &str) -> PathBuf {
        self.servers_dir().join(id)
    }
    pub fn server_file(&self, id: &str) -> PathBuf {
        self.server_dir(id).join("server.json")
    }
    pub fn server_logs(&self, id: &str) -> PathBuf {
        self.server_dir(id).join("logs")
    }
    pub fn server_content(&self, id: &str) -> PathBuf {
        self.server_dir(id).join("content")
    }
    /// The runnable tree's install dir; `game\bin\win64` lives under it.
    pub fn server_tree(&self, id: &str) -> PathBuf {
        self.server_dir(id)
    }
    pub fn server_bin(&self, id: &str) -> PathBuf {
        self.server_tree(id).join("game").join("bin").join("win64")
    }
    pub fn server_configs(&self, id: &str) -> PathBuf {
        self.server_bin(id).join("configs")
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BaseRecord {
    pub source: BaseSource,
    pub copy_mode: CopyMode,
    pub build_id: String,
    pub depots: Vec<(String, String)>,
    pub size_bytes: u64,
    #[serde(default)]
    pub modified_files: Vec<String>,
    /// Bumped whenever base files change, so server trees know to relink.
    #[serde(default)]
    pub generation: u64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostingState {
    #[serde(default)]
    pub version: u32,
    pub base: Option<BaseRecord>,
    pub deadworks_tag: Option<String>,
    pub deadworks_latest: Option<String>,
    pub dotnet_version: Option<String>,
    pub steam_username: Option<String>,
    pub last_check: Option<u64>,
    /// Game build a server last ran on for five minutes with the installed
    /// Deadworks, i.e. a combination known to work.
    pub last_good_build: Option<String>,
    /// Why servers can't start right now (unsupported game build).
    pub hold_reason: Option<String>,
    /// Build the hold applies to; a newer Deadworks release lifts it.
    pub hold_deadworks: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct StoredFile {
    p: String,
    s: u64,
    h: String,
}

pub fn load_state(layout: &Layout) -> HostingState {
    read_json(&layout.state_file()).unwrap_or_default()
}

pub fn save_state(layout: &Layout, state: &HostingState) -> Result<(), String> {
    write_json(&layout.state_file(), state)
}

pub fn load_base_files(layout: &Layout) -> Vec<DepotFile> {
    let stored: Vec<StoredFile> = read_json(&layout.base_files_file()).unwrap_or_default();
    stored
        .into_iter()
        .filter_map(|f| Some(DepotFile { path: f.p, size: f.s, sha1: parse_sha1_hex(&f.h)? }))
        .collect()
}

pub fn save_base_files(layout: &Layout, files: &[DepotFile]) -> Result<(), String> {
    let stored: Vec<StoredFile> =
        files.iter().map(|f| StoredFile { p: f.path.clone(), s: f.size, h: sha1_hex(&f.sha1) }).collect();
    write_json(&layout.base_files_file(), &stored)
}

/// What `server.json` holds: the profile plus bookkeeping the UI never edits.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerFile {
    #[serde(flatten)]
    pub config: ServerConfig,
    /// The SDR address the user last copied; a different live one means they
    /// have to share it again.
    #[serde(default)]
    pub last_shared_sdr_id: Option<String>,
    #[serde(default)]
    pub created_at: u64,
}

pub fn load_servers(layout: &Layout) -> Vec<ServerFile> {
    let Ok(rd) = std::fs::read_dir(layout.servers_dir()) else { return Vec::new() };
    let mut out: Vec<ServerFile> = rd
        .flatten()
        .filter_map(|e| read_json::<ServerFile>(&e.path().join("server.json")).ok())
        .collect();
    out.sort_by(|a, b| a.created_at.cmp(&b.created_at).then_with(|| a.config.name.cmp(&b.config.name)));
    out
}

pub fn save_server(layout: &Layout, server: &ServerFile) -> Result<(), String> {
    write_json(&layout.server_file(&server.config.id), server)
}

pub fn read_json<T: serde::de::DeserializeOwned>(path: &Path) -> Result<T, String> {
    let text = std::fs::read_to_string(path).map_err(|e| e.to_string())?;
    serde_json::from_str(&text).map_err(|e| format!("{}: {e}", path.display()))
}

pub fn write_json<T: Serialize>(path: &Path, value: &T) -> Result<(), String> {
    let data = serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("Couldn't create {}: {e}", parent.display()))?;
    }
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, data).map_err(|e| format!("Couldn't write {}: {e}", tmp.display()))?;
    std::fs::rename(&tmp, path).map_err(|e| format!("Couldn't write {}: {e}", path.display()))
}

pub fn now_secs() -> u64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}
