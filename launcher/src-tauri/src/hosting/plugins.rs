//! The plugin library: plugins imported once, enabled per server.
//!
//! A plugin is a .NET assembly implementing `IDeadworksPlugin`. It can arrive as
//! a single DLL, a zip or a folder; the ones that come with dependency DLLs
//! keep them together. Deadworks treats every DLL in `managed\plugins` as a
//! candidate and loads each into its own context, so dependencies simply ride
//! along next to the plugin.

use std::path::{Path, PathBuf};

use super::fsutil;
use super::store::{now_secs, read_json, write_json, Layout};
use super::types::PluginEntry;

const ENTRY_FILE: &str = "plugin.json";
/// Supplied by Deadworks itself (or build-time only, like the source
/// generator); a copy shipped with a plugin would shadow the real one.
fn is_host_assembly(stem: &str) -> bool {
    let s = stem.to_ascii_lowercase();
    s.starts_with("deadworksmanaged") || s == "google.protobuf"
}
const SIDECAR_EXTS: &[&str] = &["pdb", "json", "xml"];

pub fn plugin_dir(layout: &Layout, id: &str) -> PathBuf {
    layout.plugins_dir().join(id)
}

/// The library folder of plugin `id`, for deleting. Only ever a direct child of the library:
/// an id like `..` would otherwise name the hosting root itself.
fn owned_dir(layout: &Layout, id: &str) -> Result<PathBuf, String> {
    let dir = plugin_dir(layout, id);
    if safe_id(id) && dir.parent() == Some(layout.plugins_dir().as_path()) && dir.file_name().is_some_and(|n| n == id) {
        Ok(dir)
    } else {
        Err(format!("'{id}' isn't a usable plugin name."))
    }
}

pub fn entry(layout: &Layout, id: &str) -> Option<PluginEntry> {
    read_json(&plugin_dir(layout, id).join(ENTRY_FILE)).ok()
}

pub fn library(layout: &Layout) -> Vec<PluginEntry> {
    let Ok(rd) = std::fs::read_dir(layout.plugins_dir()) else { return Vec::new() };
    let mut out: Vec<PluginEntry> = rd.flatten().filter_map(|e| read_json(&e.path().join(ENTRY_FILE)).ok()).collect();
    out.sort_by_key(|e| e.id.to_ascii_lowercase());
    out
}

pub fn remove(layout: &Layout, id: &str) -> Result<(), String> {
    let dir = owned_dir(layout, id).map_err(|_| "Unknown plugin".to_string())?;
    fsutil::remove_dir_all_force(&dir).map_err(|e| format!("Couldn't remove the plugin: {e}"))
}

/// Import DLLs, zips and folders. Each plugin assembly found becomes a library
/// entry carrying the non-plugin DLLs that came with it.
pub fn import(layout: &Layout, paths: &[String]) -> Result<Vec<PluginEntry>, String> {
    let mut imported = Vec::new();
    for p in paths {
        let path = Path::new(p);
        let staging = layout.downloads_dir().join(format!("plugin-import-{}", uuid::Uuid::new_v4()));
        let result = (|| {
            let files: Vec<PathBuf> = if path.is_dir() {
                collect_files(path)
            } else if has_ext(path, "zip") {
                fsutil::extract_zip(path, &staging)?;
                collect_files(&staging)
            } else if has_ext(path, "dll") {
                let mut v = vec![path.to_path_buf()];
                // Debug symbols and deps manifest next to it, if any.
                for ext in SIDECAR_EXTS {
                    let side = path.with_extension(ext);
                    if side.is_file() {
                        v.push(side);
                    }
                }
                if let Some(stem) = path.file_stem() {
                    let deps = path.with_file_name(format!("{}.deps.json", stem.to_string_lossy()));
                    if deps.is_file() {
                        v.push(deps);
                    }
                }
                v
            } else {
                return Err(format!("{} isn't a plugin (.dll), zip or folder.", file_name(path)));
            };
            import_files(layout, &files, path)
        })();
        let _ = fsutil::remove_dir_all_force(&staging);
        imported.extend(result?);
    }
    Ok(imported)
}

fn import_files(layout: &Layout, files: &[PathBuf], origin: &Path) -> Result<Vec<PluginEntry>, String> {
    let dlls: Vec<&PathBuf> = files
        .iter()
        .filter(|f| has_ext(f, "dll"))
        .filter(|f| !is_host_assembly(&stem(f)))
        .collect();
    let mut mains = Vec::new();
    let mut deps = Vec::new();
    for dll in dlls {
        let bytes = std::fs::read(dll).map_err(|e| format!("Couldn't read {}: {e}", file_name(dll)))?;
        if !is_managed_pe(&bytes) {
            // Native DLLs can't be loaded as plugins; keep them as dependencies.
            deps.push(dll);
        } else if is_plugin_assembly(&bytes) {
            mains.push(dll);
        } else {
            deps.push(dll);
        }
    }
    if mains.is_empty() {
        return Err(format!(
            "{} doesn't contain a Deadworks plugin (no assembly implementing IDeadworksPlugin).",
            file_name(origin)
        ));
    }
    let mut out = Vec::new();
    for main in mains {
        let id = stem(main);
        let dir = owned_dir(layout, &id)?;
        let _ = fsutil::remove_dir_all_force(&dir);
        std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
        let mut names = Vec::new();
        let mut size = 0;
        let sidecars = files.iter().filter(|f| {
            let s = stem(f);
            SIDECAR_EXTS.iter().any(|e| has_ext(f, e)) && (s.eq_ignore_ascii_case(&id) || s.eq_ignore_ascii_case(&format!("{id}.deps")))
        });
        for f in std::iter::once(main).chain(deps.iter().copied()).chain(sidecars) {
            let name = file_name(f);
            if names.iter().any(|n: &String| n.eq_ignore_ascii_case(&name)) {
                continue;
            }
            std::fs::copy(f, dir.join(&name)).map_err(|e| format!("Couldn't import {name}: {e}"))?;
            size += std::fs::metadata(f).map(|m| m.len()).unwrap_or(0);
            names.push(name);
        }
        let entry = PluginEntry { id, files: names, size_bytes: size, imported_at: now_secs() };
        write_json(&dir.join(ENTRY_FILE), &entry)?;
        out.push(entry);
    }
    Ok(out)
}

