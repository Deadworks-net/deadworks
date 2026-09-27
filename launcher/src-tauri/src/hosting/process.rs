//! Running `deadworks.exe`.
//!
//! The dedicated server's text console reads commands from its console input
//! and repaints a status bar at the top of the screen. Neither a pipe nor a
//! pseudo console gives a clean result: with stdin on a pipe (or NUL) the
//! engine spams `GetLine: !GetNumberOfConsoleInputEvents` and ignores input,
//! and under ConPTY every status-bar repaint re-emits the whole screen, so
//! each log line arrives dozens of times. What works (verified on build 6698):
//!
//! * `CREATE_NO_WINDOW` gives the child its own hidden console;
//! * `STARTF_USESTDHANDLES` with a *NULL* stdin leaves stdin on that console,
//!   while stdout/stderr go to pipes, so output arrives exactly once, clean;
//! * commands are typed into it: attach to the child's console, write key
//!   events to `CONIN$`, detach. The engine echoes each command to stdout.
//!
//! Every server is placed in one kill-on-close job object, so a launcher crash
//! never leaves orphaned servers holding ports.

use std::path::PathBuf;

pub struct SpawnSpec {
    pub exe: PathBuf,
    pub cwd: PathBuf,
    pub args: Vec<String>,
    pub env: Vec<(String, String)>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Stream {
    Stdout,
    Stderr,
}

#[cfg(windows)]
pub use win::*;

#[cfg(not(windows))]
pub use other::*;

/// Quote one argument for a Windows command line (CommandLineToArgvW rules).
pub fn quote_arg(arg: &str) -> String {
    if !arg.is_empty() && !arg.contains([' ', '\t', '"']) {
        return arg.to_string();
    }
    let mut out = String::from("\"");
    let mut backslashes = 0;
    for c in arg.chars() {
        match c {
            '\\' => backslashes += 1,
            '"' => {
                out.push_str(&"\\".repeat(backslashes * 2 + 1));
                out.push('"');
                backslashes = 0;
            }
            _ => {
                out.push_str(&"\\".repeat(backslashes));
                out.push(c);
                backslashes = 0;
            }
        }
    }
    out.push_str(&"\\".repeat(backslashes * 2));
    out.push('"');
    out
}

#[cfg(windows)]
mod win {
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::io::{FromRawHandle, OwnedHandle};
    use std::os::windows::io::AsRawHandle;
    use std::sync::{Arc, Mutex, OnceLock};

