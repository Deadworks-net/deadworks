//! Loopback control API for tools that drive local hosting from outside the
//! launcher window: `deadworks-launcher mcp` (coding agents) and scripts.
//!
//! One `POST /rpc` per call with `{"method", "params"}`, answered with
//! `{"result"}` or `{"error"}`. It listens on 127.0.0.1 only, behind a bearer
//! token; the port and token are written to `control.json` in the launcher's
//! config folder, so only this OS user can find and use it.
//!
//! Besides the HOST tab's operations it has the waits a script needs instead of
//! sleeping: start until up, console until a line matches, a plugin deploy
//! until Deadworks reports the load.

use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use regex::Regex;
use serde::Serialize;
use serde_json::{json, Map, Value};

use super::manager::{self, Manager};
use super::types::*;

pub const DISCOVERY_FILE: &str = "control.json";

/// Bumped when a method changes shape; clients refuse a launcher that does not match.
pub const PROTO_VERSION: u32 = 1;

const POLL: Duration = Duration::from_millis(100);
/// Upper bound for any wait, whatever the caller asks for.
const MAX_WAIT: Duration = Duration::from_secs(600);
const MAX_LINES: usize = 400;

#[derive(Serialize)]
struct Discovery<'a> {
    proto: u32,
    port: u16,
    token: &'a str,
    pid: u32,
    version: &'a str,
}

/// Remove a discovery file an earlier run left, so clients don't wait on an API that is off.
pub fn stop(config_dir: &Path) {
    let _ = std::fs::remove_file(config_dir.join(DISCOVERY_FILE));
}

pub fn start(config_dir: PathBuf) {
    let server = match tiny_http::Server::http("127.0.0.1:0") {
        Ok(s) => s,
        Err(e) => {
            println!("[control] couldn't listen: {e}");
            return;
        }
    };
    let Some(port) = server.server_addr().to_ip().map(|a| a.port()) else { return };
    let token = uuid::Uuid::new_v4().simple().to_string();
    let discovery = Discovery { proto: PROTO_VERSION, port, token: &token, pid: std::process::id(), version: env!("CARGO_PKG_VERSION") };
    let _ = std::fs::create_dir_all(&config_dir);
    if let Err(e) = super::store::write_json(&config_dir.join(DISCOVERY_FILE), &discovery) {
        println!("[control] couldn't write {DISCOVERY_FILE}: {e}");
        return;
    }
    let auth = format!("Bearer {token}");
    std::thread::Builder::new()
        .name("hosting-control".into())
        .spawn(move || {
            for req in server.incoming_requests() {
                let auth = auth.clone();
                // Waits hold a request for minutes; each gets its own thread.
                std::thread::spawn(move || handle(req, &auth));
            }
        })
        .ok();
}

fn handle(mut req: tiny_http::Request, auth: &str) {
    let authorised = req
        .headers()
        .iter()
        .any(|h| h.field.equiv("Authorization") && h.value.as_str() == auth);
    let (status, body) = if !authorised {
        (401, json!({ "error": "Bad or missing token." }))
    } else if *req.method() != tiny_http::Method::Post || req.url() != "/rpc" {
        (404, json!({ "error": "POST /rpc only." }))
    } else {
        let mut text = String::new();
        let _ = std::io::Read::read_to_string(req.as_reader(), &mut text);
        match serde_json::from_str::<Value>(&text) {
            Ok(call) => {
                let method = call.get("method").and_then(Value::as_str).unwrap_or_default();
                let params = call.get("params").cloned().unwrap_or(Value::Null);
                match dispatch(method, &params) {
                    Ok(result) => (200, json!({ "result": result })),
                    Err(e) => (200, json!({ "error": e })),
                }
            }
            Err(e) => (400, json!({ "error": format!("Bad JSON: {e}") })),
        }
    };
    let header = tiny_http::Header::from_bytes("Content-Type", "application/json").unwrap();
    let _ = req.respond(tiny_http::Response::from_string(body.to_string()).with_status_code(status).with_header(header));
}

