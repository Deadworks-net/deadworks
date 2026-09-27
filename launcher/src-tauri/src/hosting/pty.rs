//! Running a line-oriented console tool (SteamCMD) under a pseudo console.
//!
//! SteamCMD buffers its stdout when that is a pipe, so its prompts
//! ("password:", "Steam Guard code:") never arrive before it blocks on input.
//! Under ConPTY it sees a terminal and flushes. The game server can't use this
//! (its status bar makes ConPTY repaint the whole screen), but SteamCMD only
//! prints lines. ConPTY first asks for the cursor position (`ESC[6n`) and
//! waits for the answer; the reader replies.

/// Turns a terminal byte stream into text lines: escape sequences removed,
/// `\r` rewinds the current line, and a trailing prompt (ends in `:` or `?`)
/// is delivered without waiting for a newline.
#[derive(Default)]
pub struct VtLines {
    pending: Vec<u8>,
    line: String,
    prompt_sent: bool,
}

pub struct Fed {
    pub lines: Vec<String>,
    /// The terminal asked for the cursor position; answer `ESC[1;1R`.
    pub cursor_query: bool,
}

impl VtLines {
    pub fn feed(&mut self, bytes: &[u8]) -> Fed {
        self.pending.extend_from_slice(bytes);
        let mut out = Fed { lines: Vec::new(), cursor_query: false };
        let buf = std::mem::take(&mut self.pending);
        let text = String::from_utf8_lossy(&buf).into_owned();
        let chars: Vec<char> = text.chars().collect();
        let mut i = 0;
        while i < chars.len() {
            let c = chars[i];
            if c == '\x1b' {
                match chars.get(i + 1) {
                    None => {
                        // Incomplete: keep for the next chunk.
                        self.pending = chars[i..].iter().collect::<String>().into_bytes();
                        break;
                    }
                    Some('[') => {
                        let mut j = i + 2;
                        while j < chars.len() && !('\x40'..='\x7e').contains(&chars[j]) {
                            j += 1;
                        }
                        if j >= chars.len() {
                            self.pending = chars[i..].iter().collect::<String>().into_bytes();
                            break;
                        }
                        let params: String = chars[i + 2..j].iter().collect();
                        if chars[j] == 'n' && params == "6" {
                            out.cursor_query = true;
                        }
                        i = j + 1;
                    }
                    Some(']') => {
                        let mut j = i + 2;
                        let mut end = None;
                        while j < chars.len() {
                            if chars[j] == '\x07' {
                                end = Some(j + 1);
                                break;
                            }
                            if chars[j] == '\x1b' && chars.get(j + 1) == Some(&'\\') {
                                end = Some(j + 2);
                                break;
                            }
                            j += 1;
                        }
                        match end {
                            Some(e) => i = e,
                            None => {
                                self.pending = chars[i..].iter().collect::<String>().into_bytes();
                                break;
                            }
                        }
                    }
                    Some(_) => i += 2,
                }
                continue;
            }
            match c {
                '\n' => {
                    let line = std::mem::take(&mut self.line);
                    // A prompt already went out when it appeared.
                    if !self.prompt_sent {
                        out.lines.push(line);
                    }
                    self.prompt_sent = false;
                }
                '\r' => {
                    if chars.get(i + 1) != Some(&'\n') {
                        self.line.clear();
                    }
                }
                c if (c as u32) < 0x20 && c != '\t' => {}
                c => self.line.push(c),
            }
            i += 1;
        }
        let t = self.line.trim_end();
        if !self.prompt_sent && (t.ends_with(':') || t.ends_with('?')) {
            self.prompt_sent = true;
            out.lines.push(self.line.clone());
        }
        out
    }
}

#[cfg(windows)]
mod win {
    use std::io::{Read, Write};
    use std::sync::Mutex;

    use portable_pty::{native_pty_system, Child, CommandBuilder, MasterPty, PtySize};

    use super::VtLines;
    use crate::hosting::process::SpawnSpec;

    pub struct PtyProcess {
        writer: Mutex<Box<dyn Write + Send>>,
        child: Mutex<Box<dyn Child + Send + Sync>>,
        _master: Mutex<Box<dyn MasterPty + Send>>,
    }

    impl PtyProcess {
        /// Type `line` + Enter.
        pub fn send_line(&self, line: &str) -> Result<(), String> {
            let mut w = self.writer.lock().unwrap_or_else(|e| e.into_inner());
            w.write_all(format!("{line}\r").as_bytes())
                .and_then(|_| w.flush())
                .map_err(|e| format!("Couldn't type into SteamCMD: {e}"))
        }

