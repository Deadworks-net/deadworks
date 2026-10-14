//! What ended a server's last run, for the control API's `server.crash_report`: the exit code
//! decoded, the .NET fatal-error block with the frames that are not the runtime's or Deadworks',
//! Deadworks' error lines, known engine exits, and the crash site of any minidump the run left.

use std::path::{Path, PathBuf};

use serde_json::{json, Value};

use super::types::{ConsoleLine, LineKind, ServerRuntime};

/// Console lines that explain an exit on their own. Matched as substrings of one line.
const SIGNATURES: &[(&str, &str)] = &[
    ("Failed to find signature", "This Deadworks release doesn't support the installed game build; servers start again once a compatible release is installed."),
    ("missing responses from the GC", "The engine quits on its own after about 17 hours of being empty (Game Coordinator watchdog). Not a crash."),
    ("Maximum uptime reached", "The engine's built-in uptime limit (48-72 hours). Not a crash."),
    ("Encountered accessviolation", "An access violation in native code. A minidump, if one was written, has the module and offset."),
    ("Stack overflow.", "A stack overflow, usually unbounded recursion in a plugin; the frames above it repeat."),
];

pub fn report(rt: &ServerRuntime, console: &[ConsoleLine], dump_dirs: &[PathBuf]) -> Value {
    // The last run: from the launcher's "Starting ..." line on.
    let start = console
        .iter()
        .rposition(|l| l.kind == LineKind::Sys && l.text.starts_with("Starting "))
        .unwrap_or(0);
    let run = &console[start..];

    let fatal_at = run.iter().position(|l| l.text.trim() == "Fatal error.");
    let fatal_len = fatal_at.map(|i| run[i..].iter().take(80).take_while(|l| l.kind == LineKind::Out).count()).unwrap_or(0);
    let fatal = fatal_at.map(|i| {
        let block: Vec<&str> = run[i..i + fatal_len].iter().map(|l| l.text.as_str()).collect();
        let own_frames: Vec<&str> = block
            .iter()
            .map(|l| l.trim())
            .filter(|l| l.starts_with("at ") && !is_framework_frame(l))
            .collect();
        json!({ "lines": block, "ownFrames": own_frames })
    });

    let errors: Vec<&str> = run
        .iter()
        .map(|l| l.text.as_str())
        .filter(|t| t.contains("] [ERR] ") || t.contains("] [CRT] ") || (t.contains("[PluginLoader]") && (t.contains("threw:") || t.contains("Failed"))))
        .collect();
    let errors = &errors[errors.len().saturating_sub(30)..];

    let known: Vec<Value> = SIGNATURES
        .iter()
        .filter_map(|(needle, meaning)| run.iter().find(|l| l.text.contains(needle)).map(|l| json!({ "line": l.text, "meaning": meaning })))
        .collect();

    // Only dumps from this run; without a start time there is no telling which those are.
    let dumps: Vec<Value> = match rt.started_at {
        Some(since) => dump_dirs.iter().flat_map(|d| find_dumps(d, since)).map(|p| describe_dump(&p)).collect(),
        None => Vec::new(),
    };

    // The last lines, without the fatal block again.
    let tail: Vec<&str> = run
        .iter()
        .enumerate()
        .filter(|(i, _)| fatal_at.is_none_or(|f| *i < f || *i >= f + fatal_len))
        .map(|(_, l)| l.text.as_str())
        .collect();
    let tail = &tail[tail.len().saturating_sub(30)..];
    json!({
        "state": rt.state,
        "exitCode": rt.exit_code,
        "exit": rt.exit_code.map(exit_meaning),
        "message": rt.message,
        "fatal": fatal,
        "errors": errors,
        "known": known,
        "dumps": dumps,
        "tail": tail,
    })
}

fn is_framework_frame(frame: &str) -> bool {
    ["at System.", "at Microsoft.", "at DeadworksManaged.", "at DeadworksDevBridge.", "at InvokeStub_"].iter().any(|p| frame.starts_with(p))
}

