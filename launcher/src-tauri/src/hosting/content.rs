//! Per-server content: addon VPKs Deadworks mounts and extra map VPKs.

use std::path::Path;

use super::fsutil;
use super::store::Layout;
use super::types::{ContentFile, ContentKind};

const VPK_MAGIC: u32 = 0x55AA_1234;

pub fn kind_dir(kind: ContentKind) -> &'static str {
    match kind {
        ContentKind::Addon => "addons",
        ContentKind::Map => "maps",
    }
}

pub fn list(layout: &Layout, id: &str) -> Vec<ContentFile> {
    let mut out = Vec::new();
    for kind in [ContentKind::Addon, ContentKind::Map] {
        let Ok(rd) = std::fs::read_dir(layout.server_content(id).join(kind_dir(kind))) else { continue };
        for e in rd.flatten() {
            let p = e.path();
            if p.extension().is_some_and(|x| x.eq_ignore_ascii_case("vpk")) {
                out.push(ContentFile {
                    file_name: e.file_name().to_string_lossy().into_owned(),
                    kind,
                    size_bytes: e.metadata().map(|m| m.len()).unwrap_or(0),
                });
            }
        }
    }
    out.sort_by_key(|f| f.file_name.to_ascii_lowercase());
    out
}

/// Copy VPKs into the server's content folder. Returns the imported file names.
pub fn import(layout: &Layout, id: &str, paths: &[String], kind: ContentKind) -> Result<Vec<String>, String> {
    let dir = layout.server_content(id).join(kind_dir(kind));
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let mut names = Vec::new();
    for p in paths {
        let src = Path::new(p);
        let name = src.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        let stem = name.strip_suffix(".vpk").or_else(|| name.strip_suffix(".VPK")).unwrap_or("");
        if stem.is_empty() || !stem.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '-' | '.')) {
            return Err(format!(
                "{name} can't be used. Use a .vpk named with letters, digits, '_', '-' or '.'."
            ));
        }
        if stem.ends_with("_dir") && kind == ContentKind::Map {
            return Err(format!("{name} is a multi-part VPK. Maps must be a single file."));
        }
        let mut magic = [0u8; 4];
        std::fs::File::open(src)
            .and_then(|mut f| std::io::Read::read_exact(&mut f, &mut magic))
            .map_err(|e| format!("Couldn't read {name}: {e}"))?;
        if u32::from_le_bytes(magic) != VPK_MAGIC {
            return Err(format!("{name} isn't a valid VPK file."));
        }
        let dst = dir.join(&name);
        // Replacing a VPK a running server has linked in: drop the link first.
        fsutil::remove_file_force(&dst).map_err(|e| format!("Couldn't replace {name}: {e}"))?;
        std::fs::copy(src, &dst).map_err(|e| format!("Couldn't import {name}: {e}"))?;
        names.push(name);
    }
    Ok(names)
}

/// Delete a content file; returns which kind it was.
pub fn remove(layout: &Layout, id: &str, file_name: &str) -> Result<ContentKind, String> {
    if file_name.contains(['/', '\\']) || file_name.contains("..") {
        return Err("Unknown content file".into());
    }
    for kind in [ContentKind::Addon, ContentKind::Map] {
        let p = layout.server_content(id).join(kind_dir(kind)).join(file_name);
        if p.is_file() {
            fsutil::remove_file_force(&p).map_err(|e| format!("Couldn't remove {file_name}: {e}"))?;
            return Ok(kind);
        }
    }
    Err(format!("{file_name} isn't in this server's content"))
}
