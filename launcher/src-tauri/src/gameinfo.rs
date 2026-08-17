//! gameinfo.gi patching.
//!
//! Two independent mechanisms live in the `FileSystem/SearchPaths` block:
//!
//!   * `addonroot citadel/deadworks_addons` — root for the per-server content
//!     VPKs that `addons.rs` downloads. Mounted through the engine's addon
//!     path, not through a search path.
//!   * `Game citadel/deadworks_mods` — a real search path, so the engine
//!     auto-mounts the VPKs in that directory at startup. Panorama is
//!     initialised before we can mount anything at runtime, so UI content has
//!     to be on a search path before the process starts.
//!
//! Both live inside a single marker-delimited block that we own, which lets us
//! re-assert the whole thing in one idempotent write.
//!
//! # Coexisting with Deadlock Mod Manager
//!
//! DMM rewrites the *entire* SearchPaths block on every launch (see its
//! `modify_search_paths` / `update_mod_path_multi`), so anything we put in
//! there is transient — we have to re-assert immediately before launching.
//! Conversely, several things would break DMM, and this module avoids all of
//! them:
//!
//!   * DMM locates its edit target with `content.find("SearchPaths")` and then
//!     the first `}` after it. We never emit the literal `SearchPaths`, and we
//!     never emit a brace, so its range stays correct whether or not we are
//!     present.
//!   * DMM decides "are mods enabled" with `content.contains("citadel/addons")`
//!     and collects profile folders from marker lines whose value *starts with*
//!     `citadel/addons`. Our paths are `citadel/deadworks_*`, which matches
//!     neither, so DMM's status and its vanilla-toggle validation stay honest.
//!   * DMM's markers (`// Deadlock Mod Manager - Start/End`) and its backup
//!     (`gameinfo.gi.bak`) and temp file (`gameinfo.gi.tmp`) are left alone; we
//!     use our own distinct names for all three.

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use serde::Serialize;

/// Root for the per-server content VPKs (`addons.rs`). Separate mechanism.
const ADDONROOT_VALUE: &str = "citadel/deadworks_addons";
/// Search path auto-mounted at startup. Must not contain `citadel/addons`,
/// or DMM will read our entry as one of its own profile folders.
const MODS_SEARCH_PATH: &str = "citadel/deadworks_mods";
/// Keeps `MOD` / `DEFAULT_WRITE_PATH` pinned where vanilla puts them once our
/// entry becomes the first `Game` path.
const ANCHOR_PATH: &str = "citadel";

const BLOCK_START: &str = "// Deadworks Launcher - Start";
const BLOCK_END: &str = "// Deadworks Launcher - End";

const BACKUP_NAME: &str = "gameinfo.gi.deadworks.bak";
const TEMP_NAME: &str = "gameinfo.gi.deadworks.tmp";

/// DMM's start marker. Only used to report coexistence in [`Status`]; we never
/// read, move or rewrite DMM's block.
const DMM_MARKER: &str = "// Deadlock Mod Manager - Start";

// ── Public API ──

#[derive(Debug, Clone, Serialize)]
pub struct Status {
    /// Our marker block is present and its contents are what we would write.
    pub up_to_date: bool,
    /// A `Game citadel/deadworks_mods` entry is live (mods will mount).
    pub has_mods_search_path: bool,
    /// An `addonroot citadel/deadworks_addons` entry is live.
    pub has_addonroot: bool,
    /// Deadlock Mod Manager also manages this file.
    pub dmm_present: bool,
}

pub fn gameinfo_path(game_dir: &Path) -> PathBuf {
    game_dir.join("citadel").join("gameinfo.gi")
}

pub fn status(game_dir: &Path) -> Result<Status, String> {
    let content = read_gameinfo(game_dir)?;
    let live = live_entries(&content);
    Ok(Status {
        up_to_date: render_patch(&content).map(|p| p == content).unwrap_or(false),
        has_mods_search_path: live.mods_search_path,
        has_addonroot: live.addonroot,
        dmm_present: content.contains(DMM_MARKER),
    })
}

