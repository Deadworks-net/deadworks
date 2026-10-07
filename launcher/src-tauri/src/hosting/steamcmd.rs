//! SteamCMD: the alternative game source for people without a local Deadlock
//! install (or who want the server fully independent of it).
//!
//! Deadlock can't be downloaded anonymously, so this needs the user's Steam
//! login. The password is typed into SteamCMD's own prompt (never put on the
//! command line, never stored); SteamCMD caches a login token itself, so later
//! updates only need the username. Steam Guard codes and mobile confirmations
//! are relayed to the UI as prompts. SteamCMD runs under the same hidden-console
//! scheme as the servers, which is what lets its prompts read our input.

use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::sync::mpsc;
use std::time::Duration;

use super::download;
use super::fsutil;
use super::manifest::{self, AppManifest, DepotFile, DEADLOCK_APP_ID};
use super::process::SpawnSpec;
use super::pty;
use super::store::Layout;
use super::task::{Progress, CANCELLED};
use super::types::SteamPrompt;

const BOOTSTRAP_URL: &str = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

pub fn exe(layout: &Layout) -> PathBuf {
    layout.steamcmd_dir().join("steamcmd.exe")
}

pub fn appmanifest_path(layout: &Layout) -> PathBuf {
    layout.base_dir().join("steamapps").join(format!("appmanifest_{DEADLOCK_APP_ID}.acf"))
}

pub fn depotcache_dirs(layout: &Layout) -> Vec<PathBuf> {
    vec![
        layout.steamcmd_dir().join("depotcache"),
        layout.base_dir().join("steamapps").join("depotcache"),
        layout.base_dir().join("depotcache"),
    ]
}

/// The base's manifest and file list after SteamCMD installed or updated it.
pub fn installed(layout: &Layout) -> Result<(AppManifest, Vec<DepotFile>), String> {
    let app = manifest::read_appmanifest(&appmanifest_path(layout))?;
    let files = manifest::load_depot_files(&app, &depotcache_dirs(layout))?;
    Ok((app, files))
}

fn ensure_installed(layout: &Layout, progress: &Progress) -> Result<(), String> {
    if exe(layout).is_file() {
        return Ok(());
    }
    progress.stage("steamcmd", "Downloading SteamCMD", 0, 1);
    let zip = layout.downloads_dir().join("steamcmd.zip");
    download::to_file(BOOTSTRAP_URL, &zip, progress)?;
    fsutil::extract_zip(&zip, &layout.steamcmd_dir())?;
    let _ = std::fs::remove_file(&zip);
    Ok(())
}

enum Line {
    Out(String),
    Exit(i32),
}

/// Run SteamCMD with `args`, answering its prompts. Returns its output.
fn run(layout: &Layout, args: Vec<String>, password: Option<&str>, progress: &Progress) -> Result<Vec<String>, String> {
    let (tx, rx) = mpsc::channel::<Line>();
    let tx2 = tx.clone();
    let proc = pty::spawn(
        SpawnSpec {
            exe: exe(layout),
            cwd: layout.steamcmd_dir(),
            args,
            env: Vec::new(),
            rcon: None,
        },
        move |line| {
            let _ = tx.send(Line::Out(line));
        },
        move |code| {
            let _ = tx2.send(Line::Exit(code));
        },
    )?;
    let mut output = Vec::new();
    let mut password_sent = false;
    let result = loop {
        if progress.cancelled() {
            proc.terminate();
            break Err(CANCELLED.to_string());
        }
        let line = match rx.recv_timeout(Duration::from_millis(250)) {
            Ok(Line::Out(l)) => l,
            Ok(Line::Exit(code)) => break Ok(code),
            Err(mpsc::RecvTimeoutError::Timeout) => continue,
            Err(_) => break Ok(0),
        };
        let lower = line.to_ascii_lowercase();
        if lower.trim_end().ends_with("password:") {
            let answer = match password.filter(|_| !password_sent) {
                Some(p) => p.to_string(),
                None => {
                    progress.set_prompt(Some(SteamPrompt::Password));
                    progress.label("Enter your Steam password");
                    progress.wait_input()?
                }
            };
            password_sent = true;
            progress.set_prompt(None);
            proc.send_line(&answer)?;
            continue;
        }
        let is_prompt = lower.trim_end().ends_with(':');
        if is_prompt && ["steam guard code", "two-factor code", "auth code"].iter().any(|k| lower.contains(k)) {
            progress.set_prompt(Some(SteamPrompt::GuardCode));
            progress.label("Enter your Steam Guard code");
            let code = progress.wait_input()?;
            progress.set_prompt(None);
            progress.label("Logging in to Steam");
            proc.send_line(code.trim())?;
            continue;
        }
        if lower.contains("confirm the login in the steam mobile app") {
            progress.set_prompt(Some(SteamPrompt::MobileConfirm));
            progress.label("Approve the login in your Steam Mobile app");
        } else if lower.contains("waiting for user info") || lower.contains("logged in ok") {
            progress.set_prompt(None);
        }
        if let Some((done, total)) = parse_progress(&line) {
            progress.bytes_total.store(total, Ordering::Relaxed);
            progress.bytes_done.store(done, Ordering::Relaxed);
            progress.label(if lower.contains("verifying") { "Verifying game files" } else { "Downloading Deadlock" });
        }
        output.push(line);
    };
    progress.set_prompt(None);
    match result {
        Ok(_) => Ok(output),
        Err(e) => Err(e),
    }
}

