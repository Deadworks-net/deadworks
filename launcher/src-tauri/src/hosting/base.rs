//! The shared base install every server's tree links into.
//!
//! Filled from the user's own Deadlock client, driven by Steam's depot
//! manifests: only files Steam shipped are taken, and each one must hash to
//! Steam's SHA1. Mods, plugins, replays and downloaded maps sitting in the
//! client install are never looked at, and a shipped file a mod replaced is
//! refused (or, if the user insists, copied and recorded as modified).

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;

use super::fsutil;
use super::manifest::DepotFile;
use super::task::{Progress, TaskError, CANCELLED};
use super::types::CopyMode;

const GAMEINFO: &str = "game\\citadel\\gameinfo.gi";
const WORKERS: usize = 4;
/// Space kept free beyond what the copy needs.
const SPACE_MARGIN: u64 = 1 << 30;

pub struct SyncOutcome {
    pub modified_files: Vec<String>,
    pub size_bytes: u64,
}

/// Bring `base_dir` to exactly `files`, taking content from the client install
/// at `client_dir` (the folder holding `game\`). `previous` is what the base
/// held before; files it already has with the right hash are left alone.
pub fn sync_from_client(
    base_dir: &Path,
    client_dir: &Path,
    files: &[DepotFile],
    previous: &[DepotFile],
    mode: CopyMode,
    allow_modified: bool,
    progress: &Progress,
) -> Result<SyncOutcome, TaskError> {
    let prev: HashMap<String, [u8; 20]> =
        previous.iter().map(|f| (f.path.to_ascii_lowercase(), f.sha1)).collect();

    let todo: Vec<&DepotFile> = files
        .iter()
        .filter(|f| {
            let dst = fsutil::join_rel(base_dir, &f.path);
            let unchanged = prev.get(&f.path.to_ascii_lowercase()) == Some(&f.sha1)
                && std::fs::metadata(&dst).map(|m| m.len() == f.size).unwrap_or(false);
            // A hardlinked base must still be linked to the client's current file.
            !(unchanged && (mode == CopyMode::Copy || fsutil::same_file(&dst, &fsutil::join_rel(client_dir, &f.path))))
        })
        .collect();

    for f in &todo {
        if !fsutil::join_rel(client_dir, &f.path).is_file() {
            return Err(format!(
                "Deadlock is missing {}. Verify its files in Steam.",
                f.path
            )
            .into());
        }
    }

    let needed: u64 = if mode == CopyMode::Copy { todo.iter().map(|f| f.size).sum() } else { 0 };
    if needed > 0 {
        if let Some((free, _)) = super::disk::space(base_dir) {
            if free < needed + SPACE_MARGIN {
                return Err(format!(
                    "Not enough space: needs {}, {} free.",
                    human_bytes(needed + SPACE_MARGIN),
                    human_bytes(free)
                )
                .into());
            }
        }
    }

    let label = match mode {
        CopyMode::Copy => "Copying game files",
        CopyMode::Hardlink => "Linking game files",
    };
    progress.stage("game", label, todo.iter().map(|f| f.size).sum(), todo.len() as u64);

    let next = AtomicUsize::new(0);
    let modified = Mutex::new(Vec::<String>::new());
    let failure = Mutex::new(None::<String>);
    std::thread::scope(|s| {
        for _ in 0..WORKERS {
            s.spawn(|| loop {
                if progress.cancelled() || failure.lock().unwrap().is_some() {
                    return;
                }
                let i = next.fetch_add(1, Ordering::Relaxed);
                let Some(f) = todo.get(i) else { return };
                match place_file(base_dir, client_dir, f, mode, allow_modified, progress) {
                    Ok(Placed::Clean) => {}
                    Ok(Placed::Modified) => modified.lock().unwrap().push(f.path.clone()),
                    Err(e) => {
                        failure.lock().unwrap().get_or_insert(e);
                        return;
                    }
                }
                progress.files_done.fetch_add(1, Ordering::Relaxed);
            });
        }
    });

    if progress.cancelled() {
        return Err(CANCELLED.into());
    }
    if let Some(e) = failure.into_inner().unwrap() {
        return Err(e.into());
    }
    let mut modified = modified.into_inner().unwrap();
    modified.sort();
    if !modified.is_empty() && !allow_modified {
        return Err(TaskError {
            message: format!(
                "{} game file{} {} been changed by mods.",
                modified.len(),
                if modified.len() == 1 { "" } else { "s" },
                if modified.len() == 1 { "has" } else { "have" }
            ),
            modified,
        });
    }

    remove_stale(base_dir, files, previous);

    // Files a previous run recorded as modified, which are clean now, drop out.
    let size_bytes = files.iter().map(|f| f.size).sum();
    Ok(SyncOutcome { modified_files: modified, size_bytes })
}

