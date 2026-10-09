//! File operations the install and the per-server trees are built from.
//!
//! The shared base install is marked read-only so that a server writing into a
//! shipped file (which would write through every hardlink into every server)
//! fails loudly instead. That makes the ordinary delete/replace calls fail on
//! it too, so everything that replaces or removes a base file or one of its
//! links goes through [`remove_file_force`].

use std::fs::File;
use std::io::{Read, Write};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

use sha1::{Digest, Sha1};

/// Join a path that uses either separator onto `base`. Steam's depot manifests (and the paths this
/// module derives from them) are written with `\`, which is just a filename character on Linux.
pub fn join_rel(base: &Path, rel: &str) -> std::path::PathBuf {
    let mut out = base.to_path_buf();
    out.extend(rel.split(['\\', '/']).filter(|seg| !seg.is_empty()));
    out
}

/// Delete a file even when it is read-only, without touching the read-only bit
/// of other hardlinks to the same data. Missing files are not an error.
pub fn remove_file_force(path: &Path) -> std::io::Result<()> {
    match std::fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => {
            #[cfg(windows)]
            {
                if e.raw_os_error() == Some(5) {
                    return win::delete_ignoring_readonly(path);
                }
            }
            Err(e)
        }
    }
}

pub fn set_readonly(path: &Path, readonly: bool) -> std::io::Result<()> {
    let mut perms = std::fs::metadata(path)?.permissions();
    if perms.readonly() != readonly {
        perms.set_readonly(readonly);
        std::fs::set_permissions(path, perms)?;
    }
    Ok(())
}

/// Staging name next to `dst`. Appended rather than swapped for the extension:
/// `a.png` and `a.xcf` copied in parallel must not share one temp file.
fn tmp_path(dst: &Path) -> std::path::PathBuf {
    let mut name = dst.file_name().unwrap_or_default().to_os_string();
    name.push(".dwtmp");
    dst.with_file_name(name)
}

/// Replace `dst` with a hardlink to `src`.
pub fn relink(src: &Path, dst: &Path) -> std::io::Result<()> {
    if let Some(parent) = dst.parent() {
        std::fs::create_dir_all(parent)?;
    }
    remove_file_force(dst)?;
    std::fs::hard_link(src, dst)
}

/// Write `data` to `dst` as a real (unlinked) file, replacing whatever was
/// there, including a hardlink into the base.
pub fn write_real(dst: &Path, data: &[u8]) -> std::io::Result<()> {
    if let Some(parent) = dst.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let tmp = tmp_path(dst);
    std::fs::write(&tmp, data)?;
    remove_file_force(dst)?;
    std::fs::rename(&tmp, dst)
}

/// Write only when the content differs, so unchanged files keep their mtime
/// and running servers' file watchers stay quiet.
/// Keep other accounts on this machine from reading `path`. Where files have a mode, that is;
/// on Windows the folder's permissions decide.
pub fn owner_only(path: &Path) {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600));
    }
    #[cfg(not(unix))]
    let _ = path;
}

pub fn write_if_changed(dst: &Path, data: &[u8]) -> std::io::Result<bool> {
    if std::fs::read(dst).map(|cur| cur == data).unwrap_or(false) {
        return Ok(false);
    }
    write_real(dst, data)?;
    Ok(true)
}