/// Insert (or refresh) our block in `FileSystem/SearchPaths`.
///
/// Idempotent: returns `Ok(false)` when the file already says exactly what we
/// want. Must be called immediately before launching the game — DMM rewrites
/// the whole block on its own launches and will have dropped ours.
pub fn ensure_patched(game_dir: &Path) -> Result<bool, String> {
    let path = gameinfo_path(game_dir);
    // Search paths are relative to the `game/` directory. A path pointing at a
    // directory that does not exist logs a warning on every start, so create it
    // alongside the entry that references it.
    let mods_dir = MODS_SEARCH_PATH
        .split('/')
        .fold(game_dir.to_path_buf(), |acc, seg| acc.join(seg));
    if let Err(e) = std::fs::create_dir_all(&mods_dir) {
        println!("[gameinfo] could not create {}: {}", mods_dir.display(), e);
    }

    let content = read_gameinfo(game_dir)?;
    let patched = render_patch(&content)?;
    if patched == content {
        return Ok(false);
    }
    backup_once(&path, &content);
    write_atomic(&path, &patched)?;
    Ok(true)
}

/// Remove our block, leaving everything else — including DMM's edits — intact.
/// The counterpart to [`ensure_patched`], for uninstall; no call site yet.
#[allow(dead_code)]
pub fn remove_patch(game_dir: &Path) -> Result<bool, String> {
    let path = gameinfo_path(game_dir);
    let content = read_gameinfo(game_dir)?;
    let stripped = render_stripped(&content)?;
    if stripped == content {
        return Ok(false);
    }
    write_atomic(&path, &stripped)?;
    Ok(true)
}

/// Best-effort re-assert for call sites that launch the game and have nowhere
/// useful to surface an error inline (startup, tray menu, `launch_deadlock`).
/// The error is stashed in [`LAST_ERROR`] for the UI instead.
pub fn reassert_quiet(game_dir: &Path) {
    let result = ensure_patched(game_dir);
    record(&result);
    match result {
        Ok(true) => println!("[gameinfo] re-asserted Deadworks search paths"),
        Ok(false) => {}
        Err(e) => println!("[gameinfo] could not patch gameinfo.gi: {}", e),
    }
}

/// The last patch failure from a call site that could not report it inline.
///
/// A failed patch is otherwise completely silent — the game starts normally and
/// just never mounts our search paths — so the UI has to put it in front of the
/// user. Cleared as soon as any attempt succeeds.
static LAST_ERROR: Mutex<Option<String>> = Mutex::new(None);

fn record(result: &Result<bool, String>) {
    if let Ok(mut slot) = LAST_ERROR.lock() {
        *slot = result.as_ref().err().cloned();
    }
}

/// Pending failure for the frontend to show. The startup patch runs inside
/// Tauri's `setup`, so it has always finished by the time the webview asks.
#[tauri::command]
pub fn gameinfo_error() -> Option<String> {
    LAST_ERROR.lock().ok().and_then(|slot| slot.clone())
}

/// Re-run the patch once the user has closed whatever was holding the file.
#[tauri::command]
pub fn retry_gameinfo_patch() -> Result<(), String> {
    let game_dir = crate::connect::resolve_game_dir()?;
    let result = ensure_patched(&game_dir);
    record(&result);
    result.map(|_| ())
}

/// True when the process holding the file is the game (or DMM) rather than a
/// permissions problem we cannot recover from.
pub(crate) fn is_sharing_violation(err: &std::io::Error) -> bool {
    #[cfg(windows)]
    {
        // ERROR_SHARING_VIOLATION = 32, ERROR_ACCESS_DENIED = 5
        matches!(err.raw_os_error(), Some(32) | Some(5))
    }
    #[cfg(not(windows))]
    {
        matches!(err.kind(), std::io::ErrorKind::PermissionDenied)
    }
}

// ── Rendering (pure, unit-tested) ──