pub fn dispatch(method: &str, p: &Value) -> Result<Value, String> {
    let mgr = manager::get();
    match method {
        "hello" => Ok(json!({ "proto": PROTO_VERSION, "version": env!("CARGO_PKG_VERSION") })),
        "overview" => to_value(mgr.overview()),
        "maps" => to_value(mgr.maps()),
        "hosting.setup_check" => to_value(mgr.setup_check()),
        // Starts the install task and returns; `overview` shows its progress.
        "hosting.install" => {
            let opts: InstallOptions = serde_json::from_value(p.clone()).map_err(|e| format!("Bad install options: {e}"))?;
            mgr.install(opts)?;
            Ok(Value::Null)
        }

        "server.list" => to_value(mgr.overview().servers),
        "server.get" => to_value(mgr.summary(&resolve(mgr, p)?)?),
        "server.create" => {
            let name = str_param(p, "name")?;
            let network = match p.get("network") {
                Some(v) => serde_json::from_value(v.clone()).map_err(|e| format!("network: {e}"))?,
                None => NetworkMode::Lan,
            };
            let config = mgr.create_server(name, network)?;
            match p.get("settings") {
                Some(settings) => to_value(patch_config(mgr, config, settings)?),
                None => to_value(config),
            }
        }
        "server.update" => {
            let id = resolve(mgr, p)?;
            let config = mgr.summary(&id)?.config;
            to_value(patch_config(mgr, config, p.get("settings").unwrap_or(&Value::Null))?)
        }
        "server.delete" => {
            mgr.delete_server(&resolve(mgr, p)?)?;
            Ok(Value::Null)
        }
        "server.start" => start_server(mgr, p, false),
        "server.restart" => start_server(mgr, p, true),
        "server.stop" => {
            let id = resolve(mgr, p)?;
            mgr.stop(&id)?;
            if bool_param(p, "wait", true) {
                let deadline = Instant::now() + timeout(p, 30_000);
                while mgr.runtime(&id)?.state == ServerState::Stopping || mgr.runtime(&id)?.state == ServerState::Starting {
                    if Instant::now() >= deadline {
                        return Err("The server is still stopping.".into());
                    }
                    std::thread::sleep(POLL);
                }
            }
            to_value(mgr.summary(&id)?)
        }
        "server.crash_report" => {
            let id = resolve(mgr, p)?;
            let rt = mgr.runtime(&id)?;
            let (console, _) = mgr.console_after(&id, 0)?;
            let (logs, game_bin) = mgr.server_paths(&id)?;
            let mut dirs = vec![game_bin.clone(), game_bin.join("../../citadel"), logs.join("..")];
            if let Some(root) = mgr.root() {
                dirs.push(root.join("cache").join("dumps"));
            }
            Ok(super::crash::report(&rt, &console, &dirs))
        }
        "server.paths" => {
            let (logs, game_bin) = mgr.server_paths(&resolve(mgr, p)?)?;
            Ok(json!({ "logs": logs, "gameBin": game_bin }))
        }

        "console.read" => {
            let id = resolve(mgr, p)?;
            let (mut lines, next) = mgr.console_after(&id, u64_param(p, "after", 0))?;
            if let Some(re) = opt_regex(p, "grep")? {
                lines.retain(|l| re.is_match(&l.text));
            }
            let limit = u64_param(p, "limit", 200).min(MAX_LINES as u64) as usize;
            let skipped = lines.len().saturating_sub(limit);
            lines.drain(..skipped);
            Ok(json!({ "lines": lines, "skipped": skipped, "next": next }))
        }
        "console.wait" => {
            let id = resolve(mgr, p)?;
            let re = opt_regex(p, "pattern")?.ok_or("pattern is required.")?;
            let after = match p.get("after").and_then(Value::as_u64) {
                Some(a) => a,
                None => mgr.console_after(&id, u64::MAX)?.1 - 1,
            };
            let w = wait_for_line(mgr, &id, after, timeout(p, 30_000), |l| re.is_match(&l.text))?;
            to_value(w)
        }
        "console.command" => {
            let id = resolve(mgr, p)?;
            let command = str_param(p, "command")?;
            let after = mgr.console_after(&id, u64::MAX)?.1 - 1;
            mgr.send_command(&id, &command)?;
            let lines = settle(mgr, &id, after, Duration::from_millis(u64_param(p, "settle_ms", 500)), timeout(p, 5_000))?;
            let next = lines.last().map(|l| l.seq + 1).unwrap_or(after + 1);
            Ok(json!({ "lines": lines, "next": next }))
        }

        // A built map or addon VPK into the server's content, switched on, and optionally made its map.
        "content.install" => {
            let id = resolve(mgr, p)?;
            let kind: ContentKind = serde_json::from_value(p.get("kind").cloned().unwrap_or(json!("map"))).map_err(|e| format!("kind: {e}"))?;
            let files = mgr.import_content(&id, vec![str_param(p, "path")?], kind)?;
            let mut config = mgr.summary(&id)?.config;
            for f in &files {
                let list = if kind == ContentKind::Map { &mut config.extra_maps } else { &mut config.content_addons };
                if !list.contains(&f.file_name) {
                    list.push(f.file_name.clone());
                }
                if kind == ContentKind::Map && bool_param(p, "set_map", false) {
                    config.map = f.file_name.trim_end_matches(".vpk").to_string();
                }
            }
            let config = mgr.update_server(config)?;
            let running = live(mgr.runtime(&id)?.state);
            Ok(json!({ "installed": files, "config": config, "restartNeeded": running }))
        }
        "plugin.list" => to_value(mgr.plugin_library()),
        "plugin.deploy" => deploy_plugin(mgr, p),
        "plugin.remove" => {
            // Disables it on every server first; a running one unloads it.
            mgr.remove_plugin(&str_param(p, "plugin")?)?;
            Ok(Value::Null)
        }
        "plugin.enable" => {
            let id = resolve(mgr, p)?;
            to_value(mgr.set_plugin_enabled(&id, &str_param(p, "plugin")?, bool_param(p, "enabled", true))?)
        }
        "plugin.config.list" => to_value(mgr.plugin_configs(&resolve(mgr, p)?)?),
        "plugin.config.read" => to_value(mgr.read_plugin_config(&resolve(mgr, p)?, &str_param(p, "plugin")?)?),
        "plugin.config.write" => {
            mgr.write_plugin_config(&resolve(mgr, p)?, &str_param(p, "plugin")?, &str_param(p, "text")?)?;
            Ok(Value::Null)
        }

        "bridge.enable" => {
            // The MCP server passes the agent kit's DevBridge; scripts pass their own build.
            if p.get("path").is_none() {
                return Err("path (a DevBridge build) is required.".into());
            }
            deploy_plugin(mgr, p)
        }
        "sdk.info" => sdk_info(mgr),
        "bridge.call" => {
            let (_, game_bin) = mgr.server_paths(&resolve(mgr, p)?)?;
            bridge_call(&game_bin, &str_param(p, "op")?, p.get("args").cloned().unwrap_or_else(|| json!({})), timeout(p, 15_000))
        }
        _ => Err(format!("Unknown method '{method}'.")),
    }
}

