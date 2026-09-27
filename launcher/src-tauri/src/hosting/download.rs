//! Downloads for the hosting core: the Deadworks release, the .NET runtime,
//! SteamCMD. Blocking, because they run on the task thread.

use std::io::{Read, Write};
use std::path::Path;
use std::sync::atomic::Ordering;
use std::time::Duration;

use sha2::{Digest, Sha512};

use super::task::{Progress, CANCELLED};

pub fn client() -> reqwest::blocking::Client {
    reqwest::blocking::Client::builder()
        .user_agent(concat!("deadworks-launcher/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(Duration::from_secs(15))
        .timeout(None)
        .build()
        .expect("http client")
}

/// Stream `url` to `dest` (via a temp file). Adds the byte count to the
/// progress totals. Returns the SHA-512 of the body for callers that verify it.
pub fn to_file(url: &str, dest: &Path, progress: &Progress) -> Result<[u8; 64], String> {
    let mut resp = client()
        .get(url)
        .send()
        .and_then(|r| r.error_for_status())
        .map_err(|e| format!("Download failed ({url}): {e}"))?;
    if let Some(len) = resp.content_length() {
        progress.bytes_total.fetch_add(len, Ordering::Relaxed);
    }
    if let Some(parent) = dest.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let tmp = dest.with_extension("part");
    let mut out = std::fs::File::create(&tmp).map_err(|e| format!("Couldn't write {}: {e}", tmp.display()))?;
    let mut hasher = Sha512::new();
    let mut buf = vec![0u8; 256 * 1024];
    let result = loop {
        if progress.cancelled() {
            break Err(CANCELLED.to_string());
        }
        match resp.read(&mut buf) {
            Ok(0) => break Ok(()),
            Ok(n) => {
                hasher.update(&buf[..n]);
                if let Err(e) = out.write_all(&buf[..n]) {
                    break Err(format!("Couldn't write {}: {e}", tmp.display()));
                }
                progress.bytes_done.fetch_add(n as u64, Ordering::Relaxed);
            }
            Err(e) => break Err(format!("Download interrupted ({url}): {e}")),
        }
    };
    drop(out);
    if let Err(e) = result {
        let _ = std::fs::remove_file(&tmp);
        return Err(e);
    }
    let _ = std::fs::remove_file(dest);
    std::fs::rename(&tmp, dest).map_err(|e| format!("Couldn't write {}: {e}", dest.display()))?;
    Ok(hasher.finalize().into())
}

pub fn get_text(url: &str) -> Result<String, String> {
    client()
        .get(url)
        .timeout(Duration::from_secs(30))
        .send()
        .and_then(|r| r.error_for_status())
        .and_then(|r| r.text())
        .map_err(|e| format!("Couldn't reach {url}: {e}"))
}

pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