#[derive(Default)]
struct LiveEntries {
    mods_search_path: bool,
    addonroot: bool,
}

/// Which of our entries are currently live anywhere in the file, regardless of
/// whether our markers survived.
fn live_entries(content: &str) -> LiveEntries {
    let mut out = LiveEntries::default();
    for line in content.lines() {
        let Some(key) = first_token(line) else { continue };
        let value = value_token(line).unwrap_or("");
        if key.eq_ignore_ascii_case("addonroot") && value.eq_ignore_ascii_case(ADDONROOT_VALUE) {
            out.addonroot = true;
        }
        if key.eq_ignore_ascii_case("Game") && value.eq_ignore_ascii_case(MODS_SEARCH_PATH) {
            out.mods_search_path = true;
        }
    }
    out
}

fn render_patch(content: &str) -> Result<String, String> {
    let (mut lines, open, close) = clean(content)?;

    // Scan the block body for the entries the engine derives behaviour from.
    // `Game_Language` / `Game_LowViolence` are distinct keys and must stay
    // above us, so we anchor on the first plain `Game`.
    let mut has_mod = false;
    let mut has_write = false;
    let mut insert_at = close; // no plain Game entry: sit just above the `}`
    let mut found_game = false;
    for (i, line) in lines.iter().enumerate().take(close).skip(open + 1) {
        let Some(key) = first_token(line) else { continue };
        if key.eq_ignore_ascii_case("Mod") {
            has_mod = true;
        } else if key.eq_ignore_ascii_case("Write") {
            has_write = true;
        } else if !found_game && key.eq_ignore_ascii_case("Game") {
            insert_at = i;
            found_game = true;
        }
    }

    // Match the entry we sit above. With no plain `Game` entry we land on the
    // closing brace, which is indented one level short of the body.
    let indent = (if found_game { indent_of(&lines[insert_at]) } else { None })
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| format!("{}\t", indent_of(&lines[open]).unwrap_or_default()));
    let nl = newline_for(&lines[open], content);

    let mut block: Vec<String> = Vec::with_capacity(6);
    block.push(format!("{indent}{BLOCK_START}{nl}"));
    // Highest precedence of the plain `Game` paths: server-required Panorama
    // assets win over anything DMM mounted.
    block.push(entry(&indent, "Game", MODS_SEARCH_PATH, nl));
    // Vanilla declares neither key, so the engine would repoint MOD and
    // DEFAULT_WRITE_PATH at whatever is now the first Game path — us. Pin them
    // back to `citadel`, which is what vanilla resolves to. When DMM is
    // installed it already emits both, and duplicating them would mount the
    // path twice.
    if !has_mod {
        block.push(entry(&indent, "Mod", ANCHOR_PATH, nl));
    }
    if !has_write {
        block.push(entry(&indent, "Write", ANCHOR_PATH, nl));
    }
    block.push(entry(&indent, "addonroot", ADDONROOT_VALUE, nl));
    block.push(format!("{indent}{BLOCK_END}{nl}"));

    // The line above the insertion point must terminate, or we would splice
    // our block onto the end of it.
    if insert_at > 0 && !lines[insert_at - 1].ends_with('\n') {
        lines[insert_at - 1].push_str(nl);
    }
    lines.splice(insert_at..insert_at, block);
    Ok(lines.concat())
}

fn render_stripped(content: &str) -> Result<String, String> {
    let (lines, _, _) = clean(content)?;
    Ok(lines.concat())
}