enum Placed {
    Clean,
    Modified,
}

fn place_file(
    base_dir: &Path,
    client_dir: &Path,
    f: &DepotFile,
    mode: CopyMode,
    allow_modified: bool,
    progress: &Progress,
) -> Result<Placed, String> {
    let src = fsutil::join_rel(client_dir, &f.path);
    let dst = fsutil::join_rel(base_dir, &f.path);
    let io = |e: std::io::Error| {
        if e.kind() == std::io::ErrorKind::Interrupted {
            CANCELLED.to_string()
        } else {
            format!("Couldn't copy {}: {e}", f.path)
        }
    };
    let sha = match mode {
        CopyMode::Copy => fsutil::copy_hashing(&src, &dst, &progress.bytes_done, &progress.cancel).map_err(io)?,
        CopyMode::Hardlink => fsutil::sha1_file(&src, &progress.bytes_done, &progress.cancel).map_err(io)?,
    };
    if sha == f.sha1 {
        if mode == CopyMode::Hardlink {
            fsutil::relink(&src, &dst).map_err(|e| link_error(&f.path, e))?;
        } else {
            let _ = fsutil::set_readonly(&dst, true);
        }
        return Ok(Placed::Clean);
    }

    // The client's gameinfo.gi normally carries this launcher's own search-path
    // block (and maybe Deadlock Mod Manager's). Recover Steam's original.
    if f.path.eq_ignore_ascii_case(GAMEINFO) {
        if let Some(original) = recover_gameinfo(&src, &f.sha1) {
            fsutil::write_real(&dst, &original).map_err(io)?;
            let _ = fsutil::set_readonly(&dst, mode == CopyMode::Copy);
            return Ok(Placed::Clean);
        }
    }

    if allow_modified {
        if mode == CopyMode::Hardlink {
            fsutil::relink(&src, &dst).map_err(|e| link_error(&f.path, e))?;
        } else {
            let _ = fsutil::set_readonly(&dst, true);
        }
    } else if mode == CopyMode::Copy {
        let _ = fsutil::remove_file_force(&dst);
    }
    Ok(Placed::Modified)
}

fn link_error(path: &str, e: std::io::Error) -> String {
    format!("Couldn't link {path}: {e}. Linking needs the same drive as Deadlock.")
}

/// Steam's original gameinfo.gi from a client copy that tools have patched.
fn recover_gameinfo(client_file: &Path, want: &[u8; 20]) -> Option<Vec<u8>> {
    let dir = client_file.parent()?;
    let current = std::fs::read_to_string(client_file).ok();
    let mut candidates: Vec<String> = Vec::new();
    if let Some(cur) = &current {
        if let Ok(stripped) = crate::gameinfo::render_stripped(cur) {
            candidates.push(stripped);
        }
    }
    // Deadlock Mod Manager's pristine copy, then our own first-patch backup.
    for backup in ["gameinfo.gi.bak", "gameinfo.gi.deadworks.bak"] {
        if let Ok(text) = std::fs::read_to_string(dir.join(backup)) {
            if let Ok(stripped) = crate::gameinfo::render_stripped(&text) {
                candidates.push(stripped);
            }
            candidates.push(text);
        }
    }
    candidates.into_iter().map(String::into_bytes).find(|c| &fsutil::sha1_bytes(c) == want)
}

/// Delete base files that were shipped before but no longer are.
fn remove_stale(base_dir: &Path, files: &[DepotFile], previous: &[DepotFile]) {
    let keep: HashSet<String> = files.iter().map(|f| f.path.to_ascii_lowercase()).collect();
    for f in previous {
        if !keep.contains(&f.path.to_ascii_lowercase()) {
            let _ = fsutil::remove_file_force(&fsutil::join_rel(base_dir, &f.path));
        }
    }
}

pub struct VerifyOutcome {
    /// Files that were replaced; server trees must relink to pick them up.
    pub repaired: usize,
    pub still_broken: Vec<String>,
}

