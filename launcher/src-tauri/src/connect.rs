use std::fs;
use std::path::PathBuf;
use std::sync::Mutex;
use std::env;

const DEADLOCK_APP_ID: &str = "1422450";

/// User override for the Deadlock game directory. When set, bypasses auto-detection.
static GAME_DIR_OVERRIDE: Mutex<Option<PathBuf>> = Mutex::new(None);

pub(crate) fn set_game_dir_override(path: Option<PathBuf>) {
    *GAME_DIR_OVERRIDE.lock().unwrap() = path;
}

pub(crate) fn get_game_dir_override() -> Option<PathBuf> {
    GAME_DIR_OVERRIDE.lock().unwrap().clone()
}

/// Find Steam install path from the Windows registry.
#[cfg(windows)]
fn find_steam_path() -> Result<PathBuf, String> {
    use winreg::enums::HKEY_LOCAL_MACHINE;
    use winreg::RegKey;

    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let steam_key = hklm
        .open_subkey("SOFTWARE\\WOW6432Node\\Valve\\Steam")
        .map_err(|e| format!("Failed to open Steam registry key: {}", e))?;
    let install_path: String = steam_key
        .get_value("InstallPath")
        .map_err(|e| format!("Failed to read Steam InstallPath: {}", e))?;
    Ok(PathBuf::from(install_path))
}

#[cfg(target_os = "linux")]
fn find_steam_path() -> Result<PathBuf, String> {
    match env::home_dir() {
        Some(path) => {
            let native_path = path.join(".steam").join("steam");
            let flatpak_path = path
                .join(".var")
                .join("app")
                .join("com.valvesoftware.Steam")
                .join("data")
                .join("Steam");
            if native_path.exists() {
                return Ok(native_path)
            } else if flatpak_path.exists() {
                return Ok(flatpak_path)
            }
            Err("Failed to find Steam directory".into())
        },
        None => Err("Failed to find the home directory".into()),
    }
}


/// Parse libraryfolders.vdf and return the library path whose `apps` block
/// lists `app_id`. Returns an error if no library claims that app.
fn find_library_for_app(steam_path: &PathBuf, app_id: &str) -> Result<PathBuf, String> {
    let vdf_path = steam_path.join("steamapps").join("libraryfolders.vdf");
    let content = fs::read_to_string(&vdf_path)
        .map_err(|e| format!("Failed to read {}: {}", vdf_path.display(), e))?;

    // libraryfolders.vdf nesting:
    //   depth 1: "libraryfolders" block
    //   depth 2: each "<index>" library entry — has "path" and "apps"
    //   depth 3: entries inside "apps" (the only nested block at depth 2)
    let mut depth: u32 = 0;
    let mut current_path: Option<String> = None;
    let mut found_app = false;

    for line in content.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        if trimmed == "{" {
            depth += 1;
            continue;
        }
        if trimmed == "}" {
            if depth == 2 {
                if found_app {
                    if let Some(p) = current_path.take() {
                        return Ok(PathBuf::from(p));
                    }
                }
                current_path = None;
                found_app = false;
            }
            depth = depth.saturating_sub(1);
            continue;
        }

        let Some((key, value)) = parse_vdf_kv(trimmed) else { continue };
        match depth {
            2 if key == "path" => current_path = value,
            3 if key == app_id => found_app = true,
            _ => {}
        }
    }

    Err(format!("App ID {} not found in any Steam library", app_id))
}

/// Parse a single VDF key/value line: `"key"` or `"key" "value"`.
fn parse_vdf_kv(line: &str) -> Option<(String, Option<String>)> {
    let (key, rest) = read_quoted(line)?;
    let value = read_quoted(rest).map(|(v, _)| v);
    Some((key, value))
}