// ── SDK ──

/// Assemblies a plugin compiles against, from the installed release.
const SDK_FILES: &[&str] = &[
    "DeadworksManaged.Api.dll",
    "DeadworksManaged.Api.xml",
    "DeadworksManaged.Game.dll",
    "DeadworksManaged.Game.xml",
    "Google.Protobuf.dll",
];

/// Keep `<hosting root>/sdk` holding the installed release's API assemblies, so plugin projects
/// can reference them at a path that survives Deadworks updates.
fn sdk_info(mgr: &Manager) -> Result<Value, String> {
    let (managed, tag) = mgr.release_managed().ok_or("Hosting has no Deadworks release installed yet; run hosting_setup first.")?;
    let dir = mgr.root().ok_or("Hosting isn't set up.")?.join("sdk");
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let mut files = Vec::new();
    for name in SDK_FILES {
        let dst = dir.join(name);
        match std::fs::read(managed.join(name)) {
            Ok(data) => {
                super::fsutil::write_if_changed(&dst, &data).map_err(|e| format!("Couldn't update {name}: {e}"))?;
                files.push(*name);
            }
            // Not in this release (the generated game assembly is newer than some): drop a copy from an earlier one.
            Err(_) => {
                let _ = std::fs::remove_file(&dst);
            }
        }
    }
    Ok(json!({ "dir": dir, "deadworks": tag, "files": files }))
}