/// Re-hash the base against the recorded manifest. Broken files are restored
/// from the client when its copy is intact.
pub fn verify(
    base_dir: &Path,
    client_dir: Option<&Path>,
    files: &[DepotFile],
    mode: CopyMode,
    progress: &Progress,
) -> Result<VerifyOutcome, String> {
    progress.stage("verify", "Verifying game files", files.iter().map(|f| f.size).sum(), files.len() as u64);
    let next = AtomicUsize::new(0);
    let broken = Mutex::new(Vec::<&DepotFile>::new());
    std::thread::scope(|s| {
        for _ in 0..WORKERS {
            s.spawn(|| loop {
                if progress.cancelled() {
                    return;
                }
                let i = next.fetch_add(1, Ordering::Relaxed);
                let Some(f) = files.get(i) else { return };
                let ok = fsutil::sha1_file(&fsutil::join_rel(base_dir, &f.path), &progress.bytes_done, &progress.cancel)
                    .map(|sha| sha == f.sha1)
                    .unwrap_or(false);
                if !ok {
                    broken.lock().unwrap().push(f);
                }
                progress.files_done.fetch_add(1, Ordering::Relaxed);
            });
        }
    });
    progress.check_cancel()?;
    let broken = broken.into_inner().unwrap();
    if broken.is_empty() {
        return Ok(VerifyOutcome { repaired: 0, still_broken: Vec::new() });
    }

    let mut still = Vec::new();
    let Some(client_dir) = client_dir else {
        return Ok(VerifyOutcome { repaired: 0, still_broken: broken.iter().map(|f| f.path.clone()).collect() });
    };
    progress.stage("repair", "Repairing game files", broken.iter().map(|f| f.size).sum(), broken.len() as u64);
    for f in &broken {
        progress.check_cancel()?;
        match place_file(base_dir, client_dir, f, mode, false, progress) {
            Ok(Placed::Clean) => {}
            _ => still.push(f.path.clone()),
        }
        progress.files_done.fetch_add(1, Ordering::Relaxed);
    }
    Ok(VerifyOutcome { repaired: broken.len() - still.len(), still_broken: still })
}

/// Install dir (holding `game\`) of a `...\Deadlock\game` directory.
pub fn install_dir_of(game_dir: &Path) -> PathBuf {
    game_dir.parent().map(Path::to_path_buf).unwrap_or_else(|| game_dir.to_path_buf())
}