/// Split `content` into lines with every trace of us removed, and return the
/// block's `{` / `}` line indices in the *cleaned* line vector.
fn clean(content: &str) -> Result<(Vec<String>, usize, usize), String> {
    let located = locate_search_paths(content)
        .ok_or_else(|| "Could not find the FileSystem/SearchPaths block in gameinfo.gi".to_string())?;
    let src: Vec<&str> = content.split_inclusive('\n').collect();
    let open = line_of_offset(&src, located.open);
    let close = line_of_offset(&src, located.close);
    if close <= open {
        return Err("SearchPaths block in gameinfo.gi is on a single line; refusing to edit".into());
    }

    let mut drop = vec![false; src.len()];

    // Our marker block. An unterminated start marker means someone truncated
    // the file mid-block — drop the marker alone rather than eating the body.
    let mut start_idx: Option<usize> = None;
    for (i, line) in src.iter().enumerate().take(close).skip(open + 1) {
        let trimmed = line.trim();
        if trimmed.starts_with(BLOCK_START) {
            start_idx = Some(i);
        } else if trimmed.starts_with(BLOCK_END) {
            match start_idx.take() {
                Some(s) => drop[s..=i].fill(true),
                None => drop[i] = true,
            }
        }
    }
    if let Some(s) = start_idx {
        drop[s] = true;
    }

    // Entries we own that escaped the markers: older launcher versions wrote a
    // bare `addonroot` line, and a half-applied edit can leave a stray `Game`.
    for (i, line) in src.iter().enumerate().take(close).skip(open + 1) {
        if !drop[i] && is_owned_entry(line) {
            drop[i] = true;
        }
    }

    let mut lines = Vec::with_capacity(src.len());
    let mut new_open = open;
    let mut new_close = close;
    for (i, line) in src.iter().enumerate() {
        if drop[i] {
            if i < open {
                new_open -= 1;
            }
            if i < close {
                new_close -= 1;
            }
            continue;
        }
        lines.push((*line).to_string());
    }
    Ok((lines, new_open, new_close))
}

/// A line this module is responsible for, outside of our markers.
fn is_owned_entry(line: &str) -> bool {
    let Some(key) = first_token(line) else {
        return false;
    };
    // We own `addonroot` outright: replace any value, so a stale root from an
    // older build is corrected rather than duplicated.
    if key.eq_ignore_ascii_case("addonroot") {
        return true;
    }
    key.eq_ignore_ascii_case("Game")
        && value_token(line)
            .map(|v| v.eq_ignore_ascii_case(MODS_SEARCH_PATH))
            .unwrap_or(false)
}

fn entry(indent: &str, key: &str, value: &str, nl: &str) -> String {
    format!("{indent}{key:<20}{value}{nl}")
}

// ── gameinfo.gi lexing ──

struct Located {
    open: usize,
    close: usize,
}

/// Byte offsets of the braces delimiting `FileSystem/SearchPaths`, found by
/// brace depth rather than by string search, so comments and the other
/// `SearchPaths`-like keys in the file cannot mislead us.
fn locate_search_paths(content: &str) -> Option<Located> {
    locate_block(content, &["FileSystem", "SearchPaths"])
        .or_else(|| locate_block(content, &["SearchPaths"]))
}

fn locate_block(content: &str, path: &[&str]) -> Option<Located> {
    let b = content.as_bytes();
    let mut i = 0usize;
    let mut stack: Vec<String> = Vec::new();
    let mut last: Option<String> = None;
    let mut open: Option<usize> = None;
    let mut open_depth = 0usize;

    while i < b.len() {
        match b[i] {
            b'/' if b.get(i + 1) == Some(&b'/') => {
                while i < b.len() && b[i] != b'\n' {
                    i += 1;
                }
            }
            b'"' => {
                i += 1;
                let start = i;
                while i < b.len() && b[i] != b'"' {
                    i += 1;
                }
                last = Some(String::from_utf8_lossy(&b[start..i]).into_owned());
                if i < b.len() {
                    i += 1;
                }
            }
            b'{' => {
                stack.push(last.take().unwrap_or_default());
                if open.is_none() && ends_with_path(&stack, path) {
                    open = Some(i);
                    open_depth = stack.len();
                }
                i += 1;
            }
            b'}' => {
                if let Some(o) = open {
                    if stack.len() == open_depth {
                        return Some(Located { open: o, close: i });
                    }
                }
                stack.pop();
                last = None;
                i += 1;
            }
            c if c.is_ascii_whitespace() => i += 1,
            _ => {
                let start = i;
                while i < b.len() {
                    let ch = b[i];
                    if ch.is_ascii_whitespace() || ch == b'{' || ch == b'}' || ch == b'"' {
                        break;
                    }
                    if ch == b'/' && b.get(i + 1) == Some(&b'/') {
                        break;
                    }
                    i += 1;
                }
                last = Some(String::from_utf8_lossy(&b[start..i]).into_owned());
            }
        }
    }
    None
}

