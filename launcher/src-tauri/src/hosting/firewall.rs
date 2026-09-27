//! Windows Firewall rules for server executables, and port checks that don't
//! trip the firewall.
//!
//! Each server runs its own `deadworks.exe` path, and Windows asks about every
//! new program that listens on the network. Dismissing that prompt silently
//! creates *block* rules, which win over any allow rule, so port-forwarded and
//! LAN players can't connect (SDR still works: it only connects outwards).
//! Before a server's first start the launcher therefore adds an allow rule for
//! its executable, removing any block rules Windows made for that path, in one
//! elevated step (a single UAC prompt per server). With a rule in place Windows
//! never prompts.

pub fn rule_name(server_id: &str) -> String {
    format!("Deadworks server {server_id}")
}

#[cfg(windows)]
mod win {
    use std::os::windows::process::CommandExt;
    use std::path::Path;
    use std::process::Command;

    const CREATE_NO_WINDOW: u32 = 0x0800_0000;

    /// `netsh` exits 0 when a rule with this name exists. Needs no elevation.
    pub fn rule_exists(name: &str) -> bool {
        Command::new("netsh")
            .args(["advfirewall", "firewall", "show", "rule"])
            .arg(format!("name={name}"))
            .creation_flags(CREATE_NO_WINDOW)
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false)
    }

    /// Replace every rule for `exe` with one inbound allow rule named `name`.
    /// Shows a UAC prompt; returns Err when the user declines or it fails.
    pub fn allow_program(name: &str, exe: &Path) -> Result<(), String> {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0};
        use windows_sys::Win32::System::Threading::{GetExitCodeProcess, WaitForSingleObject, INFINITE};
        use windows_sys::Win32::UI::Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW};

        let exe = exe.to_string_lossy();
        if exe.contains('"') || name.contains('"') {
            return Err("Unexpected character in the server path".into());
        }
        // cmd /c "A & B": delete may find nothing (non-zero), add must succeed.
        let params = format!(
            "/c netsh advfirewall firewall delete rule name=all program=\"{exe}\" dir=in >nul & \
             netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any"
        );
        let wide = |s: &str| -> Vec<u16> { std::ffi::OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect() };
        let (verb, file, params) = (wide("runas"), wide("cmd.exe"), wide(&params));
        unsafe {
            let mut info: SHELLEXECUTEINFOW = std::mem::zeroed();
            info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
            info.fMask = SEE_MASK_NOCLOSEPROCESS;
            info.lpVerb = verb.as_ptr();
            info.lpFile = file.as_ptr();
            info.lpParameters = params.as_ptr();
            info.nShow = 0; // SW_HIDE
            if ShellExecuteExW(&mut info) == 0 {
                let e = std::io::Error::last_os_error();
                // ERROR_CANCELLED: the user said no to UAC.
                return Err(if e.raw_os_error() == Some(1223) {
                    "Windows Firewall wasn't updated (permission declined).".into()
                } else {
                    format!("Couldn't update Windows Firewall: {e}")
                });
            }
            if info.hProcess.is_null() {
                return Ok(());
            }
            let mut code = 1u32;
            if WaitForSingleObject(info.hProcess, INFINITE) == WAIT_OBJECT_0 {
                GetExitCodeProcess(info.hProcess, &mut code);
            }
            CloseHandle(info.hProcess);
            if code == 0 {
                Ok(())
            } else {
                Err(format!("Couldn't update Windows Firewall (netsh exit code {code})."))
            }
        }
    }

    /// Some process on this PC holds `port` for UDP or TCP. Read from the OS
    /// tables rather than by binding, which would itself trip the firewall.
    pub fn port_in_use(port: u16) -> bool {
        use windows_sys::Win32::NetworkManagement::IpHelper::{
            GetExtendedTcpTable, GetExtendedUdpTable, TCP_TABLE_OWNER_PID_LISTENER, UDP_TABLE_OWNER_PID,
        };
        use windows_sys::Win32::Networking::WinSock::{AF_INET, AF_INET6};

        // Both tables start with a u32 row count; the local port sits at a fixed
        // offset in each row, in network byte order in the low 16 bits.
        fn scan(fetch: impl Fn(*mut core::ffi::c_void, *mut u32) -> u32, row_size: usize, port_offset: usize, port: u16) -> bool {
            let mut size = 0u32;
            fetch(std::ptr::null_mut(), &mut size);
            if size == 0 {
                return false;
            }
            let mut buf = vec![0u8; size as usize + 1024];
            let mut size = buf.len() as u32;
            if fetch(buf.as_mut_ptr().cast(), &mut size) != 0 {
                return false;
            }
            let rows = u32::from_ne_bytes(buf[0..4].try_into().unwrap()) as usize;
            (0..rows).any(|i| {
                let off = 4 + i * row_size + port_offset;
                buf.get(off..off + 4)
                    .map(|b| u16::from_be(u32::from_ne_bytes(b.try_into().unwrap()) as u16) == port)
                    .unwrap_or(false)
            })
        }
        unsafe {
            let udp4 = scan(|p, s| GetExtendedUdpTable(p, s, 0, AF_INET as u32, UDP_TABLE_OWNER_PID, 0), 12, 4, port);
            let udp6 = scan(|p, s| GetExtendedUdpTable(p, s, 0, AF_INET6 as u32, UDP_TABLE_OWNER_PID, 0), 28, 20, port);
            let tcp4 = scan(|p, s| GetExtendedTcpTable(p, s, 0, AF_INET as u32, TCP_TABLE_OWNER_PID_LISTENER, 0), 24, 8, port);
            let tcp6 = scan(|p, s| GetExtendedTcpTable(p, s, 0, AF_INET6 as u32, TCP_TABLE_OWNER_PID_LISTENER, 0), 56, 20, port);
            udp4 || udp6 || tcp4 || tcp6
        }
    }
}

#[cfg(windows)]
pub use win::{allow_program, port_in_use, rule_exists};

#[cfg(not(windows))]
pub fn rule_exists(_name: &str) -> bool {
    true
}

#[cfg(not(windows))]
pub fn allow_program(_name: &str, _exe: &std::path::Path) -> Result<(), String> {
    Ok(())
}

#[cfg(not(windows))]
pub fn port_in_use(port: u16) -> bool {
    std::net::UdpSocket::bind(("0.0.0.0", port)).is_err()
}

#[cfg(test)]
mod tests {
    use super::port_in_use;

    #[test]
    fn a_bound_port_is_seen_without_binding_it_ourselves() {
        let sock = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        let port = sock.local_addr().unwrap().port();
        assert!(port_in_use(port));
        let tcp = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        assert!(port_in_use(tcp.local_addr().unwrap().port()));
        drop(sock);
    }
}