/// Read the next `"..."` token (with `\\`, `\"`, `\n`, `\t` escapes) and return
/// the unescaped contents plus the slice that follows the closing quote.
fn read_quoted(s: &str) -> Option<(String, &str)> {
    let open = s.find('"')?;
    let body_start = open + 1;
    let body = &s[body_start..];

    let bytes = body.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b'\\' if i + 1 < bytes.len() => i += 2,
            b'"' => {
                let raw = &body[..i];
                let after = &body[i + 1..];
                return Some((unescape_vdf(raw), after));
            }
            _ => i += 1,
        }
    }
    None
}

fn unescape_vdf(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut chars = s.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.next() {
            Some('\\') => out.push('\\'),
            Some('"') => out.push('"'),
            Some('n') => out.push('\n'),
            Some('t') => out.push('\t'),
            Some(other) => {
                out.push('\\');
                out.push(other);
            }
            None => out.push('\\'),
        }
    }
    out
}

/// Find Deadlock's `game` directory (parent of `citadel/`) in the Steam library
/// that owns the app. Callers append whatever subpath they need.
#[cfg(any(windows, target_os = "linux"))]
pub(crate) fn find_deadlock_game_dir() -> Result<PathBuf, String> {
    let steam_path = find_steam_path()?;
    let library = find_library_for_app(&steam_path, DEADLOCK_APP_ID)?;
    let game_dir = library
        .join("steamapps")
        .join("common")
        .join("Deadlock")
        .join("game");
    if !game_dir.join("citadel").exists() {
        return Err(format!(
            "Deadlock game directory not found at {}",
            game_dir.display()
        ));
    }
    Ok(game_dir)
}

#[cfg(target_os = "macos")]
pub(crate) fn find_deadlock_game_dir() -> Result<PathBuf, String> {
    Err("Deadlock detection is only supported on Windows and Linux".into())
}

#[derive(serde::Serialize)]
pub struct ConnectResult {
    success: bool,
    method: String,
    message: String,
}

/// Open `steam://connect/<addr>` which tells Steam to launch/join the server.
/// `addr` must be a raw `ip:port` pair; anything else is rejected so the API
/// (or a deep link) cannot smuggle extra URL segments into Steam's handler.
pub(crate) fn connect_to_server_inner(addr: &str) -> Result<ConnectResult, String> {
    if !crate::deep_link::is_valid_ip_port(addr) {
        return Err(format!("invalid server address: {}", addr));
    }
    let steam_url = format!("steam://connect/{}", addr);
    open::that(&steam_url).map_err(|e| format!("Failed to open Steam: {}", e))?;
    Ok(ConnectResult {
        success: true,
        method: "steam_connect".into(),
        message: format!("Opening steam://connect/{}", addr),
    })
}

#[tauri::command]
pub fn launch_deadlock() -> Result<(), String> {
    let steam_url = format!("steam://run/{}", DEADLOCK_APP_ID);
    open::that(&steam_url).map_err(|e| format!("Failed to launch Deadlock: {}", e))
}

/// Returns the auto-detected game directory (ignoring any override).
#[tauri::command]
pub fn get_detected_game_dir() -> Result<String, String> {
    Ok(find_deadlock_game_dir()?.to_string_lossy().to_string())
}

/// Returns the current effective game directory (override or auto-detected).
#[tauri::command]
pub fn get_game_dir() -> Result<String, String> {
    if let Some(override_dir) = get_game_dir_override() {
        return Ok(override_dir.to_string_lossy().to_string());
    }
    get_detected_game_dir()
}

/// Set a manual override for the game directory.
#[tauri::command]
pub fn set_game_dir(path: String) -> Result<(), String> {
    let p = PathBuf::from(&path);
    let citadel = p.join("citadel");
    if !citadel.exists() {
        return Err(format!(
            "Invalid Deadlock directory: expected a 'citadel' folder inside '{}'",
            path
        ));
    }
    set_game_dir_override(Some(p));
    Ok(())
}

/// Clear the manual override, reverting to auto-detection.
#[tauri::command]
pub fn reset_game_dir() {
    set_game_dir_override(None);
}
