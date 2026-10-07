//! Local dedicated-server hosting (the HOST tab).
//!
//! A shared base game install (copied from the user's Deadlock, or downloaded
//! with SteamCMD) feeds any number of server profiles; each runs Deadworks from
//! its own hardlinked tree with its own plugins, config and network mode.
//! See `launcher/HOSTING_PLAN.md` for the design and what was verified.

mod base;
mod cfg;
mod console;
mod content;
mod disk;
mod dotnet;
mod download;
mod firewall;
mod fsutil;
mod manager;
mod manifest;
mod netcfg;
mod plugins;
mod process;
mod pty;
// The console transport for servers under Wine; unused on Windows.
#[cfg_attr(windows, allow(dead_code))]
mod rcon;
mod release;
mod steamcmd;
mod store;
mod task;
mod tree;
mod types;
mod wine;

use types::*;

pub use manager::init;

/// Number of servers that are starting, running or stopping.
pub fn running_count() -> usize {
    manager::get().running_count()
}

/// Stop every server, waiting up to ~15 s. For quitting the launcher.
pub fn stop_all_blocking() {
    manager::get().stop_all_blocking();
}

/// Run blocking hosting work off the main thread.
async fn blocking<T: Send + 'static>(f: impl FnOnce() -> Result<T, String> + Send + 'static) -> Result<T, String> {
    tauri::async_runtime::spawn_blocking(f).await.map_err(|e| e.to_string())?
}

#[tauri::command]
pub async fn hosting_overview() -> Result<HostingOverview, String> {
    blocking(|| Ok(manager::get().overview())).await
}

#[tauri::command]
pub async fn hosting_setup_check() -> Result<SetupCheck, String> {
    blocking(|| Ok(manager::get().setup_check())).await
}

#[tauri::command]
pub async fn hosting_install(options: InstallOptions) -> Result<(), String> {
    blocking(move || manager::get().install(options)).await
}

#[tauri::command]
pub fn hosting_cancel_task() {
    manager::get().cancel_task();
}

#[tauri::command]
pub fn hosting_steamcmd_input(value: String) -> Result<(), String> {
    manager::get().steamcmd_input(value)
}

#[tauri::command]
pub async fn hosting_check_updates() -> Result<UpdateState, String> {
    blocking(|| {
        let state = manager::get().check_updates(true);
        Ok(state)
    })
    .await
}

#[tauri::command]
pub async fn hosting_apply_updates() -> Result<(), String> {
    blocking(|| manager::get().apply_updates()).await
}

#[tauri::command]
pub async fn hosting_verify() -> Result<(), String> {
    blocking(|| manager::get().verify()).await
}

#[tauri::command]
pub fn hosting_repair_client() -> Result<(), String> {
    open::that(format!("steam://validate/{}", manifest::DEADLOCK_APP_ID)).map_err(|e| format!("Couldn't open Steam: {e}"))
}

#[tauri::command]
pub async fn hosting_uninstall() -> Result<(), String> {
    blocking(|| manager::get().uninstall()).await
}

#[tauri::command]
pub async fn hosting_maps() -> Result<Vec<String>, String> {
    blocking(|| Ok(manager::get().maps())).await
}

#[tauri::command]
pub async fn hosting_create_server(name: String, network: NetworkMode) -> Result<ServerConfig, String> {
    blocking(move || manager::get().create_server(name, network)).await
}

#[tauri::command]
pub async fn hosting_update_server(config: ServerConfig) -> Result<ServerConfig, String> {
    blocking(move || manager::get().update_server(config)).await
}

#[tauri::command]
pub async fn hosting_duplicate_server(id: String) -> Result<ServerConfig, String> {
    blocking(move || manager::get().duplicate_server(&id)).await
}

#[tauri::command]
pub async fn hosting_delete_server(id: String) -> Result<(), String> {
    blocking(move || manager::get().delete_server(&id)).await
}

#[tauri::command]
pub async fn hosting_start(id: String) -> Result<(), String> {
    blocking(move || manager::get().start(&id)).await
}

#[tauri::command]
pub async fn hosting_stop(id: String) -> Result<(), String> {
    blocking(move || manager::get().stop(&id)).await
}

#[tauri::command]
pub async fn hosting_restart(id: String) -> Result<(), String> {
    blocking(move || manager::get().restart(&id)).await
}