pub fn human_bytes(n: u64) -> String {
    const GB: f64 = 1024.0 * 1024.0 * 1024.0;
    const MB: f64 = 1024.0 * 1024.0;
    if n as f64 >= GB {
        format!("{:.1} GB", n as f64 / GB)
    } else {
        format!("{:.0} MB", n as f64 / MB)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hosting::types::TaskKind;

    fn scratch(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("dw-base-{name}-{}", std::process::id()));
        let _ = fsutil::remove_dir_all_force(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn file(root: &Path, rel: &str, content: &[u8]) -> DepotFile {
        let p = fsutil::join_rel(root, rel);
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        std::fs::write(&p, content).unwrap();
        DepotFile { path: rel.into(), size: content.len() as u64, sha1: fsutil::sha1_bytes(content) }
    }

    #[test]
    fn copies_only_manifest_files_and_refuses_modified_ones() {
        let dir = scratch("sync");
        let client = dir.join("client");
        let base = dir.join("base");
        let a = file(&client, "game\\bin\\a.dll", b"engine");
        let mut b = file(&client, "game\\citadel\\pak01_000.vpk", b"original");
        file(&client, "game\\citadel\\addons\\mod.vpk", b"a mod");
        // A mod replaced a shipped file.
        std::fs::write(client.join(&b.path), b"modded").unwrap();
        b.size = 6;
        let files = vec![a.clone(), b.clone()];

        let p = Progress::new(TaskKind::Install);
        let err = sync_from_client(&base, &client, &files, &[], CopyMode::Copy, false, &p).err().unwrap();
        assert_eq!(err.modified, vec![b.path.clone()]);
        assert!(!base.join(&b.path).exists(), "a refused file must not be left in the base");

        let p = Progress::new(TaskKind::Install);
        let ok = sync_from_client(&base, &client, &files, &[], CopyMode::Copy, true, &p).unwrap();
        assert_eq!(ok.modified_files, vec![b.path.clone()]);
        assert!(base.join(&a.path).exists());
        assert!(!base.join("game\\citadel\\addons\\mod.vpk").exists(), "non-manifest files are never copied");
        assert!(std::fs::metadata(base.join(&a.path)).unwrap().permissions().readonly());
        fsutil::remove_dir_all_force(&dir).unwrap();
    }

    #[test]
    fn hardlink_mode_links_clean_files_and_never_touches_the_client() {
        let dir = scratch("hardlink");
        let client = dir.join("client");
        let base = dir.join("base");
        let a = file(&client, "game\\bin\\a.dll", b"engine");
        let vanilla = "\"GameInfo\"\n{\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tGame\t\t\t\tcitadel\n\t\t}\n\t}\n}\n";
        let gi = file(&client, GAMEINFO, vanilla.as_bytes());
        let patched = vanilla.replace(
            "\t\t\tGame\t\t\t\tcitadel\n",
            "\t\t\t// Deadworks Launcher - Start\n\t\t\tGame                citadel/deadworks_mods\n\t\t\t// Deadworks Launcher - End\n\t\t\tGame\t\t\t\tcitadel\n",
        );
        std::fs::write(fsutil::join_rel(&client, GAMEINFO), &patched).unwrap();

        let p = Progress::new(TaskKind::Install);
        let ok = sync_from_client(&base, &client, &[a.clone(), gi], &[], CopyMode::Hardlink, false, &p).unwrap();
        assert!(ok.modified_files.is_empty());
        assert!(fsutil::same_file(&base.join(&a.path), &client.join(&a.path)), "clean files are links, not copies");
        assert!(!fsutil::same_file(&fsutil::join_rel(&base, GAMEINFO), &fsutil::join_rel(&client, GAMEINFO)), "a recovered file is a real copy");
        assert_eq!(std::fs::read_to_string(fsutil::join_rel(&base, GAMEINFO)).unwrap(), vanilla);
        assert_eq!(std::fs::read_to_string(fsutil::join_rel(&client, GAMEINFO)).unwrap(), patched, "the client is never modified");
        assert!(
            !std::fs::metadata(client.join(&a.path)).unwrap().permissions().readonly(),
            "linking must not make client files read-only"
        );
        fsutil::remove_dir_all_force(&dir).unwrap();
    }

    #[test]
    fn launcher_patched_gameinfo_is_restored_to_steams_version() {
        let dir = scratch("gameinfo");
        let client = dir.join("client");
        let base = dir.join("base");
        let vanilla = "\"GameInfo\"\n{\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tGame\t\t\t\tcitadel\n\t\t}\n\t}\n}\n";
        let f = file(&client, GAMEINFO, vanilla.as_bytes());
        let patched = vanilla.replace(
            "\t\t\tGame\t\t\t\tcitadel\n",
            "\t\t\t// Deadworks Launcher - Start\n\t\t\tGame                citadel/deadworks_mods\n\t\t\t// Deadworks Launcher - End\n\t\t\tGame\t\t\t\tcitadel\n",
        );
        std::fs::write(fsutil::join_rel(&client, GAMEINFO), &patched).unwrap();
        let p = Progress::new(TaskKind::Install);
        let ok = sync_from_client(&base, &client, &[f], &[], CopyMode::Copy, false, &p).unwrap();
        assert!(ok.modified_files.is_empty());
        assert_eq!(std::fs::read_to_string(fsutil::join_rel(&base, GAMEINFO)).unwrap(), vanilla);
        fsutil::remove_dir_all_force(&dir).unwrap();
    }

    #[test]
    fn resync_skips_unchanged_and_removes_unshipped_files() {
        let dir = scratch("resync");
        let client = dir.join("client");
        let base = dir.join("base");
        let a = file(&client, "game\\a", b"one");
        let old = file(&client, "game\\old", b"gone soon");
        let p = Progress::new(TaskKind::Install);
        sync_from_client(&base, &client, &[a.clone(), old.clone()], &[], CopyMode::Copy, false, &p).unwrap();

        let a2 = file(&client, "game\\a", b"two");
        let p = Progress::new(TaskKind::Update);
        sync_from_client(&base, &client, &[a2.clone()], &[a, old.clone()], CopyMode::Copy, false, &p).unwrap();
        assert_eq!(std::fs::read(base.join("game\\a")).unwrap(), b"two");
        assert!(!base.join(&old.path).exists());
        assert_eq!(p.files_total.load(Ordering::Relaxed), 1);

        // Nothing changed: nothing to do.
        let p = Progress::new(TaskKind::Update);
        sync_from_client(&base, &client, &[a2.clone()], &[a2], CopyMode::Copy, false, &p).unwrap();
        assert_eq!(p.files_total.load(Ordering::Relaxed), 0);
        fsutil::remove_dir_all_force(&dir).unwrap();
    }
}