fn ends_with_path(stack: &[String], path: &[&str]) -> bool {
    stack.len() >= path.len()
        && stack[stack.len() - path.len()..]
            .iter()
            .zip(path)
            .all(|(a, b)| a.eq_ignore_ascii_case(b))
}

fn line_of_offset(lines: &[&str], offset: usize) -> usize {
    let mut acc = 0usize;
    for (i, line) in lines.iter().enumerate() {
        acc += line.len();
        if offset < acc {
            return i;
        }
    }
    lines.len().saturating_sub(1)
}

/// The line with any trailing `//` comment removed, ignoring `//` inside quotes.
fn code_part(line: &str) -> &str {
    let b = line.as_bytes();
    let mut quoted = false;
    let mut i = 0usize;
    while i < b.len() {
        match b[i] {
            b'"' => quoted = !quoted,
            b'/' if !quoted && b.get(i + 1) == Some(&b'/') => return &line[..i],
            _ => {}
        }
        i += 1;
    }
    line
}

fn first_token(line: &str) -> Option<&str> {
    let s = code_part(line).trim();
    if s.is_empty() {
        return None;
    }
    match s.strip_prefix('"') {
        Some(rest) => rest.split('"').next().filter(|t| !t.is_empty()),
        None => s.split_whitespace().next(),
    }
}

fn value_token(line: &str) -> Option<&str> {
    let s = code_part(line).trim();
    let rest = match s.strip_prefix('"') {
        Some(r) => &r[r.find('"')? + 1..],
        None => &s[s.find(char::is_whitespace)?..],
    };
    let rest = rest.trim();
    if rest.is_empty() {
        return None;
    }
    match rest.strip_prefix('"') {
        Some(r) => r.split('"').next(),
        None => rest.split_whitespace().next(),
    }
}

fn indent_of(line: &str) -> Option<String> {
    let stripped = line.trim_start();
    if stripped.is_empty() {
        return None;
    }
    Some(line[..line.len() - stripped.len()].to_string())
}

fn newline_for(line: &str, content: &str) -> &'static str {
    if line.ends_with("\r\n") || (!line.ends_with('\n') && content.contains("\r\n")) {
        "\r\n"
    } else {
        "\n"
    }
}

// ── Disk IO ──

fn read_gameinfo(game_dir: &Path) -> Result<String, String> {
    let path = gameinfo_path(game_dir);
    if !path.exists() {
        return Err(format!("gameinfo.gi not found at {}", path.display()));
    }
    std::fs::read_to_string(&path).map_err(|e| format!("Failed to read gameinfo.gi: {}", e))
}

/// One-time safety net under our own name. DMM keeps its vanilla copy in
/// `gameinfo.gi.bak`; overwriting that would destroy its only route back to a
/// clean file.
fn backup_once(path: &Path, content: &str) {
    let backup = path.with_file_name(BACKUP_NAME);
    if backup.exists() {
        return;
    }
    if let Err(e) = std::fs::write(&backup, content) {
        println!("[gameinfo] could not write backup {}: {}", backup.display(), e);
    }
}