fn exit_meaning(code: i32) -> String {
    match code as u32 {
        0 => "Exited normally.".into(),
        1 => "Exit code 1: ended by the launcher (killed after a stop timeout or a fatal error), or the engine quit with an error.".into(),
        5 => "Exit code 5: under Wine, a .NET fatal error (fail-fast); the managed stack is in the console.".into(),
        78 => "Exit code 78: Deadworks doesn't support this game build.".into(),
        0xC0000005 => "0xC0000005: access violation.".into(),
        0xC00000FD => "0xC00000FD: stack overflow.".into(),
        0xC0000409 => "0xC0000409: fail-fast (a .NET fatal error or a security check).".into(),
        0x80131506 => "0x80131506: the .NET runtime failed (execution engine error).".into(),
        0xE0434352 => "0xE0434352: an unhandled .NET exception.".into(),
        c => format!("Exit code {code} (0x{c:08X})."),
    }
}

/// Minidumps in `dir` (not recursive) written since `since` (unix seconds).
fn find_dumps(dir: &Path, since: u64) -> Vec<PathBuf> {
    let Ok(rd) = std::fs::read_dir(dir) else { return Vec::new() };
    rd.flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dmp") || x.eq_ignore_ascii_case("mdmp")))
        .filter(|p| {
            let modified = std::fs::metadata(p).and_then(|m| m.modified()).ok();
            let secs = modified.and_then(|m| m.duration_since(std::time::UNIX_EPOCH).ok()).map(|d| d.as_secs()).unwrap_or(0);
            secs + 5 >= since
        })
        .collect()
}

fn describe_dump(path: &Path) -> Value {
    match std::fs::read(path).ok().and_then(|b| crash_site(&b)) {
        Some(site) => json!({ "path": path, "exception": format!("0x{:08X}", site.code), "at": site.location }),
        None => json!({ "path": path }),
    }
}

struct Site {
    code: u32,
    location: String,
}

