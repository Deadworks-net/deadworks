//! Drives and free space, for choosing where the ~36 GB base install goes.

use std::path::Path;

use super::fsutil::volume_of;
use super::store::FOLDER_NAME;
use super::types::DriveInfo;

/// Room the install needs before a location is worth suggesting.
const COMFORTABLE: u64 = 45 * 1024 * 1024 * 1024;

/// Places an install could go, with their free and total space. On Windows these are the fixed
/// drives; on Linux the home directory, the Steam library holding Deadlock, and `/`, one entry per
/// filesystem.
pub fn drives(client_game_dir: Option<&Path>) -> Vec<DriveInfo> {
    let client_volume = client_game_dir.and_then(volume_of);
    let mut out = Vec::new();
    #[cfg(windows)]
    {
        use windows_sys::Win32::Storage::FileSystem::{GetDriveTypeW, GetLogicalDrives};
        const DRIVE_FIXED: u32 = 3;
        let mask = unsafe { GetLogicalDrives() };
        for i in 0..26u32 {
            if mask & (1 << i) == 0 {
                continue;
            }
            let letter = (b'A' + i as u8) as char;
            let root = format!("{letter}:\\");
            let wide: Vec<u16> = root.encode_utf16().chain(std::iter::once(0)).collect();
            if unsafe { GetDriveTypeW(wide.as_ptr()) } != DRIVE_FIXED {
                continue;
            }
            let Some((free, total)) = space(Path::new(&root)) else { continue };
            out.push(DriveInfo {
                same_as_client: client_volume.as_deref() == Some(&root[..2]),
                root,
                free_bytes: free,
                total_bytes: total,
            });
        }
    }
    #[cfg(unix)]
    {
        // game -> Deadlock -> common -> steamapps -> library
        let library = client_game_dir.and_then(|g| g.ancestors().nth(4)).map(Path::to_path_buf);
        let home = std::env::var_os("HOME").map(std::path::PathBuf::from);
        let mut seen = Vec::new();
        for dir in [home, library, Some("/".into())].into_iter().flatten() {
            let Some(volume) = volume_of(&dir) else { continue };
            if seen.contains(&volume) {
                continue;
            }
            let Some((free, total)) = space(&dir) else { continue };
            out.push(DriveInfo {
                same_as_client: client_volume.as_deref() == Some(volume.as_str()),
                root: dir.to_string_lossy().into_owned(),
                free_bytes: free,
                total_bytes: total,
            });
            seen.push(volume);
        }
    }
    out
}

/// `(free, total)` bytes on the volume holding `path`.
pub fn space(path: &Path) -> Option<(u64, u64)> {
    // The folder may not exist yet; ask about its nearest existing ancestor.
    let existing = path.ancestors().find(|p| p.exists())?;
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
        let wide: Vec<u16> = existing.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
        let (mut free, mut total, mut _all) = (0u64, 0u64, 0u64);
        let ok = unsafe { GetDiskFreeSpaceExW(wide.as_ptr(), &mut free, &mut total, &mut _all) };
        (ok != 0).then_some((free, total))
    }
    #[cfg(unix)]
    {
        use std::os::unix::ffi::OsStrExt;
        let c = std::ffi::CString::new(existing.as_os_str().as_bytes()).ok()?;
        let mut stat: libc::statvfs = unsafe { std::mem::zeroed() };
        if unsafe { libc::statvfs(c.as_ptr(), &mut stat) } != 0 {
            return None;
        }
        let block = stat.f_frsize as u64;
        Some((stat.f_bavail as u64 * block, stat.f_blocks as u64 * block))
    }
}

pub const PATH_SEPARATOR: &str = if cfg!(windows) { "\\" } else { "/" };

/// Default install folder. Windows: the fixed drive with the most free space. Linux: the home
/// directory when it has room (the first entry), otherwise whichever place has the most.
pub fn suggested_root(drives: &[DriveInfo]) -> String {
    let join = |root: &str| format!("{}{PATH_SEPARATOR}{FOLDER_NAME}", root.trim_end_matches(['\\', '/']));
    // "/" itself is never somewhere a user can create a folder.
    let roomiest = drives.iter().filter(|d| d.root != "/").max_by_key(|d| d.free_bytes);
    let pick = if cfg!(windows) {
        roomiest
    } else {
        drives.first().filter(|home| home.free_bytes >= COMFORTABLE).or(roomiest)
    };
    match pick {
        Some(d) => join(&d.root),
        None if cfg!(windows) => join("C:"),
        None => join(&std::env::var("HOME").unwrap_or_default()),
    }
}