        pub fn terminate(&self) {
            let _ = self.child.lock().unwrap_or_else(|e| e.into_inner()).kill();
        }
    }

    pub fn spawn(
        spec: SpawnSpec,
        on_line: impl Fn(String) + Send + 'static,
        on_exit: impl FnOnce(i32) + Send + 'static,
    ) -> Result<std::sync::Arc<PtyProcess>, String> {
        let pair = native_pty_system()
            .openpty(PtySize { rows: 50, cols: 250, pixel_width: 0, pixel_height: 0 })
            .map_err(|e| format!("Couldn't start SteamCMD: {e}"))?;
        let mut cmd = CommandBuilder::new(&spec.exe);
        cmd.cwd(&spec.cwd);
        cmd.args(&spec.args);
        for (k, v) in &spec.env {
            cmd.env(k, v);
        }
        let child = pair.slave.spawn_command(cmd).map_err(|e| format!("Couldn't start SteamCMD: {e}"))?;
        drop(pair.slave);
        let mut reader = pair.master.try_clone_reader().map_err(|e| e.to_string())?;
        let writer = pair.master.take_writer().map_err(|e| e.to_string())?;
        let proc = std::sync::Arc::new(PtyProcess {
            writer: Mutex::new(writer),
            child: Mutex::new(child),
            _master: Mutex::new(pair.master),
        });

        let p = proc.clone();
        std::thread::spawn(move || {
            let mut vt = VtLines::default();
            let mut buf = [0u8; 8192];
            loop {
                let n = match reader.read(&mut buf) {
                    Ok(0) | Err(_) => break,
                    Ok(n) => n,
                };
                let fed = vt.feed(&buf[..n]);
                if fed.cursor_query {
                    let mut w = p.writer.lock().unwrap_or_else(|e| e.into_inner());
                    let _ = w.write_all(b"\x1b[1;1R").and_then(|_| w.flush());
                }
                for l in fed.lines {
                    on_line(l);
                }
            }
        });

        let p = proc.clone();
        std::thread::spawn(move || {
            // The pty's output pipe doesn't close by itself; poll for the exit.
            let code = loop {
                if let Ok(Some(status)) = p.child.lock().unwrap_or_else(|e| e.into_inner()).try_wait() {
                    break status.exit_code() as i32;
                }
                std::thread::sleep(std::time::Duration::from_millis(200));
            };
            // Let the reader drain what the process printed last.
            std::thread::sleep(std::time::Duration::from_millis(300));
            on_exit(code);
        });
        Ok(proc)
    }
}

#[cfg(windows)]
pub use win::spawn;

#[cfg(not(windows))]
pub struct PtyProcess;

#[cfg(not(windows))]
impl PtyProcess {
    pub fn send_line(&self, _line: &str) -> Result<(), String> {
        Err("SteamCMD is only supported on Windows.".into())
    }
    pub fn terminate(&self) {}
}

#[cfg(not(windows))]
pub fn spawn(
    _spec: crate::hosting::process::SpawnSpec,
    _on_line: impl Fn(String) + Send + 'static,
    _on_exit: impl FnOnce(i32) + Send + 'static,
) -> Result<std::sync::Arc<PtyProcess>, String> {
    Err("SteamCMD is only supported on Windows.".into())
}

#[cfg(test)]
mod tests {
    use super::VtLines;

    #[test]
    fn strips_escapes_rewinds_lines_and_surfaces_prompts() {
        let mut vt = VtLines::default();
        let fed = vt.feed(b"\x1b[6n\x1b]0;steamcmd\x07Steam Console Client\rSteam Console Client - version 1\r\n\x1b[32mLoading...OK\x1b[m\r\n");
        assert!(fed.cursor_query);
        assert_eq!(fed.lines, vec!["Steam Console Client - version 1", "Loading...OK"]);
        let fed = vt.feed(b"\r\npassword: ");
        assert_eq!(fed.lines, vec!["", "password: "]);
        // The same prompt isn't delivered twice while it waits.
        assert!(vt.feed(b"").lines.is_empty());
        let fed = vt.feed(b"\r\nProceeding\r\n");
        assert_eq!(fed.lines, vec!["Proceeding"]);
    }

    #[test]
    fn escape_sequences_split_across_reads() {
        let mut vt = VtLines::default();
        assert!(vt.feed(b"abc\x1b[3").lines.is_empty());
        let fed = vt.feed(b"2mdef\r\n");
        assert_eq!(fed.lines, vec!["abcdef"]);
    }
}