fn collect_files(dir: &Path) -> Vec<PathBuf> {
    let mut out = Vec::new();
    let Ok(rd) = std::fs::read_dir(dir) else { return out };
    for e in rd.flatten() {
        let p = e.path();
        if p.is_dir() {
            out.extend(collect_files(&p));
        } else {
            out.push(p);
        }
    }
    out
}

/// A PE image with a CLR (COM descriptor) data directory, i.e. a .NET assembly.
pub fn is_managed_pe(b: &[u8]) -> bool {
    let rd32 = |o: usize| b.get(o..o + 4).map(|s| u32::from_le_bytes(s.try_into().unwrap()));
    let rd16 = |o: usize| b.get(o..o + 2).map(|s| u16::from_le_bytes(s.try_into().unwrap()));
    if b.get(0..2) != Some(b"MZ") {
        return false;
    }
    let Some(pe) = rd32(0x3c).map(|v| v as usize) else { return false };
    if b.get(pe..pe + 4) != Some(b"PE\0\0") {
        return false;
    }
    let opt = pe + 24;
    let dirs = match rd16(opt) {
        Some(0x10b) => opt + 96,
        Some(0x20b) => opt + 112,
        _ => return false,
    };
    // Data directory 14: CLR runtime header (RVA, size).
    rd32(dirs + 14 * 8).is_some_and(|rva| rva != 0)
}

/// Heuristic: the metadata string heap names the plugin interface or base class.
fn is_plugin_assembly(b: &[u8]) -> bool {
    let has = |needle: &[u8]| b.windows(needle.len()).any(|w| w == needle);
    has(b"DeadworksManaged.Api") && (has(b"IDeadworksPlugin") || has(b"DeadworksPluginBase"))
}

/// A plugin id is a DLL's file name without `.dll`, and becomes a folder name. `..` (from a
/// file called `...dll`) and Windows' device names are file names too, and must not get that far.
pub fn safe_id(id: &str) -> bool {
    let first = id.split('.').next().unwrap_or_default().to_ascii_uppercase();
    let device = matches!(first.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || (first.len() == 4 && (first.starts_with("COM") || first.starts_with("LPT")) && first.ends_with(|c: char| c.is_ascii_digit()));
    !id.is_empty()
        && id.len() <= 100
        && !id.starts_with('.')
        && !id.ends_with('.')
        && !device
        && id.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '-' | '.'))
}

fn has_ext(p: &Path, ext: &str) -> bool {
    p.extension().is_some_and(|e| e.eq_ignore_ascii_case(ext))
}

fn stem(p: &Path) -> String {
    p.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default()
}

fn file_name(p: &Path) -> String {
    p.file_name().map(|s| s.to_string_lossy().into_owned()).unwrap_or_else(|| p.display().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_that_are_not_plain_folder_names_are_refused() {
        for id in ["DeathmatchPlugin", "My.Plugin_v2", "a-b"] {
            assert!(safe_id(id), "{id}");
        }
        // `...dll` has the stem `..`, which as a folder is the hosting root.
        assert_eq!(stem(Path::new("...dll")), "..");
        for id in ["", ".", "..", "...", ".hidden", "trailing.", "a/b", r"a\b", "a b", "CON", "nul.plugin", "com1", "LPT9.x"] {
            assert!(!safe_id(id), "{id}");
        }
    }

    #[test]
    fn a_plugin_folder_is_only_ever_inside_the_library() {
        let layout = Layout::new(std::env::temp_dir().join("dw-plugins-test"));
        assert!(owned_dir(&layout, "Mine").is_ok_and(|d| d.parent() == Some(layout.plugins_dir().as_path())));
        for id in ["..", ".", "a/b", ""] {
            assert!(owned_dir(&layout, id).is_err(), "{id}");
        }
        assert!(remove(&layout, "..").is_err());
    }

    #[test]
    fn native_and_garbage_are_not_managed() {
        assert!(!is_managed_pe(b"not a pe"));
        assert!(!is_managed_pe(b"MZ"));
    }

    #[test]
    fn managed_dlls_from_the_repo_are_detected() {
        // The Deadworks API assembly is a managed PE; a native exe is not.
        let api = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../managed/DeadworksManaged.Api/bin/Release/net10.0/DeadworksManaged.Api.dll");
        if let Ok(bytes) = std::fs::read(api) {
            assert!(is_managed_pe(&bytes));
            assert!(!is_plugin_assembly(b"nothing here"));
        }
    }
}
