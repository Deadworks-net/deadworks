//! A private .NET runtime for the servers.
//!
//! Deadworks hosts CoreCLR through `nethost`, which honours `DOTNET_ROOT`, so a
//! runtime zip extracted under the hosting root works without the system-wide
//! installer (and its UAC prompt). Verified: with `DOTNET_ROOT` set, `hostfxr`
//! and `coreclr` load from here even when .NET 10 is also installed globally.

use std::path::PathBuf;

use serde::Deserialize;

use super::download;
use super::fsutil;
use super::store::Layout;
use super::task::Progress;

const METADATA_URL: &str = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";
const ZIP_NAME: &str = "dotnet-runtime-win-x64.zip";
const COMPLETE_MARKER: &str = ".complete";

#[derive(Deserialize)]
struct Metadata {
    #[serde(rename = "latest-runtime")]
    latest_runtime: String,
    releases: Vec<Release>,
}

#[derive(Deserialize)]
struct Release {
    runtime: Option<Runtime>,
}

#[derive(Deserialize)]
struct Runtime {
    version: String,
    files: Vec<RuntimeFile>,
}

#[derive(Deserialize)]
struct RuntimeFile {
    name: String,
    url: String,
    hash: String,
}

pub fn runtime_dir(layout: &Layout, version: &str) -> PathBuf {
    layout.dotnet_cache().join(version)
}

pub fn is_installed(layout: &Layout, version: &str) -> bool {
    runtime_dir(layout, version).join(COMPLETE_MARKER).is_file()
}

/// Make sure the newest .NET 10 runtime is present; returns its version.
/// Offline, an already-installed runtime is kept rather than failing.
pub fn ensure(layout: &Layout, current: Option<&str>, progress: &Progress) -> Result<String, String> {
    let meta: Metadata = match download::get_text(METADATA_URL).and_then(|t| serde_json::from_str(&t).map_err(|e| e.to_string())) {
        Ok(m) => m,
        Err(e) => {
            return match current.filter(|v| is_installed(layout, v)) {
                Some(v) => Ok(v.to_string()),
                None => Err(format!("Couldn't download .NET: {e}")),
            };
        }
    };
    let version = meta.latest_runtime.clone();
    // It becomes a folder name, and that folder gets replaced.
    let plain = !version.is_empty()
        && version.len() < 64
        && version.starts_with(|c: char| c.is_ascii_digit())
        && version.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-'));
    if !plain {
        return Err(format!("Couldn't download .NET: unexpected version '{version}'"));
    }
    if is_installed(layout, &version) {
        return Ok(version);
    }
    let file = meta
        .releases
        .iter()
        .filter_map(|r| r.runtime.as_ref())
        .find(|r| r.version == version)
        .and_then(|r| r.files.iter().find(|f| f.name == ZIP_NAME))
        .ok_or_else(|| format!(".NET {version} has no Windows runtime download"))?;

    progress.stage("dotnet", &format!("Downloading .NET {version}"), 0, 1);
    let zip = layout.downloads_dir().join(format!("dotnet-runtime-{version}.zip"));
    let sha512 = download::to_file(&file.url, &zip, progress)?;
    if !download::hex(&sha512).eq_ignore_ascii_case(&file.hash) {
        let _ = std::fs::remove_file(&zip);
        return Err(".NET download was corrupted (checksum mismatch). Try again.".into());
    }
    let dir = runtime_dir(layout, &version);
    let staging = dir.with_extension("extracting");
    let _ = fsutil::remove_dir_all_force(&staging);
    fsutil::extract_zip(&zip, &staging)?;
    let _ = fsutil::remove_dir_all_force(&dir);
    std::fs::rename(&staging, &dir).map_err(|e| format!("Couldn't install .NET: {e}"))?;
    std::fs::write(dir.join(COMPLETE_MARKER), b"").map_err(|e| e.to_string())?;
    let _ = std::fs::remove_file(&zip);
    Ok(version)
}

/// Remove runtimes other than `keep`. A running server holds its runtime's
/// DLLs open, so a failed delete is simply retried next time.
pub fn prune(layout: &Layout, keep: &str) {
    let Ok(rd) = std::fs::read_dir(layout.dotnet_cache()) else { return };
    for e in rd.flatten() {
        if e.file_name() != keep && e.path().is_dir() {
            let _ = fsutil::remove_dir_all_force(&e.path());
        }
    }
}