#[tauri::command]
pub async fn hosting_runtime(id: String) -> Result<ServerRuntime, String> {
    blocking(move || manager::get().runtime(&id)).await
}

#[tauri::command]
pub async fn hosting_console_history(id: String) -> Result<Vec<ConsoleLine>, String> {
    blocking(move || manager::get().console_history(&id)).await
}

#[tauri::command]
pub async fn hosting_send_command(id: String, command: String) -> Result<(), String> {
    blocking(move || manager::get().send_command(&id, &command)).await
}

#[tauri::command]
pub async fn hosting_kick(id: String, slot: i32) -> Result<(), String> {
    blocking(move || manager::get().kick(&id, slot)).await
}

#[tauri::command]
pub async fn hosting_ban(id: String, steam_id64: String, minutes: u32, reason: String) -> Result<(), String> {
    blocking(move || manager::get().ban(&id, &steam_id64, minutes, &reason)).await
}

#[tauri::command]
pub async fn hosting_permissions(id: String) -> Result<PermissionsSnapshot, String> {
    blocking(move || manager::get().permissions(&id)).await
}

#[tauri::command]
pub async fn hosting_write_permissions(
    id: String,
    expected: std::collections::BTreeMap<String, Option<String>>,
    files: std::collections::BTreeMap<String, String>,
) -> Result<PermissionsWriteResult, String> {
    blocking(move || manager::get().write_permissions(&id, expected, files)).await
}

#[tauri::command]
pub async fn hosting_mark_shared(id: String) -> Result<(), String> {
    blocking(move || manager::get().mark_shared(&id)).await
}

#[tauri::command]
pub async fn hosting_check_reachability(id: String) -> Result<NetworkInfo, String> {
    blocking(move || manager::get().check_reachability(&id)).await
}

#[tauri::command]
pub async fn hosting_open_folder(id: Option<String>) -> Result<(), String> {
    blocking(move || manager::get().open_folder(id.as_deref())).await
}

#[tauri::command]
pub async fn hosting_plugin_library() -> Result<Vec<PluginEntry>, String> {
    blocking(|| Ok(manager::get().plugin_library())).await
}

#[tauri::command]
pub async fn hosting_import_plugins(paths: Vec<String>) -> Result<Vec<PluginEntry>, String> {
    blocking(move || manager::get().import_plugins(paths)).await
}

#[tauri::command]
pub async fn hosting_remove_plugin(plugin_id: String) -> Result<(), String> {
    blocking(move || manager::get().remove_plugin(&plugin_id)).await
}

#[tauri::command]
pub async fn hosting_set_plugin_enabled(id: String, plugin_id: String, enabled: bool) -> Result<ServerConfig, String> {
    blocking(move || manager::get().set_plugin_enabled(&id, &plugin_id, enabled)).await
}

#[tauri::command]
pub async fn hosting_plugin_configs(id: String) -> Result<Vec<PluginConfigFile>, String> {
    blocking(move || manager::get().plugin_configs(&id)).await
}

#[tauri::command]
pub async fn hosting_read_plugin_config(id: String, plugin_id: String) -> Result<String, String> {
    blocking(move || manager::get().read_plugin_config(&id, &plugin_id)).await
}

#[tauri::command]
pub async fn hosting_write_plugin_config(id: String, plugin_id: String, text: String) -> Result<(), String> {
    blocking(move || manager::get().write_plugin_config(&id, &plugin_id, &text)).await
}

#[tauri::command]
pub async fn hosting_reset_plugin_config(id: String, plugin_id: String) -> Result<(), String> {
    blocking(move || manager::get().reset_plugin_config(&id, &plugin_id)).await
}

#[tauri::command]
pub async fn hosting_content(id: String) -> Result<Vec<ContentFile>, String> {
    blocking(move || manager::get().content(&id)).await
}

#[tauri::command]
pub async fn hosting_import_content(id: String, paths: Vec<String>, kind: ContentKind) -> Result<Vec<ContentFile>, String> {
    blocking(move || manager::get().import_content(&id, paths, kind)).await
}

#[tauri::command]
pub async fn hosting_remove_content(id: String, file_name: String) -> Result<(), String> {
    blocking(move || manager::get().remove_content(&id, &file_name)).await
}