    use windows_sys::Win32::Foundation::{
        CloseHandle, SetHandleInformation, GENERIC_READ, GENERIC_WRITE, HANDLE, HANDLE_FLAG_INHERIT,
        INVALID_HANDLE_VALUE, WAIT_OBJECT_0,
    };
    use windows_sys::Win32::Security::SECURITY_ATTRIBUTES;
    use windows_sys::Win32::Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING};
    use windows_sys::Win32::System::Console::{
        AttachConsole, FreeConsole, WriteConsoleInputW, ATTACH_PARENT_PROCESS, INPUT_RECORD, INPUT_RECORD_0, KEY_EVENT,
        KEY_EVENT_RECORD, KEY_EVENT_RECORD_0,
    };
    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    };
    use windows_sys::Win32::System::Pipes::CreatePipe;
    use windows_sys::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX};
    use windows_sys::Win32::System::Threading::{
        CreateProcessW, GetExitCodeProcess, GetProcessTimes, ResumeThread, TerminateProcess, WaitForSingleObject,
        CREATE_NO_WINDOW, CREATE_SUSPENDED, CREATE_UNICODE_ENVIRONMENT, INFINITE, PROCESS_INFORMATION,
        STARTF_USESTDHANDLES, STARTUPINFOW,
    };

    use super::{quote_arg, SpawnSpec, Stream};

    /// A running server process. Dropping it does not stop the process.
    pub struct Process {
        pub pid: u32,
        handle: Arc<OwnedHandle>,
        cpu: Mutex<Option<(u64, std::time::Instant)>>,
    }

    struct SendHandle(HANDLE);
    unsafe impl Send for SendHandle {}
    unsafe impl Sync for SendHandle {}

    fn job() -> Option<HANDLE> {
        static JOB: OnceLock<Option<SendHandle>> = OnceLock::new();
        JOB.get_or_init(|| unsafe {
            let job = CreateJobObjectW(std::ptr::null(), std::ptr::null());
            if job.is_null() {
                return None;
            }
            let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const _,
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            );
            // Never closed: the OS closes it when the launcher exits, which kills the servers.
            Some(SendHandle(job))
        })
        .as_ref()
        .map(|h| h.0)
    }

    fn wide(s: &std::ffi::OsStr) -> Vec<u16> {
        s.encode_wide().chain(std::iter::once(0)).collect()
    }

    fn pipe() -> std::io::Result<(HANDLE, HANDLE)> {
        let sa = SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: std::ptr::null_mut(),
            bInheritHandle: 1,
        };
        let (mut r, mut w): (HANDLE, HANDLE) = (std::ptr::null_mut(), std::ptr::null_mut());
        unsafe {
            if CreatePipe(&mut r, &mut w, &sa, 0) == 0 {
                return Err(std::io::Error::last_os_error());
            }
            // Only the write end goes to the child.
            SetHandleInformation(r, HANDLE_FLAG_INHERIT, 0);
        }
        Ok((r, w))
    }

    fn env_block(extra: &[(String, String)]) -> Vec<u16> {
        let mut vars: Vec<(String, String)> = std::env::vars()
            .filter(|(k, _)| !extra.iter().any(|(e, _)| e.eq_ignore_ascii_case(k)))
            .collect();
        vars.extend(extra.iter().cloned());
        vars.sort_by_key(|(k, _)| k.to_ascii_uppercase());
        let mut block = Vec::new();
        for (k, v) in vars {
            block.extend(format!("{k}={v}").encode_utf16());
            block.push(0);
        }
        block.push(0);
        block
    }

    /// Start the server. `on_line` gets every output line; `on_exit` the exit
    /// code once the process is gone and its output drained.
    pub fn spawn(
        spec: SpawnSpec,
        on_line: impl Fn(Stream, String) + Send + Sync + 'static,
        on_exit: impl FnOnce(i32) + Send + 'static,
    ) -> Result<Process, String> {
        let (out_r, out_w) = pipe().map_err(|e| format!("Couldn't start the server: {e}"))?;
        let (err_r, err_w) = pipe().map_err(|e| format!("Couldn't start the server: {e}"))?;
        let mut cmdline: Vec<u16> = std::iter::once(quote_arg(&spec.exe.to_string_lossy()))
            .chain(spec.args.iter().map(|a| quote_arg(a)))
            .collect::<Vec<_>>()
            .join(" ")
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let exe = wide(spec.exe.as_os_str());
        let cwd = wide(spec.cwd.as_os_str());
        let env = env_block(&spec.env);

        let pi = unsafe {
            let mut si: STARTUPINFOW = std::mem::zeroed();
            si.cb = std::mem::size_of::<STARTUPINFOW>() as u32;
            si.dwFlags = STARTF_USESTDHANDLES;
            // NULL, not NUL: the child's stdin then stays on its own (hidden) console.
            si.hStdInput = std::ptr::null_mut();
            si.hStdOutput = out_w;
            si.hStdError = err_w;
            let mut pi: PROCESS_INFORMATION = std::mem::zeroed();
            let ok = CreateProcessW(
                exe.as_ptr(),
                cmdline.as_mut_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                1,
                CREATE_NO_WINDOW | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
                env.as_ptr() as *const _,
                cwd.as_ptr(),
                &si,
                &mut pi,
            );
            let err = std::io::Error::last_os_error();
            CloseHandle(out_w);
            CloseHandle(err_w);
            if ok == 0 {
                CloseHandle(out_r);
                CloseHandle(err_r);
                return Err(format!("Couldn't start the server: {err}"));
            }
            if let Some(job) = job() {
                AssignProcessToJobObject(job, pi.hProcess);
            }
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
            pi
        };

        let handle = Arc::new(unsafe { OwnedHandle::from_raw_handle(pi.hProcess as _) });
        let on_line = Arc::new(on_line);
        let readers: Vec<_> = [(out_r, Stream::Stdout), (err_r, Stream::Stderr)]
            .into_iter()
            .map(|(h, stream)| {
                let on_line = on_line.clone();
                let file = unsafe { std::fs::File::from_raw_handle(h as _) };
                std::thread::spawn(move || read_lines(file, |l| on_line(stream, l)))
            })
            .collect();

        let waiter = handle.clone();
        std::thread::spawn(move || {
            let h = waiter.as_raw_handle() as HANDLE;
            let mut code = 0u32;
            unsafe {
                if WaitForSingleObject(h, INFINITE) == WAIT_OBJECT_0 {
                    GetExitCodeProcess(h, &mut code);
                }
            }
            for r in readers {
                let _ = r.join();
            }
            on_exit(code as i32);
        });

        Ok(Process { pid: pi.dwProcessId, handle, cpu: Mutex::new(None) })
    }

    fn read_lines(mut file: std::fs::File, mut emit: impl FnMut(String)) {
        use std::io::Read;
        let mut buf = Vec::new();
        let mut chunk = [0u8; 8192];
        loop {
            let n = match file.read(&mut chunk) {
                Ok(0) | Err(_) => break,
                Ok(n) => n,
            };
            buf.extend_from_slice(&chunk[..n]);
            while let Some(pos) = buf.iter().position(|&b| b == b'\n') {
                let mut line: Vec<u8> = buf.drain(..=pos).collect();
                line.pop();
                if line.last() == Some(&b'\r') {
                    line.pop();
                }
                emit(String::from_utf8_lossy(&line).into_owned());
            }
        }
        if !buf.is_empty() {
            emit(String::from_utf8_lossy(&buf).into_owned());
        }
    }

    static CONSOLE_LOCK: Mutex<()> = Mutex::new(());

    /// Type `line` + Enter into the console of process `pid`.
    pub fn send_line(pid: u32, line: &str) -> Result<(), String> {
        let _guard = CONSOLE_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let mut records = Vec::new();
        for ch in line.encode_utf16().chain(std::iter::once(b'\r' as u16)) {
            for down in [1, 0] {
                records.push(INPUT_RECORD {
                    EventType: KEY_EVENT as u16,
                    Event: INPUT_RECORD_0 {
                        KeyEvent: KEY_EVENT_RECORD {
                            bKeyDown: down,
                            wRepeatCount: 1,
                            wVirtualKeyCode: if ch == 13 { 0x0D } else { 0 },
                            wVirtualScanCode: 0,
                            uChar: KEY_EVENT_RECORD_0 { UnicodeChar: ch },
                            dwControlKeyState: 0,
                        },
                    },
                });
            }
        }
        unsafe {
            FreeConsole();
            let result = (|| {
                if AttachConsole(pid) == 0 {
                    return Err(format!("Couldn't reach the server console: {}", std::io::Error::last_os_error()));
                }
                let name: Vec<u16> = "CONIN$\0".encode_utf16().collect();
                let h = CreateFileW(
                    name.as_ptr(),
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    std::ptr::null(),
                    OPEN_EXISTING,
                    0,
                    std::ptr::null_mut(),
                );
                if h == INVALID_HANDLE_VALUE {
                    return Err("Couldn't open the server console".to_string());
                }
                let mut written = 0u32;
                let ok = WriteConsoleInputW(h, records.as_ptr(), records.len() as u32, &mut written);
                CloseHandle(h);
                if ok == 0 {
                    return Err(format!("Couldn't type into the server console: {}", std::io::Error::last_os_error()));
                }
                Ok(())
            })();
            FreeConsole();
            // Dev builds run inside a terminal; give our own logging its console back.
            if cfg!(debug_assertions) {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
            result
        }
    }

    impl Process {
        pub fn terminate(&self) {
            unsafe {
                TerminateProcess(self.handle.as_raw_handle() as HANDLE, 1);
            }
        }

        /// `(cpu % of the whole machine since the last call, private bytes)`.
        pub fn sample(&self) -> (f32, u64) {
            let h = self.handle.as_raw_handle() as HANDLE;
            let mut mem: PROCESS_MEMORY_COUNTERS_EX = unsafe { std::mem::zeroed() };
            mem.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32;
            let bytes = unsafe {
                if GetProcessMemoryInfo(h, &mut mem as *mut _ as *mut _, mem.cb) != 0 {
                    mem.PrivateUsage as u64
                } else {
                    0
                }
            };
            let busy = unsafe {
                let (mut c, mut e, mut k, mut u) = std::mem::zeroed();
                if GetProcessTimes(h, &mut c, &mut e, &mut k, &mut u) == 0 {
                    return (0.0, bytes);
                }
                let ft = |f: windows_sys::Win32::Foundation::FILETIME| (u64::from(f.dwHighDateTime) << 32) | u64::from(f.dwLowDateTime);
                ft(k) + ft(u)
            };
            let now = std::time::Instant::now();
            let mut last = self.cpu.lock().unwrap();
            let pct = match *last {
                Some((prev_busy, prev_at)) => {
                    let wall = now.duration_since(prev_at).as_nanos() as f64 / 100.0;
                    let cpus = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1) as f64;
                    if wall > 0.0 {
                        ((busy.saturating_sub(prev_busy)) as f64 / (wall * cpus) * 100.0) as f32
                    } else {
                        0.0
                    }
                }
                None => 0.0,
            };
            *last = Some((busy, now));
            (pct.clamp(0.0, 100.0), bytes)
        }
    }
}

