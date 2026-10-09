//! Deadworks releases from GitHub.
//!
//! "Latest" is resolved from the `/releases/latest` redirect rather than the
//! REST API, which rate-limits unauthenticated clients (the Docker image does
//! the same). Each release is extracted once into `cache\deadworks\<tag>` and
//! laid over every server tree; the zip is rooted at the install dir
//! (`game\bin\win64\deadworks.exe`, ...).

use std::path::{Path, PathBuf};
use std::time::Duration;

use super::download;
use super::fsutil;
use super::store::Layout;
use super::task::Progress;

const REPO: &str = "Deadworks-net/deadworks";
const COMPLETE_MARKER: &str = ".complete";
const KEEP: usize = 3;

pub fn latest_tag() -> Result<String, String> {
    let client = reqwest::blocking::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .user_agent(concat!("deadworks-launcher/", env!("CARGO_PKG_VERSION")))
        .timeout(Duration::from_secs(20))
        .build()
        .map_err(|e| e.to_string())?;
    let resp = client
        .get(format!("https://github.com/{REPO}/releases/latest"))
        .send()
        .map_err(|e| format!("Couldn't check for Deadworks updates: {e}"))?;
    let location = resp
        .headers()
        .get(reqwest::header::LOCATION)
        .and_then(|v| v.to_str().ok())
        .ok_or("Couldn't check for Deadworks updates: GitHub returned no release")?;
    tag_from_location(location).ok_or_else(|| format!("Unexpected release URL from GitHub: {location}"))
}

fn tag_from_location(location: &str) -> Option<String> {
    let tag = location.rsplit_once("/releases/tag/")?.1.trim_end_matches('/');
    valid_tag(tag).then(|| tag.to_string())
}

/// Tags become path segments and URL parts; keep them boring.
fn valid_tag(tag: &str) -> bool {
    !tag.is_empty() && tag.len() < 64 && tag.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '_'))
}

pub fn release_dir(layout: &Layout, tag: &str) -> PathBuf {
    layout.deadworks_cache().join(tag)
}

pub fn is_installed(layout: &Layout, tag: &str) -> bool {
    release_dir(layout, tag).join(COMPLETE_MARKER).is_file()
}

/// Download and extract `tag` unless it is already cached.
pub fn ensure(layout: &Layout, tag: &str, progress: &Progress) -> Result<PathBuf, String> {
    if !valid_tag(tag) {
        return Err(format!("Invalid Deadworks version '{tag}'"));
    }
    let dir = release_dir(layout, tag);
    if is_installed(layout, tag) {
        return Ok(dir);
    }
    progress.stage("deadworks", &format!("Downloading Deadworks {tag}"), 0, 1);
    let zip = layout.downloads_dir().join(format!("deadworks-{tag}.zip"));
    download::to_file(&format!("https://github.com/{REPO}/releases/download/{tag}/deadworks-{tag}.zip"), &zip, progress)?;
    let staging = dir.with_extension("extracting");
    let _ = fsutil::remove_dir_all_force(&staging);
    let names = fsutil::extract_zip(&zip, &staging)?;
    if !names.iter().any(|n| n.eq_ignore_ascii_case("game/bin/win64/deadworks.exe")) {
        let _ = fsutil::remove_dir_all_force(&staging);
        return Err(format!("The Deadworks {tag} download doesn't contain deadworks.exe"));
    }
    let _ = fsutil::remove_dir_all_force(&dir);
    std::fs::rename(&staging, &dir).map_err(|e| format!("Couldn't install Deadworks {tag}: {e}"))?;
    std::fs::write(dir.join(COMPLETE_MARKER), b"").map_err(|e| e.to_string())?;
    let _ = std::fs::remove_file(&zip);
    Ok(dir)
}

/// Every file of an extracted release, relative to its root, `\`-separated.
pub fn files(dir: &Path) -> Vec<String> {
    let mut out = Vec::new();
    walk(dir, dir, &mut out);
    out.retain(|p| p != COMPLETE_MARKER && !p.ends_with(".gitkeep"));
    out.sort();
    out
}

fn walk(root: &Path, dir: &Path, out: &mut Vec<String>) {
    let Ok(rd) = std::fs::read_dir(dir) else { return };
    for e in rd.flatten() {
        let p = e.path();
        if p.is_dir() {
            walk(root, &p, out);
        } else if let Ok(rel) = p.strip_prefix(root) {
            out.push(rel.to_string_lossy().replace('/', "\\"));
        }
    }
}

/// Keep the newest few releases; `in_use` is never removed.
pub fn prune(layout: &Layout, in_use: &str) {
    let Ok(rd) = std::fs::read_dir(layout.deadworks_cache()) else { return };
    let mut dirs: Vec<(std::time::SystemTime, PathBuf)> = rd
        .flatten()
        .filter(|e| e.path().is_dir() && e.file_name() != in_use)
        .filter_map(|e| Some((e.metadata().ok()?.modified().ok()?, e.path())))
        .collect();
    dirs.sort_by_key(|d| std::cmp::Reverse(d.0));
    for (_, d) in dirs.into_iter().skip(KEEP - 1) {
        let _ = fsutil::remove_dir_all_force(&d);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tag_parsing() {
        assert_eq!(
            tag_from_location("https://github.com/Deadworks-net/deadworks/releases/tag/v0.4.16").as_deref(),
            Some("v0.4.16")
        );
        assert_eq!(tag_from_location("https://github.com/Deadworks-net/deadworks/releases"), None);
        assert_eq!(tag_from_location("https://github.com/x/releases/tag/../../evil"), None);
    }
}