// ── DevBridge ──

/// One request to the DevBridge plugin of the server whose `game/bin/win64` is `game_bin`.
fn bridge_call(game_bin: &Path, op: &str, mut args: Value, limit: Duration) -> Result<Value, String> {
    use std::io::{BufRead, BufReader, Write};
    const NOT_RUNNING: &str = "DevBridge isn't running on this server. Enable it with bridge.enable (the server must be running).";
    let text = std::fs::read_to_string(game_bin.join("configs").join("dev_bridge.json")).map_err(|_| NOT_RUNNING.to_string())?;
    let d: Value = serde_json::from_str(&text).map_err(|e| e.to_string())?;
    let port = d["port"].as_u64().ok_or(NOT_RUNNING)? as u16;
    let addr = std::net::SocketAddr::from(([127, 0, 0, 1], port));
    let mut stream = std::net::TcpStream::connect_timeout(&addr, Duration::from_secs(3)).map_err(|_| NOT_RUNNING.to_string())?;
    // The plugin's own game-thread wait ends first and says why.
    args["timeout_ms"] = json!(limit.as_millis() as u64);
    let _ = stream.set_read_timeout(Some(limit + Duration::from_secs(30)));
    let request = json!({ "token": d["token"], "op": op, "args": args });
    writeln!(stream, "{request}").map_err(|e| e.to_string())?;
    let mut line = String::new();
    BufReader::new(stream).read_line(&mut line).map_err(|e| format!("DevBridge didn't answer: {e}"))?;
    let reply: Value = serde_json::from_str(&line).map_err(|e| format!("Bad reply from DevBridge: {e}"))?;
    if reply["ok"].as_bool() == Some(true) {
        Ok(reply["result"].clone())
    } else {
        Err(reply["error"].as_str().unwrap_or("DevBridge failed.").to_string())
    }
}

// ── Waits ──

#[derive(Serialize)]
struct LineWait {
    matched: Option<ConsoleLine>,
    /// Why the wait ended without a match: "timeout" or "stopped".
    #[serde(skip_serializing_if = "Option::is_none")]
    reason: Option<&'static str>,
    lines: Vec<ConsoleLine>,
    skipped: usize,
    next: u64,
}

/// Wait for a console line after `after` that satisfies `hit`. Ends early when the server is
/// no longer live, since nothing more will be printed.
fn wait_for_line(mgr: &Manager, id: &str, after: u64, limit: Duration, mut hit: impl FnMut(&ConsoleLine) -> bool) -> Result<LineWait, String> {
    let deadline = Instant::now() + limit;
    let mut seen = Vec::new();
    let mut cursor = after;
    loop {
        let (lines, _) = mgr.console_after(id, cursor)?;
        for l in lines {
            cursor = l.seq;
            let found = hit(&l);
            seen.push(l);
            if found {
                return Ok(finish(seen, None, cursor));
            }
        }
        if !live(mgr.runtime(id)?.state) {
            return Ok(finish(seen, Some("stopped"), cursor));
        }
        if Instant::now() >= deadline {
            return Ok(finish(seen, Some("timeout"), cursor));
        }
        std::thread::sleep(POLL);
    }

    fn finish(mut lines: Vec<ConsoleLine>, reason: Option<&'static str>, cursor: u64) -> LineWait {
        let matched = if reason.is_none() { lines.last().cloned() } else { None };
        let skipped = lines.len().saturating_sub(MAX_LINES);
        lines.drain(..skipped);
        LineWait { matched, reason, lines, skipped, next: cursor + 1 }
    }
}

/// Collect output after `after` until none has arrived for `quiet`, or `limit` passes.
fn settle(mgr: &Manager, id: &str, after: u64, quiet: Duration, limit: Duration) -> Result<Vec<ConsoleLine>, String> {
    let deadline = Instant::now() + limit;
    let mut last_change = Instant::now();
    let mut count = 0;
    loop {
        std::thread::sleep(POLL);
        let (lines, _) = mgr.console_after(id, after)?;
        if lines.len() != count {
            count = lines.len();
            last_change = Instant::now();
        }
        if last_change.elapsed() >= quiet || Instant::now() >= deadline {
            let mut lines = lines;
            let skipped = lines.len().saturating_sub(MAX_LINES);
            lines.drain(..skipped);
            return Ok(lines);
        }
    }
}