fn write_atomic(path: &Path, data: &str) -> Result<(), String> {
    // Distinct from DMM's `gameinfo.gi.tmp`, which it writes and deletes during
    // its own validation pass.
    let tmp = path.with_file_name(TEMP_NAME);
    std::fs::write(&tmp, data).map_err(|e| format!("Failed to stage gameinfo.gi: {}", e))?;
    match std::fs::rename(&tmp, path) {
        Ok(()) => Ok(()),
        Err(e) => {
            let _ = std::fs::remove_file(&tmp);
            if is_sharing_violation(&e) {
                Err("gameinfo.gi is locked. Close Deadlock and try again.".into())
            } else {
                Err(format!("Failed to write gameinfo.gi: {}", e))
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const VANILLA: &str = r#""GameInfo"
{
	game 		"citadel"
	FileSystem
	{
		//
		// 3. If no "Mod" key, for the first "Game" search path, it adds a search path called "MOD".
		//
		SearchPaths
		{
			// These are optional language paths.
			Game_Language		citadel_*LANGUAGE*

			Game_LowViolence	citadel_lv

			Game				citadel
			Game				core
		}
	}
}
"#;

    /// What DMM leaves behind after `setup_game_for_mods`.
    const DMM_PATCHED: &str = r#""GameInfo"
{
	FileSystem
	{
// Deadlock Mod Manager - Start

		SearchPaths
        {
            Game_Language       citadel_*LANGUAGE*
            Game                citadel/addons
            Mod                 citadel
            Write               citadel
            Game                citadel
            Mod                 core
            Write               core
            Game                core
        }
// Deadlock Mod Manager - End
	}
}
"#;

    fn body(content: &str) -> Vec<String> {
        let located = locate_search_paths(content).expect("block");
        let lines: Vec<&str> = content.split_inclusive('\n').collect();
        let open = line_of_offset(&lines, located.open);
        let close = line_of_offset(&lines, located.close);
        lines[open + 1..close]
            .iter()
            .filter_map(|l| first_token(l).map(|k| format!("{k} {}", value_token(l).unwrap_or(""))))
            .collect()
    }

    #[test]
    fn vanilla_gets_entry_above_first_plain_game_with_anchors() {
        let out = render_patch(VANILLA).unwrap();
        assert_eq!(
            body(&out),
            vec![
                "Game_Language citadel_*LANGUAGE*",
                "Game_LowViolence citadel_lv",
                "Game citadel/deadworks_mods",
                "Mod citadel",
                "Write citadel",
                "addonroot citadel/deadworks_addons",
                "Game citadel",
                "Game core",
            ],
            "our Game entry must outrank citadel but stay below the conditional language paths"
        );
    }

    #[test]
    fn anchors_are_omitted_when_dmm_already_declares_them() {
        let out = render_patch(DMM_PATCHED).unwrap();
        let body = body(&out);
        assert_eq!(body.iter().filter(|l| l.starts_with("Mod ")).count(), 2, "DMM's two Mod keys, none added");
        assert_eq!(body.iter().filter(|l| l.starts_with("Write ")).count(), 2);
    }

    #[test]
    fn our_entry_outranks_dmm_mods() {
        let out = render_patch(DMM_PATCHED).unwrap();
        let ours = out.find(MODS_SEARCH_PATH).unwrap();
        let dmm = out.find("citadel/addons").unwrap();
        assert!(ours < dmm, "server Panorama assets must win over user mods");
    }

    #[test]
    fn patch_is_idempotent() {
        let once = render_patch(VANILLA).unwrap();
        let twice = render_patch(&once).unwrap();
        assert_eq!(once, twice);
        let thrice = render_patch(&twice).unwrap();
        assert_eq!(once, thrice);
    }

    #[test]
    fn reassert_after_dmm_wipe_matches_a_fresh_patch() {
        // DMM regenerates the whole block, dropping ours; we put it straight back.
        let ours = render_patch(DMM_PATCHED).unwrap();
        assert_ne!(ours, DMM_PATCHED);
        assert_eq!(render_stripped(&ours).unwrap(), DMM_PATCHED);
    }

    #[test]
    fn strip_restores_the_original_file() {
        assert_eq!(render_stripped(&render_patch(VANILLA).unwrap()).unwrap(), VANILLA);
    }

    #[test]
    fn legacy_bare_addonroot_is_adopted_not_duplicated() {
        let legacy = VANILLA.replace(
            "\t\t\tGame\t\t\t\tcitadel\n",
            "\t\t\taddonroot\tcitadel/old_root\n\t\t\tGame\t\t\t\tcitadel\n",
        );
        let out = render_patch(&legacy).unwrap();
        assert_eq!(out.matches("addonroot").count(), 1);
        assert!(!out.contains("citadel/old_root"));
        assert!(out.contains(ADDONROOT_VALUE));
    }

    #[test]
    fn never_emits_strings_dmm_parses() {
        let out = render_patch(VANILLA).unwrap();
        let added: String = out
            .lines()
            .filter(|l| !VANILLA.lines().any(|v| v == *l))
            .collect::<Vec<_>>()
            .join("\n");
        assert!(!added.is_empty());
        // DMM's status check, its profile-folder scan, its block delimiters,
        // and the token it uses to find its edit range.
        assert!(!added.contains("citadel/addons"));
        assert!(!added.contains("Deadlock Mod Manager"));
        assert!(!added.contains("SearchPaths"));
        assert!(!added.contains('{') && !added.contains('}'));
    }

    #[test]
    fn dmm_range_detection_still_lands_on_the_real_closing_brace() {
        // Mirrors DMM's no-marker path: find("SearchPaths") then the next '}'.
        let out = render_patch(VANILLA).unwrap();
        let start = out.find("SearchPaths").unwrap();
        let end = start + out[start..].find('}').unwrap();
        assert!(out[start..end].contains(MODS_SEARCH_PATH), "our block must fall inside the range DMM replaces");
        assert!(out[start..end].contains("Game\t\t\t\tcore"));
    }

    #[test]
    fn preserves_crlf_and_missing_trailing_newline() {
        let crlf = VANILLA.replace('\n', "\r\n");
        let out = render_patch(&crlf).unwrap();
        assert!(!out.contains("\n\n"), "no bare LF should be introduced");
        assert_eq!(out.matches('\n').count(), out.matches("\r\n").count());

        let no_trailing = VANILLA.trim_end_matches('\n');
        let out = render_patch(no_trailing).unwrap();
        assert!(!out.ends_with('\n'), "trailing newline state must be preserved");
    }

    #[test]
    fn block_with_no_plain_game_entry_appends_before_close() {
        let odd = "\"GameInfo\"\n{\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tGame_Language\tcitadel_*LANGUAGE*\n\t\t}\n\t}\n}\n";
        let out = render_patch(odd).unwrap();
        assert_eq!(
            body(&out),
            vec![
                "Game_Language citadel_*LANGUAGE*",
                "Game citadel/deadworks_mods",
                "Mod citadel",
                "Write citadel",
                "addonroot citadel/deadworks_addons",
            ]
        );
    }

    #[test]
    fn missing_block_is_an_error_not_a_corrupt_write() {
        assert!(render_patch("\"GameInfo\"\n{\n\tgame \"citadel\"\n}\n").is_err());
    }

    #[test]
    fn recorded_error_is_cleared_by_a_later_success() {
        record(&Err("gameinfo.gi is locked.".to_string()));
        assert_eq!(gameinfo_error().as_deref(), Some("gameinfo.gi is locked."));
        // A no-op patch counts: the file already says what we want.
        record(&Ok(false));
        assert_eq!(gameinfo_error(), None);
    }

    #[test]
    fn comments_cannot_be_mistaken_for_entries() {
        assert_eq!(first_token("\t\t// Game citadel/addons"), None);
        assert!(!is_owned_entry("\t\t// addonroot citadel/deadworks_addons"));
        assert!(is_owned_entry("\t\t\tGame\t\tcitadel/deadworks_mods\t// ours"));
        assert!(!is_owned_entry("\t\t\tGame\t\tcitadel/addons"));
        assert!(!is_owned_entry("\t\t\tGame_Language\t\tcitadel_*LANGUAGE*"));
    }
}

