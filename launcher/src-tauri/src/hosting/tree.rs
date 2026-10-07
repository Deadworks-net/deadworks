//! The runnable tree of one server.
//!
//! Every shipped game file is a hardlink into the shared base (the base and the
//! servers share the hosting root, so this always works and costs no space).
//! Over that go real files the server owns: the Deadworks release, gameinfo.gi
//! for its network mode, generated cfgs and configs, its plugins and content.
//! A stock server only ever *creates* files next to the shipped ones (verified:
//! vcfgs, steam_appid.txt, configs\), so its own state survives relinking.

use std::collections::HashSet;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use super::cfg;
use super::fsutil;
use super::manifest::DepotFile;
use super::netcfg;
use super::store::{read_json, write_json, Layout};
use super::types::{ContentKind, ServerConfig};

const GAMEINFO: &str = "game\\citadel\\gameinfo.gi";
const STATE_FILE: &str = ".dw-tree.json";

/// What the launcher last put into a tree, so the next build can take away
/// exactly what it no longer wants and nothing the server or user made.
#[derive(Debug, Default, Serialize, Deserialize)]
struct TreeState {
    stamp: String,
    linked: Vec<String>,
    release: Vec<String>,
    plugins: Vec<String>,
    content: Vec<String>,
}

pub struct TreeInputs<'a> {
    pub layout: &'a Layout,
    pub config: &'a ServerConfig,
    pub base_files: &'a [DepotFile],
    /// Changes whenever base files change (build id + generation).
    pub base_stamp: String,
    pub release_dir: &'a Path,
    /// Written into the generated cfg where the console is RCON.
    pub rcon_password: Option<&'a str>,
}

pub fn prepare(inp: &TreeInputs) -> Result<(), String> {
    let id = &inp.config.id;
    let tree = inp.layout.server_tree(id);
    let state_path = inp.layout.server_dir(id).join(STATE_FILE);
    let mut state: TreeState = read_json(&state_path).unwrap_or_default();

    let release = super::release::files(inp.release_dir);
    let mut overlay: HashSet<String> = release.iter().map(|p| p.to_ascii_lowercase()).collect();
    overlay.insert(GAMEINFO.to_ascii_lowercase());

    // 1. Shipped files, relinked only when the base changed.
    if state.stamp != inp.base_stamp || !tree.join("game").is_dir() {
        let wanted: Vec<&DepotFile> =
            inp.base_files.iter().filter(|f| !overlay.contains(&f.path.to_ascii_lowercase())).collect();
        let keep: HashSet<String> = wanted.iter().map(|f| f.path.to_ascii_lowercase()).collect();
        for old in &state.linked {
            if !keep.contains(&old.to_ascii_lowercase()) {
                let _ = fsutil::remove_file_force(&fsutil::join_rel(&tree, old));
            }
        }
        let base_dir = inp.layout.base_dir();
        for f in &wanted {
            fsutil::relink(&fsutil::join_rel(&base_dir, &f.path), &fsutil::join_rel(&tree, &f.path))
                .map_err(|e| format!("Couldn't prepare the server's game files ({}): {e}", f.path))?;
        }
        state.linked = wanted.iter().map(|f| f.path.clone()).collect();
        state.stamp = inp.base_stamp.clone();
        // Save now: a failure below must not make the next start relink everything again.
        write_json(&state_path, &state)?;
    }

    // 2. Deadworks itself.
    let current: HashSet<String> = release.iter().map(|p| p.to_ascii_lowercase()).collect();
    for old in &state.release {
        if !current.contains(&old.to_ascii_lowercase()) {
            let _ = fsutil::remove_file_force(&fsutil::join_rel(&tree, old));
        }
    }
    for rel in &release {
        let data = std::fs::read(fsutil::join_rel(inp.release_dir, rel)).map_err(|e| format!("Couldn't read Deadworks file {rel}: {e}"))?;
        fsutil::write_if_changed(&fsutil::join_rel(&tree, rel), &data).map_err(|e| locked_error(rel, e))?;
    }
    state.release = release;

    // 3. gameinfo.gi for the network mode.
    let vanilla = std::fs::read_to_string(fsutil::join_rel(&inp.layout.base_dir(), GAMEINFO))
        .map_err(|e| format!("Couldn't read the base gameinfo.gi: {e}"))?;
    let gameinfo = netcfg::render_gameinfo(&vanilla, inp.config.network)?;
    fsutil::write_if_changed(&fsutil::join_rel(&tree, GAMEINFO), gameinfo.as_bytes()).map_err(|e| e.to_string())?;

    // 4. Generated config.
    let citadel_cfg = tree.join("game").join("citadel").join("cfg");
    let launcher_cfg = citadel_cfg.join(cfg::LAUNCHER_CFG);
    fsutil::write_if_changed(&launcher_cfg, cfg::launcher_cfg(inp.config, inp.rcon_password).as_bytes()).map_err(|e| e.to_string())?;
    // It holds the server password, and under Wine the console (RCON) password.
    fsutil::owner_only(&launcher_cfg);
    let user_cfg = citadel_cfg.join("deadworks_user.cfg");
    if !user_cfg.exists() {
        let _ = std::fs::write(&user_cfg, "// Your own console commands, run after the launcher's settings on every start.\n");
    }
    write_configs(inp.layout, inp.config)?;

    // 5. Plugins and content.
    state.plugins = sync_plugins(inp.layout, inp.config, &state.plugins)?;
    state.content = sync_content(inp.layout, inp.config, &state.content)?;
    write_json(&state_path, &state)
}