fn live(state: ServerState) -> bool {
    matches!(state, ServerState::Starting | ServerState::Running | ServerState::Stopping)
}

/// Start (or restart) and, unless `wait` is false, wait until the server is up or has failed.
/// A failed start returns the console since the attempt began, which is where the reason is.
fn start_server(mgr: &std::sync::Arc<Manager>, p: &Value, restart: bool) -> Result<Value, String> {
    let id = resolve(mgr, p)?;
    let before = mgr.runtime(&id)?;
    let after = mgr.console_after(&id, u64::MAX)?.1 - 1;
    if restart {
        mgr.restart(&id)?;
    } else {
        if before.state == ServerState::Running {
            return Ok(json!({ "server": mgr.summary(&id)?, "alreadyRunning": true }));
        }
        mgr.start(&id)?;
    }
    if !bool_param(p, "wait", true) {
        return to_value(mgr.summary(&id)?);
    }
    let deadline = Instant::now() + timeout(p, 240_000);
    let mut timed_out = false;
    loop {
        let rt = mgr.runtime(&id)?;
        // A restart passes through the old run's Running; only a new start time is the new run.
        let new_run = !restart || (rt.started_at.is_some() && rt.started_at != before.started_at);
        match rt.state {
            ServerState::Running if new_run => break,
            ServerState::Crashed | ServerState::WaitingForDeadworks => break,
            ServerState::Stopped if new_run || !restart => break,
            _ => {}
        }
        if Instant::now() >= deadline {
            timed_out = true;
            break;
        }
        std::thread::sleep(Duration::from_millis(250));
    }
    let summary = mgr.summary(&id)?;
    let up = summary.runtime.state == ServerState::Running;
    let (mut console, _) = mgr.console_after(&id, after)?;
    let keep = if up { 0 } else { 80 };
    console.drain(..console.len().saturating_sub(keep));
    // A start still waiting (say, on the Windows Firewall prompt the user hasn't answered) says so in its console.
    Ok(json!({ "server": summary, "up": up, "timedOut": timed_out, "console": console }))
}

/// Import a built plugin into the library, make sure the server has it enabled, and on a
/// running server wait for Deadworks to report loading (or failing to load) it.
fn deploy_plugin(mgr: &std::sync::Arc<Manager>, p: &Value) -> Result<Value, String> {
    let path = str_param(p, "path")?;
    if !Path::new(&path).exists() {
        return Err(format!("{path} doesn't exist."));
    }
    let server = match p.get("server").or_else(|| p.get("id")) {
        Some(_) => Some(resolve(mgr, p)?),
        None => None,
    };
    let Some(id) = server else {
        return to_value(mgr.import_plugins(vec![path])?);
    };
    let running = mgr.runtime(&id)?.state == ServerState::Running;
    let after = mgr.console_after(&id, u64::MAX)?.1 - 1;
    let installed = mgr.server_paths(&id)?.1.join("managed").join("plugins");
    let before = fingerprint(&installed);
    let entries = mgr.import_plugins(vec![path])?;
    let enabled = mgr.summary(&id)?.config.plugins;
    let mut newly_enabled = false;
    for e in &entries {
        if !enabled.contains(&e.id) {
            mgr.set_plugin_enabled(&id, &e.id, true)?;
            newly_enabled = true;
        }
    }
    if !running {
        return Ok(json!({ "plugins": entries, "loaded": false, "note": "The server isn't running; the plugin loads when it starts." }));
    }
    // Same bytes as the loaded build (a rebuild without changes): no file event, so no reload.
    if !newly_enabled && fingerprint(&installed) == before {
        return Ok(json!({ "plugins": entries, "loaded": true, "note": "Unchanged since the last deploy; the loaded build is this one." }));
    }
    let mut pending = entries.len();
    let mut failed = false;
    let w = wait_for_line(mgr, &id, after, timeout(p, 20_000), |l| {
        let t = &l.text;
        if !t.contains("[PluginLoader]") {
            return false;
        }
        if t.contains("Loaded plugin:") {
            pending = pending.saturating_sub(1);
        } else if t.contains("Failed") || t.contains("Cannot enable") || t.contains("threw:") {
            failed = true;
        }
        failed || pending == 0
    })?;
    let loader: Vec<&ConsoleLine> = w.lines.iter().filter(|l| l.text.contains("[PluginLoader]") || l.text.contains("Exception")).collect();
    Ok(json!({
        "plugins": entries,
        "loaded": !failed && pending == 0,
        "reason": w.reason,
        "loaderLines": loader,
        "next": w.next,
    }))
}

