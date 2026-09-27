//! Wire types for the HOST tab. Each one mirrors an interface in
//! `src/lib/hosting.ts`; keep the two in step.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BaseSource {
    Client,
    Steamcmd,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CopyMode {
    Copy,
    Hardlink,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveInfo {
    pub root: String,
    pub free_bytes: u64,
    pub total_bytes: u64,
    pub same_as_client: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SetupCheck {
    pub client_game_dir: Option<String>,
    pub client_build_id: Option<String>,
    pub client_updating: bool,
    pub required_bytes: u64,
    pub drives: Vec<DriveInfo>,
    pub suggested_root: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallOptions {
    pub root: String,
    pub source: BaseSource,
    pub copy_mode: CopyMode,
    pub steam_username: Option<String>,
    pub steam_password: Option<String>,
    #[serde(default)]
    pub allow_modified: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BaseInfo {
    pub source: BaseSource,
    pub copy_mode: CopyMode,
    pub build_id: String,
    pub size_bytes: u64,
    pub modified_files: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum TaskKind {
    Install,
    Update,
    Verify,
    Uninstall,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SteamPrompt {
    Password,
    GuardCode,
    MobileConfirm,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskProgress {
    pub kind: TaskKind,
    pub stage: String,
    pub label: String,
    pub bytes_done: u64,
    pub bytes_total: u64,
    pub files_done: u64,
    pub files_total: u64,
    pub steam_prompt: Option<SteamPrompt>,
    pub error: Option<String>,
    pub modified_files: Vec<String>,
    pub finished: bool,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateState {
    pub available_build_id: Option<String>,
    pub installed_build_id: Option<String>,
    pub deadworks_installed: Option<String>,
    pub deadworks_latest: Option<String>,
    pub pending: bool,
    pub hold_reason: Option<String>,
    pub last_check: Option<u64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HostingOverview {
    pub installed: bool,
    pub root: Option<String>,
    pub base: Option<BaseInfo>,
    pub dotnet_version: Option<String>,
    pub task: Option<TaskProgress>,
    pub updates: UpdateState,
    pub servers: Vec<ServerSummary>,
}

// ── Servers ──

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum NetworkMode {
    Sdr,
    PortForward,
    Lan,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CvarEntry {
    pub key: String,
    pub value: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerConfig {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub password: String,
    pub max_players: u32,
    pub map: String,
    pub port: u16,
    pub network: NetworkMode,
    #[serde(default)]
    pub listed: bool,
    #[serde(default)]
    pub cheats: bool,
    #[serde(default = "default_true")]
    pub hibernate_when_empty: bool,
    #[serde(default)]
    pub plugins: Vec<String>,
    #[serde(default)]
    pub cvars: Vec<CvarEntry>,
    #[serde(default)]
    pub launch_args: Vec<String>,
    #[serde(default)]
    pub content_addons: Vec<String>,
    #[serde(default)]
    pub extra_maps: Vec<String>,
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ServerState {
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
    WaitingForDeadworks,
    Updating,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerInfo {
    pub user_id: i32,
    pub slot: i32,
    pub steam_id64: String,
    pub name: String,
    pub ping_ms: u32,
    pub connected_seconds: u64,
    pub team: i32,
    pub hero: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Reachability {
    Unknown,
    Checking,
    Open,
    Closed,
    Error,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NetworkInfo {
    pub mode: NetworkMode,
    pub port: u16,
    pub lan_ips: Vec<String>,
    pub public_ip: Option<String>,
    pub reachability: Reachability,
    pub sdr_id: Option<String>,
    pub sdr_id_changed: bool,
    pub connect_command: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerRuntime {
    pub id: String,
    pub state: ServerState,
    pub pid: Option<u32>,
    pub started_at: Option<u64>,
    pub players: Vec<PlayerInfo>,
    pub cpu_percent: f32,
    pub memory_bytes: u64,
    pub message: Option<String>,
    pub exit_code: Option<i32>,
    pub network: NetworkInfo,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerSummary {
    pub config: ServerConfig,
    pub runtime: ServerRuntime,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum LineKind {
    Out,
    In,
    Sys,
}

#[derive(Debug, Clone, Serialize)]
pub struct ConsoleLine {
    pub seq: u64,
    pub kind: LineKind,
    pub text: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct ConsoleBatch {
    pub id: String,
    pub lines: Vec<ConsoleLine>,
}

// ── Plugins & content ──

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginEntry {
    pub id: String,
    pub files: Vec<String>,
    pub size_bytes: u64,
    pub imported_at: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginConfigFile {
    pub plugin_id: String,
    pub exists: bool,
    pub path: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ContentKind {
    Addon,
    Map,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ContentFile {
    pub file_name: String,
    pub kind: ContentKind,
    pub size_bytes: u64,
}