/// ` Update state (0x61) downloading, progress: 12.34 (4509715660 / 36514514979)`
fn parse_progress(line: &str) -> Option<(u64, u64)> {
    let start = line.find("progress:")?;
    let open = line[start..].find('(')? + start + 1;
    let close = line[open..].find(')')? + open;
    let (a, b) = line[open..close].split_once('/')?;
    Some((a.trim().parse().ok()?, b.trim().parse().ok()?))
}

/// Explain SteamCMD's failure lines in plain words.
fn failure(output: &[String]) -> Option<String> {
    for l in output.iter().rev() {
        let lower = l.to_ascii_lowercase();
        let msg = if lower.contains("invalid password") {
            "Steam rejected the password. Check it and try again."
        } else if lower.contains("no subscription") {
            "This Steam account doesn't own Deadlock."
        } else if lower.contains("rate limit") {
            "Steam is rate-limiting logins from this PC. Wait a few minutes and try again."
        } else if lower.contains("invalid login auth code") || lower.contains("two-factor code mismatch") || lower.contains("invalid auth code") {
            "The Steam Guard code was wrong or expired. Try again with a new code."
        } else if lower.contains("not enough disk space") || lower.contains("disk space") {
            "Not enough free disk space for Deadlock."
        } else if lower.starts_with("error!") || lower.contains("failed (") {
            return Some(format!("SteamCMD failed: {}", l.trim()));
        } else {
            continue;
        };
        return Some(msg.to_string());
    }
    None
}

/// Install or update Deadlock into the base with the user's account.
pub fn install(
    layout: &Layout,
    username: &str,
    password: Option<&str>,
    validate: bool,
    progress: &Progress,
) -> Result<(), String> {
    if username.trim().is_empty() || username.contains(char::is_whitespace) {
        return Err("Enter your Steam username.".into());
    }
    ensure_installed(layout, progress)?;
    progress.stage("steamcmd", "Updating SteamCMD", 0, 0);
    // First run self-updates; a quiet pass keeps its output out of the login run.
    run(layout, vec!["+quit".into()], None, progress)?;

    std::fs::create_dir_all(layout.base_dir()).map_err(|e| e.to_string())?;
    // SteamCMD updates files in place; the base is read-only between updates.
    set_tree_readonly(&layout.base_dir(), false);
    progress.stage("game", "Logging in to Steam", 0, 0);
    let mut args = vec![
        "+@ShutdownOnFailedCommand".to_string(),
        "1".into(),
        "+force_install_dir".into(),
        layout.base_dir().to_string_lossy().into_owned(),
        "+login".into(),
        username.trim().to_string(),
        "+app_update".into(),
        DEADLOCK_APP_ID.into(),
    ];
    if validate {
        args.push("validate".into());
    }
    args.push("+quit".into());
    let started = std::time::SystemTime::now();
    let output = run(layout, args, password, progress);
    set_tree_readonly(&layout.base_dir().join("game"), true);
    let output = output?;
    if succeeded(&output) {
        return Ok(());
    }
    if let Some(reason) = failure(&output) {
        return Err(reason);
    }
    // No verdict in the output. SteamCMD's own record decides: a 36 GB download must not be
    // called failed because a line went missing.
    if finished_since(layout, started) {
        return Ok(());
    }
    Err("SteamCMD didn't finish installing Deadlock.".into())
}

fn succeeded(output: &[String]) -> bool {
    output.iter().any(|l| l.contains("Success! App") && l.contains(DEADLOCK_APP_ID))
}

/// SteamCMD marked the app fully installed during this run.
fn finished_since(layout: &Layout, started: std::time::SystemTime) -> bool {
    let path = appmanifest_path(layout);
    let written = std::fs::metadata(&path).and_then(|m| m.modified()).is_ok_and(|t| t >= started);
    written && manifest::read_appmanifest(&path).is_ok_and(|app| !app.updating())
}

/// The public branch's current build id. Deadlock's depot and branch info is
/// hidden from anonymous logins (and Steam's UpToDateCheck API doesn't serve
/// it), so this logs in as the saved user on SteamCMD's cached token, never
/// prompting: in the background nobody is there to type a password.
pub fn latest_build(layout: &Layout, username: &str, progress: &Progress) -> Result<String, String> {
    ensure_installed(layout, progress)?;
    let output = run(
        layout,
        vec![
            "+@NoPromptForPassword".into(),
            "1".into(),
            "+login".into(),
            username.to_string(),
            "+app_info_update".into(),
            "1".into(),
            "+app_info_print".into(),
            DEADLOCK_APP_ID.into(),
            "+quit".into(),
        ],
        None,
        progress,
    )?;
    let text = output.join("\n");
    let start = text.find(&format!("\"{DEADLOCK_APP_ID}\"")).ok_or("SteamCMD returned no app info for Deadlock")?;
    let info = manifest::parse_vdf(&text[start..]);
    info.get(DEADLOCK_APP_ID)
        .and_then(|a| a.as_map()?.get("depots")?.as_map()?.get("branches")?.as_map()?.get("public")?.as_map()?.get("buildid")?.as_str().map(String::from))
        .ok_or_else(|| "SteamCMD's app info has no public build id".into())
}