/// Names and contents of the files in a folder, to tell whether a deploy changed any.
fn fingerprint(dir: &Path) -> Vec<(String, u64)> {
    use std::hash::{Hash, Hasher};
    let mut out: Vec<(String, u64)> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|e| {
            let bytes = std::fs::read(e.path()).ok()?;
            let mut h = std::collections::hash_map::DefaultHasher::new();
            bytes.hash(&mut h);
            Some((e.file_name().to_string_lossy().into_owned(), h.finish()))
        })
        .collect();
    out.sort();
    out
}

/// Merge `settings` (a partial ServerConfig, camelCase keys) into `config` and save it.
fn patch_config(mgr: &Manager, config: ServerConfig, settings: &Value) -> Result<ServerConfig, String> {
    let Some(patch) = settings.as_object() else {
        return if settings.is_null() { Ok(config) } else { Err("settings must be an object.".into()) };
    };
    let mut merged: Map<String, Value> = match to_value(&config)? {
        Value::Object(m) => m,
        _ => unreachable!(),
    };
    for (k, v) in patch {
        if k == "id" {
            return Err("A server's id can't change.".into());
        }
        if !merged.contains_key(k) {
            let known: Vec<&String> = merged.keys().collect();
            return Err(format!("Unknown setting '{k}'. Settings: {known:?}"));
        }
        merged.insert(k.clone(), v.clone());
    }
    let config: ServerConfig = serde_json::from_value(Value::Object(merged)).map_err(|e| format!("Bad settings: {e}"))?;
    mgr.update_server(config)
}

// ── Params ──

/// The server named by `id` (or `server`): an id, or a server name ignoring case.
fn resolve(mgr: &Manager, p: &Value) -> Result<String, String> {
    let want = p
        .get("id")
        .or_else(|| p.get("server"))
        .and_then(Value::as_str)
        .ok_or("id (a server id or name) is required.")?;
    let servers = mgr.overview().servers;
    if let Some(s) = servers.iter().find(|s| s.config.id == want) {
        return Ok(s.config.id.clone());
    }
    let by_name: Vec<&ServerSummary> = servers.iter().filter(|s| s.config.name.eq_ignore_ascii_case(want)).collect();
    match by_name.as_slice() {
        [one] => Ok(one.config.id.clone()),
        [] => Err(format!("No server '{want}'. Servers: {:?}", servers.iter().map(|s| &s.config.id).collect::<Vec<_>>())),
        _ => Err(format!("More than one server is named '{want}'; use its id.")),
    }
}

fn str_param(p: &Value, key: &str) -> Result<String, String> {
    p.get(key).and_then(Value::as_str).map(String::from).ok_or_else(|| format!("{key} is required."))
}

fn u64_param(p: &Value, key: &str, default: u64) -> u64 {
    p.get(key).and_then(Value::as_u64).unwrap_or(default)
}

fn bool_param(p: &Value, key: &str, default: bool) -> bool {
    p.get(key).and_then(Value::as_bool).unwrap_or(default)
}

fn timeout(p: &Value, default_ms: u64) -> Duration {
    Duration::from_millis(u64_param(p, "timeout_ms", default_ms)).min(MAX_WAIT)
}

fn opt_regex(p: &Value, key: &str) -> Result<Option<Regex>, String> {
    match p.get(key).and_then(Value::as_str) {
        Some(s) => Regex::new(s).map(Some).map_err(|e| format!("{key}: {e}")),
        None => Ok(None),
    }
}

fn to_value(v: impl Serialize) -> Result<Value, String> {
    serde_json::to_value(v).map_err(|e| e.to_string())
}