/// The exception code and `module+offset` of the faulting address, from a minidump's exception
/// and module list streams.
fn crash_site(b: &[u8]) -> Option<Site> {
    let u32_at = |o: usize| b.get(o..o + 4).map(|s| u32::from_le_bytes(s.try_into().unwrap()));
    let u64_at = |o: usize| b.get(o..o + 8).map(|s| u64::from_le_bytes(s.try_into().unwrap()));
    if b.get(0..4)? != b"MDMP" {
        return None;
    }
    let (count, dir) = (u32_at(8)? as usize, u32_at(12)? as usize);
    let stream = |kind: u32| (0..count).map(|i| dir + i * 12).find(|&e| u32_at(e) == Some(kind)).and_then(|e| u32_at(e + 8)).map(|rva| rva as usize);

    // MINIDUMP_EXCEPTION_STREAM: thread id, alignment, then MINIDUMP_EXCEPTION (code, flags, record, address).
    let ex = stream(6)?;
    let code = u32_at(ex + 8)?;
    let address = u64_at(ex + 24)?;

    let mut location = format!("0x{address:X}");
    if let Some(list) = stream(4) {
        let modules = u32_at(list)? as usize;
        for i in 0..modules {
            // MINIDUMP_MODULE is 108 bytes: base, size, checksum, timestamp, name RVA, ...
            let m = list + 4 + i * 108;
            let (base, size) = (u64_at(m)?, u32_at(m + 8)? as u64);
            if address >= base && address < base + size {
                let name_rva = u32_at(m + 20)? as usize;
                let len = u32_at(name_rva)? as usize;
                let utf16: Vec<u16> = b.get(name_rva + 4..name_rva + 4 + len)?.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
                let name = String::from_utf16_lossy(&utf16);
                let file = name.rsplit(['\\', '/']).next().unwrap_or(&name).to_string();
                location = format!("{file}+0x{:X}", address - base);
                break;
            }
        }
    }
    Some(Site { code, location })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn line(seq: u64, kind: LineKind, text: &str) -> ConsoleLine {
        ConsoleLine { seq, kind, text: text.into() }
    }

    #[test]
    fn a_fatal_error_block_keeps_the_plugins_own_frames() {
        let console = vec![
            line(1, LineKind::Sys, "Starting Old"),
            line(2, LineKind::Out, "Fatal error."),
            line(3, LineKind::Sys, "Starting Test (Deadworks v0.6.1, game build 1)"),
            line(4, LineKind::Out, "[2026-10-14 11:00:00.000] [deadworks] [ERR] [managed] something broke"),
            line(5, LineKind::Out, "Fatal error."),
            line(6, LineKind::Out, "System.AccessViolationException: Attempted to read or write protected memory."),
            line(7, LineKind::Out, "   at System.Runtime.InteropServices.Marshal.WriteInt32(IntPtr, Int32, Int32)"),
            line(8, LineKind::Out, "   at MyPlugin.Boom.Run()"),
            line(9, LineKind::Out, "   at DeadworksManaged.TimerEngine.OnTick()"),
            line(10, LineKind::Sys, "Server stopped (exit code 1)."),
        ];
        let r = report(&test_runtime(), &console, &[]);
        assert_eq!(r["fatal"]["ownFrames"], json!(["at MyPlugin.Boom.Run()"]));
        assert_eq!(r["fatal"]["lines"].as_array().unwrap().len(), 5);
        assert_eq!(r["errors"].as_array().unwrap().len(), 1);
    }

    #[test]
    fn a_minidump_names_the_faulting_module_and_offset() {
        // Header, a two-entry directory, an exception stream and a one-module list.
        let mut b = vec![0u8; 400];
        b[0..4].copy_from_slice(b"MDMP");
        b[8..12].copy_from_slice(&2u32.to_le_bytes());
        b[12..16].copy_from_slice(&32u32.to_le_bytes());
        let put = |b: &mut Vec<u8>, o: usize, v: &[u8]| b[o..o + v.len()].copy_from_slice(v);
        put(&mut b, 32, &6u32.to_le_bytes());
        put(&mut b, 40, &100u32.to_le_bytes());
        put(&mut b, 44, &4u32.to_le_bytes());
        put(&mut b, 52, &200u32.to_le_bytes());
        put(&mut b, 108, &0xC000_0005u32.to_le_bytes());
        put(&mut b, 124, &0x1_8000_1234u64.to_le_bytes());
        put(&mut b, 200, &1u32.to_le_bytes());
        put(&mut b, 204, &0x1_8000_0000u64.to_le_bytes());
        put(&mut b, 212, &0x10000u32.to_le_bytes());
        put(&mut b, 224, &320u32.to_le_bytes());
        let name: Vec<u8> = "C:\\x\\client.dll".encode_utf16().flat_map(|c| c.to_le_bytes()).collect();
        put(&mut b, 320, &(name.len() as u32).to_le_bytes());
        put(&mut b, 324, &name);
        let site = crash_site(&b).unwrap();
        assert_eq!(site.code, 0xC000_0005);
        assert_eq!(site.location, "client.dll+0x1234");
    }

    fn test_runtime() -> ServerRuntime {
        use super::super::types::*;
        ServerRuntime {
            id: "t".into(),
            state: ServerState::Crashed,
            pid: None,
            started_at: None,
            players: vec![],
            cpu_percent: 0.0,
            memory_bytes: 0,
            message: None,
            exit_code: Some(1),
            network: NetworkInfo {
                mode: NetworkMode::Lan,
                port: 0,
                lan_ips: vec![],
                public_ip: None,
                reachability: Reachability::Unknown,
                sdr_id: None,
                sdr_id_changed: false,
                connect_command: None,
            },
            moderation: false,
        }
    }
}
