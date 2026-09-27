//! Drives and free space, for choosing where the ~36 GB base install goes.

use std::path::Path;

use super::fsutil::volume_of;
use super::types::DriveInfo;

/// Fixed local drives with their free and total space.
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
    let _ = client_volume;
    out
}

/// `(free, total)` bytes on the volume holding `path`.
pub fn space(path: &Path) -> Option<(u64, u64)> {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
        // The folder may not exist yet; ask about its nearest existing ancestor.
        let existing = path.ancestors().find(|p| p.exists())?;
        let wide: Vec<u16> = existing.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
        let (mut free, mut total, mut _all) = (0u64, 0u64, 0u64);
        let ok = unsafe { GetDiskFreeSpaceExW(wide.as_ptr(), &mut free, &mut total, &mut _all) };
        (ok != 0).then_some((free, total))
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        None
    }
}

/// Default install folder: the fixed drive with the most free space.
pub fn suggested_root(drives: &[DriveInfo]) -> String {
    drives
        .iter()
        .max_by_key(|d| d.free_bytes)
        .map(|d| format!("{}{}", d.root, super::store::FOLDER_NAME))
        .unwrap_or_else(|| format!("C:\\{}", super::store::FOLDER_NAME))
}