/// Stream `src` into `dst` (via a temp file, then rename) while hashing it.
/// `progress` is advanced by every chunk. Returns the SHA1 of what was copied.
pub fn copy_hashing(
    src: &Path,
    dst: &Path,
    progress: &AtomicU64,
    cancel: &AtomicBool,
) -> std::io::Result<[u8; 20]> {
    if let Some(parent) = dst.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let tmp = tmp_path(dst);
    let result = (|| {
        let mut input = File::open(src)?;
        let mut output = File::create(&tmp)?;
        let mut hasher = Sha1::new();
        let mut buf = vec![0u8; 1 << 20];
        loop {
            if cancel.load(Ordering::Relaxed) {
                return Err(std::io::Error::new(std::io::ErrorKind::Interrupted, "cancelled"));
            }
            let n = input.read(&mut buf)?;
            if n == 0 {
                break;
            }
            hasher.update(&buf[..n]);
            output.write_all(&buf[..n])?;
            progress.fetch_add(n as u64, Ordering::Relaxed);
        }
        output.flush()?;
        drop(output);
        remove_file_force(dst)?;
        std::fs::rename(&tmp, dst)?;
        Ok(hasher.finalize().into())
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
    result
}

pub fn sha1_file(path: &Path, progress: &AtomicU64, cancel: &AtomicBool) -> std::io::Result<[u8; 20]> {
    let mut input = File::open(path)?;
    let mut hasher = Sha1::new();
    let mut buf = vec![0u8; 1 << 20];
    loop {
        if cancel.load(Ordering::Relaxed) {
            return Err(std::io::Error::new(std::io::ErrorKind::Interrupted, "cancelled"));
        }
        let n = input.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        progress.fetch_add(n as u64, Ordering::Relaxed);
    }
    Ok(hasher.finalize().into())
}

pub fn sha1_bytes(data: &[u8]) -> [u8; 20] {
    Sha1::digest(data).into()
}

/// Two paths name the same file on disk (hardlinks of one another).
pub fn same_file(a: &Path, b: &Path) -> bool {
    #[cfg(windows)]
    {
        match (win::file_id(a), win::file_id(b)) {
            (Some(x), Some(y)) => x == y,
            _ => false,
        }
    }
    #[cfg(not(windows))]
    {
        use std::os::unix::fs::MetadataExt;
        match (std::fs::metadata(a), std::fs::metadata(b)) {
            (Ok(x), Ok(y)) => x.dev() == y.dev() && x.ino() == y.ino(),
            _ => false,
        }
    }
}

/// Volume identity of a path, for "can these two be hardlinked" checks.
#[cfg(windows)]
pub fn volume_of(path: &Path) -> Option<String> {
    let s = path.to_string_lossy();
    let b = s.as_bytes();
    if b.len() >= 2 && b[1] == b':' {
        return Some(s[..2].to_ascii_uppercase());
    }
    None
}

/// Volume identity of a path: the device of its nearest existing ancestor.
#[cfg(unix)]
pub fn volume_of(path: &Path) -> Option<String> {
    use std::os::unix::fs::MetadataExt;
    let existing = path.ancestors().find(|p| p.exists())?;
    Some(std::fs::metadata(existing).ok()?.dev().to_string())
}

/// Recursively delete a directory tree, including read-only files.
pub fn remove_dir_all_force(path: &Path) -> std::io::Result<()> {
    let Ok(rd) = std::fs::read_dir(path) else { return Ok(()) };
    for entry in rd.flatten() {
        let p = entry.path();
        let ft = entry.file_type()?;
        if ft.is_symlink() {
            // Junctions / symlinks: remove the link, never what it points at.
            std::fs::remove_dir(&p).or_else(|_| std::fs::remove_file(&p))?;
        } else if ft.is_dir() {
            remove_dir_all_force(&p)?;
        } else {
            remove_file_force(&p)?;
        }
    }
    std::fs::remove_dir(path)
}

/// Extract a zip archive into `dest`, refusing entries that escape it.
pub fn extract_zip(archive: &Path, dest: &Path) -> Result<Vec<String>, String> {
    let file = File::open(archive).map_err(|e| format!("Couldn't open {}: {e}", archive.display()))?;
    let mut zip = zip::ZipArchive::new(file).map_err(|e| format!("Not a valid zip archive: {e}"))?;
    let mut names = Vec::new();
    for i in 0..zip.len() {
        let mut entry = zip.by_index(i).map_err(|e| format!("Corrupt zip archive: {e}"))?;
        let Some(rel) = entry.enclosed_name() else {
            return Err(format!("Refusing unsafe path in archive: {}", entry.name()));
        };
        let out = dest.join(&rel);
        if entry.is_dir() {
            std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
            continue;
        }
        if let Some(parent) = out.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let mut f = File::create(&out).map_err(|e| format!("Couldn't write {}: {e}", out.display()))?;
        std::io::copy(&mut entry, &mut f).map_err(|e| format!("Couldn't extract {}: {e}", out.display()))?;
        names.push(rel.to_string_lossy().replace('\\', "/"));
    }
    Ok(names)
}

#[cfg(windows)]
mod win {
    use std::os::windows::ffi::OsStrExt;
    use std::path::Path;

    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, FileDispositionInfoEx, GetFileInformationByHandle, SetFileInformationByHandle,
        BY_HANDLE_FILE_INFORMATION, DELETE, FILE_DISPOSITION_FLAG_DELETE,
        FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE, FILE_DISPOSITION_FLAG_POSIX_SEMANTICS,
        FILE_DISPOSITION_INFO_EX, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_DELETE,
        FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
    };

    fn wide(path: &Path) -> Vec<u16> {
        path.as_os_str().encode_wide().chain(std::iter::once(0)).collect()
    }

    pub fn delete_ignoring_readonly(path: &Path) -> std::io::Result<()> {
        let name = wide(path);
        unsafe {
            let h = CreateFileW(
                name.as_ptr(),
                DELETE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                std::ptr::null(),
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT,
                std::ptr::null_mut(),
            );
            if h == INVALID_HANDLE_VALUE {
                return Err(std::io::Error::last_os_error());
            }
            let info = FILE_DISPOSITION_INFO_EX {
                Flags: FILE_DISPOSITION_FLAG_DELETE
                    | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                    | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
            };
            let ok = SetFileInformationByHandle(
                h,
                FileDispositionInfoEx,
                &info as *const _ as *const _,
                std::mem::size_of::<FILE_DISPOSITION_INFO_EX>() as u32,
            );
            let err = std::io::Error::last_os_error();
            CloseHandle(h);
            if ok == 0 {
                return Err(err);
            }
        }
        Ok(())
    }

    pub fn file_id(path: &Path) -> Option<(u32, u32, u32)> {
        let name = wide(path);
        unsafe {
            let h = CreateFileW(
                name.as_ptr(),
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                std::ptr::null(),
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                std::ptr::null_mut(),
            );
            if h == INVALID_HANDLE_VALUE {
                return None;
            }
            let mut info: BY_HANDLE_FILE_INFORMATION = std::mem::zeroed();
            let ok = GetFileInformationByHandle(h, &mut info);
            CloseHandle(h);
            (ok != 0).then_some((info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("dw-fsutil-{name}-{}", std::process::id()));
        let _ = remove_dir_all_force(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn readonly_link_can_be_replaced_without_unprotecting_the_original() {
        let dir = scratch("relink");
        let base = dir.join("base.bin");
        std::fs::write(&base, b"shipped").unwrap();
        set_readonly(&base, true).unwrap();
        let link = dir.join("tree").join("base.bin");
        relink(&base, &link).unwrap();
        assert!(same_file(&base, &link));
        // Replacing the link with a real file leaves the base untouched and read-only.
        write_real(&link, b"patched").unwrap();
        assert!(!same_file(&base, &link));
        assert_eq!(std::fs::read(&base).unwrap(), b"shipped");
        assert!(std::fs::metadata(&base).unwrap().permissions().readonly());
        remove_file_force(&base).unwrap();
        assert!(!base.exists());
        remove_dir_all_force(&dir).unwrap();
    }

    #[test]
    fn same_stem_files_get_distinct_temp_names() {
        assert_ne!(tmp_path(Path::new("x/a.png")), tmp_path(Path::new("x/a.xcf")));
    }

    #[test]
    fn copy_hashing_matches_sha1_of_content() {
        let dir = scratch("copy");
        let src = dir.join("a");
        std::fs::write(&src, b"hello").unwrap();
        let p = AtomicU64::new(0);
        let sha = copy_hashing(&src, &dir.join("b"), &p, &AtomicBool::new(false)).unwrap();
        assert_eq!(sha, sha1_bytes(b"hello"));
        assert_eq!(p.load(Ordering::Relaxed), 5);
        assert_eq!(std::fs::read(dir.join("b")).unwrap(), b"hello");
        remove_dir_all_force(&dir).unwrap();
    }
}