#[cfg(not(windows))]
mod other {
    use super::{SpawnSpec, Stream};

    pub struct Process {
        pub pid: u32,
    }

    pub fn spawn(
        _spec: SpawnSpec,
        _on_line: impl Fn(Stream, String) + Send + Sync + 'static,
        _on_exit: impl FnOnce(i32) + Send + 'static,
    ) -> Result<Process, String> {
        Err("Hosting a server is only supported on Windows.".into())
    }

    pub fn send_line(_pid: u32, _line: &str) -> Result<(), String> {
        Err("Hosting a server is only supported on Windows.".into())
    }

    impl Process {
        pub fn terminate(&self) {}
        pub fn sample(&self) -> (f32, u64) {
            (0.0, 0)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::quote_arg;

    #[test]
    fn quoting() {
        assert_eq!(quote_arg("-dedicated"), "-dedicated");
        assert_eq!(quote_arg(r"C:\Deadworks Servers\x\deadworks.exe"), r#""C:\Deadworks Servers\x\deadworks.exe""#);
        assert_eq!(quote_arg(r#"a"b"#), r#""a\"b""#);
        assert_eq!(quote_arg(r"trailing\ "), r#""trailing\ ""#);
        assert_eq!(quote_arg(r"end\"), r"end\");
        assert_eq!(quote_arg(r"sp ace\"), r#""sp ace\\""#);
    }
}