fn set_tree_readonly(dir: &Path, readonly: bool) {
    let Ok(rd) = std::fs::read_dir(dir) else { return };
    for e in rd.flatten() {
        let p = e.path();
        if p.is_dir() {
            set_tree_readonly(&p, readonly);
        } else {
            let _ = fsutil::set_readonly(&p, readonly);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn progress_lines() {
        assert_eq!(
            parse_progress(" Update state (0x61) downloading, progress: 12.34 (4509715660 / 36514514979)"),
            Some((4_509_715_660, 36_514_514_979))
        );
        assert_eq!(parse_progress("Logging in user 'x' to Steam Public...OK"), None);
    }

    /// Real SteamCMD with an account whose login token SteamCMD has cached:
    /// `DW_STEAMCMD_TEST_ROOT=H:\... DW_STEAMCMD_TEST_USER=name cargo test -- --ignored steamcmd_live`
    #[test]
    #[ignore]
    fn steamcmd_live_build_lookup() {
        let root = std::env::var("DW_STEAMCMD_TEST_ROOT").expect("set DW_STEAMCMD_TEST_ROOT");
        let user = std::env::var("DW_STEAMCMD_TEST_USER").expect("set DW_STEAMCMD_TEST_USER");
        let layout = Layout::new(root);
        let p = Progress::new(crate::hosting::types::TaskKind::Update);
        let build = latest_build(&layout, &user, &p).unwrap();
        assert!(build.parse::<u64>().is_ok(), "{build}");
        println!("public build {build}");
    }

    /// More than a screenful of output before the result line: SteamCMD's last lines must still
    /// arrive (they were once lost to `\r\n` splitting across reads). Needs a cached login and
    /// an installed base in `DW_STEAMCMD_TEST_ROOT`.
    #[test]
    #[ignore]
    fn steamcmd_live_result_line_survives_long_output() {
        let root = std::env::var("DW_STEAMCMD_TEST_ROOT").expect("set DW_STEAMCMD_TEST_ROOT");
        let user = std::env::var("DW_STEAMCMD_TEST_USER").expect("set DW_STEAMCMD_TEST_USER");
        let layout = Layout::new(root);
        let p = Progress::new(crate::hosting::types::TaskKind::Update);
        let base = layout.base_dir().to_string_lossy().into_owned();
        let args = [
            "+@ShutdownOnFailedCommand", "1", "+@NoPromptForPassword", "1", "+force_install_dir", &base,
            "+login", &user, "+app_info_print", DEADLOCK_APP_ID, "+app_update", DEADLOCK_APP_ID, "+quit",
        ];
        let out = run(&layout, args.iter().map(|s| s.to_string()).collect(), None, &p).unwrap();
        assert!(out.len() > 100, "expected a long listing, got {} lines", out.len());
        assert!(succeeded(&out), "no result line in {} lines; last: {:?}", out.len(), out.last());
    }

    /// A login with a made-up account must surface the password prompt to the
    /// UI (proving prompts arrive through the pipe) and then fail readably.
    #[test]
    #[ignore]
    fn steamcmd_live_password_prompt_reaches_the_ui() {
        let root = std::env::var("DW_STEAMCMD_TEST_ROOT").expect("set DW_STEAMCMD_TEST_ROOT");
        let layout = Layout::new(root);
        let p = std::sync::Arc::new(Progress::new(crate::hosting::types::TaskKind::Install));
        let watcher = {
            let p = p.clone();
            std::thread::spawn(move || {
                for _ in 0..600 {
                    if p.snapshot().steam_prompt == Some(SteamPrompt::Password) {
                        p.submit_input("definitely-wrong-password".into());
                        return true;
                    }
                    std::thread::sleep(Duration::from_millis(100));
                }
                false
            })
        };
        let user = format!("dwtest{}", std::process::id());
        let err = install(&layout, &user, None, false, &p).unwrap_err();
        assert!(watcher.join().unwrap(), "password prompt never reached the UI");
        println!("failed as expected: {err}");
    }

    #[test]
    fn failures_read_as_sentences() {
        let out = vec!["Logging in user 'x' to Steam Public...".to_string(), "FAILED (Invalid Password)".into()];
        assert_eq!(failure(&out).unwrap(), "Steam rejected the password. Check it and try again.");
        let out = vec!["ERROR! Failed to install app '1422450' (No subscription)".to_string()];
        assert_eq!(failure(&out).unwrap(), "This Steam account doesn't own Deadlock.");
    }
}