fn locked_error(rel: &str, e: std::io::Error) -> String {
    if crate::gameinfo::is_sharing_violation(&e) {
        format!("{rel} is in use. Is this server already running outside the launcher?")
    } else {
        format!("Couldn't install {rel}: {e}")
    }
}

/// `configs/deadworks.jsonc` and `configs/plugins.jsonc`.
pub fn write_configs(layout: &Layout, config: &ServerConfig) -> Result<(), String> {
    let dir = layout.server_configs(&config.id);
    let dw = dir.join("deadworks.jsonc");
    let existing = std::fs::read_to_string(&dw).ok();
    let deadworks = cfg::deadworks_jsonc(existing.as_deref(), config)?;
    let pj = dir.join("plugins.jsonc");
    let existing = std::fs::read_to_string(&pj).ok();
    let plugins = cfg::plugins_jsonc(existing.as_deref(), &config.plugins)?;
    // Both checked before either is written.
    fsutil::write_if_changed(&dw, deadworks.as_bytes()).map_err(|e| e.to_string())?;
    fsutil::write_if_changed(&pj, plugins.as_bytes()).map_err(|e| e.to_string())?;
    Ok(())
}

/// Make `managed\plugins` hold exactly the enabled plugins' files (and leave
/// alone anything the user dropped in by hand). Returns what is now placed.
pub fn sync_plugins(layout: &Layout, config: &ServerConfig, placed_before: &[String]) -> Result<Vec<String>, String> {
    let dest_dir = layout.server_bin(&config.id).join("managed").join("plugins");
    std::fs::create_dir_all(&dest_dir).map_err(|e| e.to_string())?;
    let mut wanted: Vec<(PathBuf, String)> = Vec::new();
    for id in &config.plugins {
        let Some(entry) = super::plugins::entry(layout, id) else { continue };
        for f in entry.files {
            wanted.push((super::plugins::plugin_dir(layout, id).join(&f), f));
        }
    }
    let names: HashSet<String> = wanted.iter().map(|(_, n)| n.to_ascii_lowercase()).collect();
    for old in placed_before {
        if !names.contains(&old.to_ascii_lowercase()) {
            let _ = fsutil::remove_file_force(&dest_dir.join(old));
        }
    }
    let mut placed = Vec::new();
    for (src, name) in wanted {
        let data = std::fs::read(&src).map_err(|e| format!("Couldn't read plugin file {name}: {e}"))?;
        fsutil::write_if_changed(&dest_dir.join(&name), &data).map_err(|e| format!("Couldn't install plugin file {name}: {e}"))?;
        placed.push(name);
    }
    Ok(placed)
}

/// Link the enabled content VPKs where Deadworks mounts them.
fn sync_content(layout: &Layout, config: &ServerConfig, placed_before: &[String]) -> Result<Vec<String>, String> {
    let tree = layout.server_tree(&config.id);
    let content = layout.server_content(&config.id);
    let mut wanted: Vec<(PathBuf, String)> = Vec::new();
    for (names, kind) in [(&config.content_addons, ContentKind::Addon), (&config.extra_maps, ContentKind::Map)] {
        for name in names {
            let file = vpk_name(name);
            let src = content.join(super::content::kind_dir(kind)).join(&file);
            if !src.is_file() {
                continue;
            }
            let rel = match kind {
                ContentKind::Addon => format!("game\\citadel\\deadworks_mods\\vpks\\{file}"),
                ContentKind::Map => format!("game\\citadel\\maps\\{file}"),
            };
            wanted.push((src, rel));
        }
    }
    let keep: HashSet<String> = wanted.iter().map(|(_, r)| r.to_ascii_lowercase()).collect();
    for old in placed_before {
        if !keep.contains(&old.to_ascii_lowercase()) {
            let _ = fsutil::remove_file_force(&fsutil::join_rel(&tree, old));
        }
    }
    let mut placed = Vec::new();
    for (src, rel) in wanted {
        let dst = fsutil::join_rel(&tree, &rel);
        if !fsutil::same_file(&src, &dst) {
            fsutil::relink(&src, &dst).map_err(|e| format!("Couldn't install {rel}: {e}"))?;
        }
        placed.push(rel);
    }
    Ok(placed)
}

fn vpk_name(name: &str) -> String {
    if name.to_ascii_lowercase().ends_with(".vpk") {
        name.to_string()
    } else {
        format!("{name}.vpk")
    }
}

/// Live plugin changes on a running server: update configs and files only.
pub fn apply_plugins(layout: &Layout, config: &ServerConfig) -> Result<(), String> {
    let state_path = layout.server_dir(&config.id).join(STATE_FILE);
    let mut state: TreeState = read_json(&state_path).unwrap_or_default();
    write_configs(layout, config)?;
    state.plugins = sync_plugins(layout, config, &state.plugins)?;
    write_json(&state_path, &state)
}
