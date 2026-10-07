//! Running the (Windows-only) server on Linux through Wine.
//!
//! This follows `docker/entrypoint.sh`, which is verified on Debian's 64-bit Wine: a plain Wine
//! prefix, the Windows .NET runtime reached through `DOTNET_ROOT=Z:\...`, and real Steam auth from
//! the Steamworks redistributable (app 1007, fetched anonymously with DepotDownloader) placed
//! beside the exe, since there is no Windows Steam client under Wine. The launcher's version of it
//! has not been run on a Linux desktop yet.

use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use super::download;
use super::fsutil;
use super::manifest::DEADLOCK_APP_ID;
use super::store::Layout;
use super::task::Progress;

/// Pinned to the release the Docker image is tested with.
const DEPOTDOWNLOADER_VERSION: &str = "3.4.0";
const REDIST_APP: &str = "1007";
const REDIST_DLLS: &[&str] = &["steamclient64.dll", "tier0_s64.dll", "vstdlib_s64.dll"];
/// Keeps Wine from offering to install Gecko or creating desktop menu entries.
const DLL_OVERRIDES: &str = "mshtml,winemenubuilder.exe=d";

pub fn prefix_dir(layout: &Layout) -> PathBuf {
    layout.root.join("cache").join("wineprefix")
}

fn redist_dir(layout: &Layout) -> PathBuf {
    layout.root.join("cache").join("steam-redist")
}

fn depotdownloader_dir(layout: &Layout) -> PathBuf {
    layout.root.join("cache").join("depotdownloader")
}

/// A Unix path as Wine programs see it: the `Z:` drive maps to `/`.
pub fn windows_path(path: &Path) -> String {
    format!("Z:{}", path.to_string_lossy().replace('/', "\\"))
}

/// Environment for every Wine process of this hosting root.
pub fn env(layout: &Layout) -> Vec<(String, String)> {
    vec![
        ("WINEPREFIX".into(), prefix_dir(layout).to_string_lossy().into_owned()),
        ("WINEDEBUG".into(), "-all".into()),
        ("WINEDLLOVERRIDES".into(), DLL_OVERRIDES.into()),
        // CoreCLR's W^X remapping is slow and fragile under Wine.
        ("DOTNET_EnableWriteXorExecute".into(), "0".into()),
    ]
}

/// What has to be installed before hosting can work, as sentences for the setup screen.
pub fn missing_tools() -> Vec<String> {
    let has = |tool: &str| {
        Command::new(tool)
            .arg("--version")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .map(|s| s.success())
            .unwrap_or(false)
    };
    if has("wine") && has("wineserver") {
        Vec::new()
    } else {
        vec!["Hosting on Linux needs Wine (64-bit, version 9 or newer). Install it with your package manager, then check again.".into()]
    }
}

fn run(layout: &Layout, program: &str, args: &[&str], extra_env: &[(&str, &str)]) -> Result<(), String> {
    let status = Command::new(program)
        .args(args)
        .envs(env(layout))
        .envs(extra_env.iter().copied())
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map_err(|e| format!("Couldn't run {program}: {e}"))?;
    if status.success() {
        Ok(())
    } else {
        Err(format!("{program} failed ({status})"))
    }
}

/// Create the Wine prefix once (about half a minute).
pub fn ensure_prefix(layout: &Layout, progress: &Progress) -> Result<(), String> {
    let prefix = prefix_dir(layout);
    if prefix.join("system.reg").is_file() {
        return Ok(());
    }
    progress.stage("wine", "Setting up Wine", 0, 0);
    std::fs::create_dir_all(&prefix).map_err(|e| e.to_string())?;
    // mscoree is disabled for this one command only, so wineboot doesn't try to install
    // wine-mono. It must stay enabled at runtime: Wine maps every managed DLL through it.
    let overrides = format!("mscoree,{DLL_OVERRIDES}");
    let _ = run(layout, "wineboot", &["--init"], &[("WINEDLLOVERRIDES", overrides.as_str())]);
    let _ = run(layout, "wineserver", &["-w"], &[]);
    if prefix.join("system.reg").is_file() {
        Ok(())
    } else {
        Err("Wine couldn't create its prefix. Check that `wineboot --init` works on this PC.".into())
    }
}

/// Download the Steam client DLLs the server needs to log on to Steam.
pub fn ensure_redist(layout: &Layout, progress: &Progress) -> Result<(), String> {
    let redist = redist_dir(layout);
    if REDIST_DLLS.iter().all(|f| redist.join(f).is_file()) {
        return Ok(());
    }
    let tool_dir = depotdownloader_dir(layout);
    let tool = tool_dir.join("DepotDownloader");
    if !tool.is_file() {
        progress.stage("steam", "Downloading Steam's server files", 0, 0);
        let zip = layout.downloads_dir().join("depotdownloader.zip");
        download::to_file(
            &format!(
                "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_{DEPOTDOWNLOADER_VERSION}/DepotDownloader-linux-x64.zip"
            ),
            &zip,
            progress,
        )?;
        fsutil::extract_zip(&zip, &tool_dir)?;
        let _ = std::fs::remove_file(&zip);
        make_executable(&tool);
    }
    progress.stage("steam", "Downloading Steam's server files", 0, 0);
    let home = tool_dir.join("home");
    std::fs::create_dir_all(&home).map_err(|e| e.to_string())?;
    let status = Command::new(&tool)
        .args(["-app", REDIST_APP, "-os", "windows", "-osarch", "64", "-dir"])
        .arg(&redist)
        // Its login token and config go under the hosting root, not the user's home.
        .env("HOME", &home)
        // The self-contained build otherwise needs the system's ICU libraries.
        .env("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map_err(|e| format!("Couldn't run DepotDownloader: {e}"))?;
    if !status.success() || !REDIST_DLLS.iter().all(|f| redist.join(f).is_file()) {
        return Err("Couldn't download Steam's server files (Steamworks redistributable). Check your connection and try again.".into());
    }
    Ok(())
}

/// Put the Steam client DLLs and steam_appid.txt beside a server's deadworks.exe.
pub fn install_redist(layout: &Layout, bin_dir: &Path) -> Result<(), String> {
    for f in REDIST_DLLS {
        let data = std::fs::read(redist_dir(layout).join(f)).map_err(|e| format!("Steam's server files are missing ({f}): {e}"))?;
        // Copied, not linked: a refresh must not rewrite a DLL a running server has mapped.
        fsutil::write_if_changed(&bin_dir.join(f), &data).map_err(|e| format!("Couldn't install {f}: {e}"))?;
    }
    fsutil::write_if_changed(&bin_dir.join("steam_appid.txt"), format!("{DEADLOCK_APP_ID}\n").as_bytes())
        .map_err(|e| e.to_string())?;
    Ok(())
}

fn make_executable(path: &Path) {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        if let Ok(meta) = std::fs::metadata(path) {
            let mut perms = meta.permissions();
            perms.set_mode(perms.mode() | 0o755);
            let _ = std::fs::set_permissions(path, perms);
        }
    }
    #[cfg(not(unix))]
    let _ = path;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unix_paths_map_onto_the_z_drive() {
        assert_eq!(windows_path(Path::new("/home/me/Deadworks Servers/cache/dotnet/10.0.12")), "Z:\\home\\me\\Deadworks Servers\\cache\\dotnet\\10.0.12");
    }
}
