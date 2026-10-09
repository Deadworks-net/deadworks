//! Windows Firewall rules for server executables, and port checks that don't
//! trip the firewall.
//!
//! Each server runs its own `deadworks.exe` path, and Windows asks about every
//! new program that listens on the network. Dismissing that prompt silently
//! creates *block* rules, which win over any allow rule, so port-forwarded and
//! LAN players can't connect (SDR still works: it only connects outwards).
//! Before a server's first start the launcher therefore adds an allow rule for
//! its executable (a single UAC prompt per server), after removing any block
//! rules Windows already made for that path (a second prompt, only then). With a
//! rule in place Windows never prompts. That is also why SDR servers get one.

pub fn rule_name(server_id: &str) -> String {
    format!("Deadworks server {server_id}")
}

#[cfg(windows)]
mod win {
    use std::os::windows::process::CommandExt;
    use std::path::{Path, PathBuf};
    use std::process::Command;

    const CREATE_NO_WINDOW: u32 = 0x0800_0000;

    /// `netsh.exe` by its full System32 path. A bare name would be looked for in the current
    /// directory and the user's App Paths first, and this one gets run elevated.
    fn netsh() -> PathBuf {
        use std::os::windows::ffi::OsStringExt;
        use windows_sys::Win32::System::SystemInformation::GetSystemDirectoryW;

        let mut buf = [0u16; 260];
        let len = unsafe { GetSystemDirectoryW(buf.as_mut_ptr(), buf.len() as u32) } as usize;
        let dir = if len > 0 && len < buf.len() {
            PathBuf::from(std::ffi::OsString::from_wide(&buf[..len]))
        } else {
            PathBuf::from(r"C:\Windows\System32")
        };
        dir.join("netsh.exe")
    }

    /// `netsh` exits 0 when a rule with this name exists. Needs no elevation.
    pub fn rule_exists(name: &str) -> bool {
        Command::new(netsh())
            .args(["advfirewall", "firewall", "show", "rule"])
            .arg(format!("name={name}"))
            .creation_flags(CREATE_NO_WINDOW)
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false)
    }

    /// An inbound rule other than ours already names `exe`: the block rules Windows makes when
    /// its own "allow access?" prompt for that program is dismissed. A block beats any allow.
    fn has_other_rules(name: &str, exe: &str) -> bool {
        Command::new(netsh())
            .args(["advfirewall", "firewall", "show", "rule", "name=all", "dir=in", "verbose"])
            .creation_flags(CREATE_NO_WINDOW)
            .output()
            .map(|o| other_rules_in(&String::from_utf8_lossy(&o.stdout), name, exe))
            .unwrap_or(false)
    }

    /// `listing` is `show rule ... verbose`: one block of `Label: value` lines per rule, blank
    /// lines between. Labels are translated, so only the values are looked at.
    pub(super) fn other_rules_in(listing: &str, name: &str, exe: &str) -> bool {
        let exe = exe.to_lowercase();
        listing.replace("\r\n", "\n").split("\n\n").any(|rule| {
            let names_exe = rule.lines().any(|l| l.trim_end().to_lowercase().ends_with(&exe));
            let ours = rule.lines().any(|l| l.trim_end().ends_with(name));
            names_exe && !ours
        })
    }

    /// Run `netsh <args>` elevated (one UAC prompt) and return its exit code. Err when the user
    /// declines or it can't be started.
    ///
    /// netsh is started directly, never through `cmd`: cmd expands `%VAR%` before it looks at
    /// quotes, and `%CMDCMDLINE%` in a folder name would let that folder run commands as admin.
    fn elevated(args: &str) -> Result<u32, String> {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0};
        use windows_sys::Win32::System::Threading::{GetExitCodeProcess, WaitForSingleObject, INFINITE};
        use windows_sys::Win32::UI::Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW};

        let wide = |s: &std::ffi::OsStr| -> Vec<u16> { s.encode_wide().chain(std::iter::once(0)).collect() };
        let (verb, file, params) = (wide("runas".as_ref()), wide(netsh().as_os_str()), wide(args.as_ref()));
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
                    "Firewall permission declined.".into()
                } else {
                    format!("Couldn't update Windows Firewall: {e}")
                });
            }
            if info.hProcess.is_null() {
                return Ok(0);
            }
            let mut code = 1u32;
            if WaitForSingleObject(info.hProcess, INFINITE) == WAIT_OBJECT_0 {
                GetExitCodeProcess(info.hProcess, &mut code);
            }
            CloseHandle(info.hProcess);
            Ok(code)
        }
    }

    /// Give `exe` one inbound allow rule named `name`, first clearing other rules for it when
    /// there are any. Shows a UAC prompt (two when rules have to be cleared); returns Err when
    /// the user declines or it fails.
    pub fn allow_program(name: &str, exe: &Path) -> Result<(), String> {
        let exe = exe.to_string_lossy();
        // Quotes are the only thing netsh's own argument parsing gives meaning to.
        let unusable = |s: &str| s.contains('"') || s.chars().any(char::is_control);
        if unusable(&exe) || unusable(name) {
            return Err("Unexpected character in the server path".into());
        }
        if has_other_rules(name, &exe) {
            // Its exit code says whether anything matched, which doesn't matter here.
            elevated(&format!("advfirewall firewall delete rule name=all dir=in program=\"{exe}\""))?;
        }
        let code = elevated(&format!(
            "advfirewall firewall add rule name=\"{name}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any"
        ))?;
        if code == 0 {
            Ok(())
        } else {
            Err(format!("Couldn't update Windows Firewall (netsh exit code {code})."))
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
    std::net::UdpSocket::bind(("0.0.0.0", port)).is_err() || std::net::TcpListener::bind(("0.0.0.0", port)).is_err()
}

#[cfg(test)]
mod tests {
    use super::port_in_use;

    #[cfg(windows)]
    #[test]
    fn rules_windows_made_for_the_same_program_are_noticed() {
        let exe = r"H:\Servers\servers\abc-1f2e3d\game\bin\win64\deadworks.exe";
        let ours = "Deadworks server abc-1f2e3d";
        let rule = |name: &str, program: &str, action: &str| {
            format!("Rule Name:      {name}\r\n-------\r\nEnabled:        Yes\r\nProgram:        {program}\r\nAction:         {action}\r\n")
        };
        let other = rule("Something else", r"C:\Games\other.exe", "Allow");
        let mine = rule(ours, exe, "Allow");
        let blocked = rule("deadworks", &exe.to_uppercase(), "Block");

        assert!(!super::win::other_rules_in(&format!("\r\n{other}\r\n{mine}\r\nOk.\r\n"), ours, exe));
        assert!(super::win::other_rules_in(&format!("\r\n{other}\r\n{mine}\r\n{blocked}\r\nOk.\r\n"), ours, exe));
        assert!(!super::win::other_rules_in("", ours, exe));
    }

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
