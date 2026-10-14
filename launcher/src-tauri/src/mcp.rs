//! `deadworks-launcher mcp`: a Model Context Protocol server on stdio, so a
//! coding agent can create, run and watch local Deadworks servers and deploy
//! the plugin it is writing.
//!
//! It holds no hosting state of its own. Every tool is a call to the running
//! launcher's control API (src/hosting/control.rs); when no launcher is
//! running, the first tool call starts one in the background.

use std::io::{BufRead, Write};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use regex::Regex;
use serde_json::{json, Value};

mod docs;

const PROTOCOL_VERSION: &str = "2025-06-18";
const CONTROL_PROTO: u64 = 1;
const IDENTIFIER: &str = "net.deadworks.launcher";
const LAUNCH_WAIT: Duration = Duration::from_secs(45);

const INSTRUCTIONS: &str = "\
Drives local Deadworks (Deadlock) dedicated servers through the Deadworks launcher's hosting.

Typical plugin loop:
1. hosting_status: check hosting is installed (if not, hosting_setup once) and list servers. Reuse an existing dev server rather than creating one per task; \
a new server asks the user for a Windows Firewall rule on its first start.
2. create_server with settings {\"cheats\": true, \"hibernateWhenEmpty\": false} if there is none.
3. start_server: waits until the server is up and returns the console of a failed start.
4. Build the plugin (dotnet build), then deploy_plugin with the built .dll (or its output folder) and the server. \
On a running server Deadworks hot-reloads it; the result says whether it loaded and quotes the loader's lines.
5. run_command for console commands (it returns the output), console_read / console_wait to follow the log. \
Console lines carry a sequence number; pass the returned `next` as `after` to read only new lines.

To look inside the running game, bridge_enable loads the DevBridge plugin (development servers only; it runs code it is sent). Then game_status, find_entities, inspect_entity and set_entity_field read and write entities, eval_csharp runs C# on the game thread, and trace_start / trace_read record damage, modifiers, game events and more. These need ticks: keep hibernateWhenEmpty off.

Server ids or names are accepted wherever a tool takes `server`.";

pub fn run() {
    let out = Arc::new(Mutex::new(std::io::stdout()));
    let client = Arc::new(Client::default());
    for line in std::io::stdin().lock().lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        let msg: Value = match serde_json::from_str(&line) {
            Ok(v) => v,
            Err(e) => {
                write(&out, &json!({ "jsonrpc": "2.0", "id": null, "error": { "code": -32700, "message": e.to_string() } }));
                continue;
            }
        };
        let Some(id) = msg.get("id").cloned() else { continue }; // notifications need no answer
        let method = msg.get("method").and_then(Value::as_str).unwrap_or_default().to_string();
        let params = msg.get("params").cloned().unwrap_or(Value::Null);
        let (out, client) = (out.clone(), client.clone());
        // Tool calls can wait minutes (a server start); others must not queue behind them.
        std::thread::spawn(move || {
            let reply = match handle(&client, &method, &params) {
                Ok(result) => json!({ "jsonrpc": "2.0", "id": id, "result": result }),
                Err((code, message)) => json!({ "jsonrpc": "2.0", "id": id, "error": { "code": code, "message": message } }),
            };
            write(&out, &reply);
        });
    }
}

fn write(out: &Mutex<std::io::Stdout>, v: &Value) {
    let mut out = out.lock().unwrap_or_else(|e| e.into_inner());
    let _ = writeln!(out, "{v}");
    let _ = out.flush();
}

fn handle(client: &Client, method: &str, params: &Value) -> Result<Value, (i64, String)> {
    match method {
        "initialize" => {
            let version = params.get("protocolVersion").and_then(Value::as_str).unwrap_or(PROTOCOL_VERSION);
            Ok(json!({
                "protocolVersion": version,
                "capabilities": { "tools": {} },
                "serverInfo": { "name": "deadworks", "version": env!("CARGO_PKG_VERSION") },
                "instructions": INSTRUCTIONS,
            }))
        }
        "ping" => Ok(json!({})),
        "tools/list" => Ok(json!({ "tools": tools() })),
        "tools/call" => {
            let name = params.get("name").and_then(Value::as_str).unwrap_or_default();
            let args = params.get("arguments").cloned().unwrap_or_else(|| json!({}));
            let (text, is_error) = match call_tool(client, name, &args) {
                Ok(text) => (text, false),
                Err(e) => (e, true),
            };
            Ok(json!({ "content": [{ "type": "text", "text": text }], "isError": is_error }))
        }
        _ => Err((-32601, format!("Unknown method {method}"))),
    }
}

// ── Tools ──

fn tools() -> Value {
    let server = json!({ "type": "string", "description": "Server id or name." });
    let timeout = json!({ "type": "integer", "description": "Milliseconds to wait at most." });
    let after = json!({ "type": "integer", "description": "Only lines after this sequence number (the `next` of an earlier read)." });
    let settings = json!({
        "type": "object",
        "description": "ServerConfig fields to change, camelCase: name, password, maxPlayers, map, port, network (sdr|port_forward|lan), \
listed, cheats, hibernateWhenEmpty, plugins, cvars [{key,value}], launchArgs, contentAddons, extraMaps.",
    });
    let tool = |name: &str, description: &str, properties: Value, required: &[&str]| {
        json!({
            "name": name,
            "description": description,
            "inputSchema": { "type": "object", "properties": properties, "required": required },
        })
    };
    json!([
        tool("hosting_status", "Whether hosting is installed, the game build and Deadworks version, and every server with its state.", json!({}), &[]),
        tool(
            "hosting_setup",
            "One-time setup of local hosting: copies (or, on Deadlock's drive, hardlinks) the game from the Steam install and installs Deadworks and .NET into a hosting folder. Starts the work and returns; hosting_status shows progress. Takes a few minutes and tens of GB when copying.",
            json!({
                "folder": { "type": "string", "description": "Where hosting goes. Default: the launcher's suggestion." },
                "allow_modified": { "type": "boolean", "description": "Go ahead even though some game files differ from Steam's (mods)." },
            }),
            &[],
        ),
        tool("list_maps", "Maps the installed game can load.", json!({}), &[]),
        tool("server_info", "A server's full settings, state, players and network details, plus where its logs and game files are.", json!({ "server": server }), &["server"]),
        tool(
            "create_server",
            "Create a server profile. Defaults to LAN networking; for development pass settings {\"cheats\": true, \"hibernateWhenEmpty\": false}.",
            json!({ "name": { "type": "string" }, "settings": settings }),
            &["name"],
        ),
        tool("update_server", "Change a server's settings. Most take effect on the next start.", json!({ "server": server, "settings": settings }), &["server", "settings"]),
        tool("delete_server", "Delete a stopped server and its files.", json!({ "server": server }), &["server"]),
        tool(
            "start_server",
            "Start a server and wait until it is up (or has failed, returning the console of the attempt).",
            json!({ "server": server, "wait": { "type": "boolean", "default": true }, "timeout_ms": timeout }),
            &["server"],
        ),
        tool(
            "crash_report",
            "Why a server's last run ended: the exit code decoded, a .NET fatal error with the frames from plugin code, Deadworks errors, known engine exits, and the module+offset of any minidump it wrote.",
            json!({ "server": server }),
            &["server"],
        ),
        tool("stop_server", "Stop a server cleanly and wait for it to exit.", json!({ "server": server, "timeout_ms": timeout }), &["server"]),
        tool("restart_server", "Restart a server and wait until it is back up.", json!({ "server": server, "timeout_ms": timeout }), &["server"]),
        tool(
            "run_command",
            "Run a console command on a running server and return the output it printed.",
            json!({
                "server": server,
                "command": { "type": "string" },
                "settle_ms": { "type": "integer", "description": "Output is collected until none arrives for this long (default 500)." },
            }),
            &["server", "command"],
        ),
        tool(
            "console_read",
            "Read a server's console scrollback, optionally filtered by a regex.",
            json!({
                "server": server,
                "after": after,
                "limit": { "type": "integer", "description": "Most recent lines to return (default 100, max 400)." },
                "grep": { "type": "string", "description": "Regex a line must match." },
            }),
            &["server"],
        ),
        tool(
            "console_wait",
            "Wait for a console line matching a regex. Without `after`, only lines printed from now on count.",
            json!({ "server": server, "pattern": { "type": "string" }, "after": after, "timeout_ms": timeout }),
            &["server", "pattern"],
        ),
        tool(
            "deploy_plugin",
            "Install a built plugin (.dll, zip or folder) into the plugin library and enable it on the server. \
A running server hot-reloads it; the result says whether Deadworks loaded it.",
            json!({ "path": { "type": "string", "description": "Absolute path to the built plugin." }, "server": server, "timeout_ms": timeout }),
            &["path"],
        ),
        tool(
            "create_plugin_project",
            "Scaffold a Deadworks plugin project (csproj referencing the API of the Deadworks release the launcher hosts, a plugin class \
and a [GameTest] file) in a new folder.",
            json!({
                "directory": { "type": "string", "description": "Absolute path of the folder to create; must not exist or be empty." },
                "name": { "type": "string", "description": "Project and plugin class name, e.g. KillStreaks." },
            }),
            &["directory", "name"],
        ),
        tool(
            "search_api",
            "Search the documentation of the Deadworks API (and of DeadworksManaged.Game, the generated schema classes, where the hosted \
release has it) for the hosted version: types, methods, properties and fields whose name or summary contains every word of the query.",
            json!({
                "query": { "type": "string", "description": "Words, e.g. \"modifier remove\" or \"m_iHealth\"." },
                "kind": { "type": "string", "enum": ["type", "method", "property", "field", "event"] },
                "assembly": { "type": "string", "enum": ["api", "game"] },
                "limit": { "type": "integer", "default": 25 },
            }),
            &["query"],
        ),
        tool("sdk_info","Where the Deadworks API assemblies of the hosted release are, for project references.", json!({}), &[]),
        tool(
            "install_content",
            "Install a map or addon VPK (for example one the modkit built) on a server and switch it on; with set_map, the map becomes the server's map. Takes effect on the next start.",
            json!({
                "server": server,
                "path": { "type": "string" },
                "kind": { "type": "string", "enum": ["map", "addon"], "default": "map" },
                "set_map": { "type": "boolean" },
            }),
            &["server", "path"],
        ),
        tool("list_plugins", "Plugins in the launcher's plugin library.", json!({}), &[]),
        tool(
            "remove_plugin",
            "Delete a plugin from the launcher's library, disabling it on every server first.",
            json!({ "plugin": { "type": "string" } }),
            &["plugin"],
        ),
        tool(
            "set_plugin_enabled",
            "Enable or disable a library plugin on a server; a running server loads or unloads it at once.",
            json!({ "server": server, "plugin": { "type": "string" }, "enabled": { "type": "boolean" } }),
            &["server", "plugin", "enabled"],
        ),
        tool(
            "plugin_config",
            "Read a plugin's config file on a server, or replace it when `text` is given.",
            json!({ "server": server, "plugin": { "type": "string" }, "text": { "type": "string" } }),
            &["server", "plugin"],
        ),
        tool(
            "bridge_enable",
            "Load the DevBridge plugin on a running server: it lets the game_*, entity, eval_csharp and trace tools look inside the game. Development servers only: it runs any code it is sent.",
            json!({ "server": server, "path": { "type": "string", "description": "A DevBridge build to use instead of the agent kit's." } }),
            &["server"],
        ),
        tool("game_status", "Map, tick, game time and the players with their heroes, health and positions.", json!({ "server": server }), &["server"]),
        tool(
            "find_entities",
            "Find entities by class (designer name or class name regex), targetname regex, team, or distance from a point.",
            json!({
                "server": server,
                "class": { "type": "string" },
                "name": { "type": "string" },
                "team": { "type": "integer" },
                "near": { "type": "array", "items": { "type": "number" }, "description": "[x, y, z]; results are sorted by distance." },
                "radius": { "type": "number" },
                "limit": { "type": "integer" },
            }),
            &["server"],
        ),
        tool(
            "inspect_entity",
            "An entity's fields. On Deadworks with the generated game assembly these are all schema fields of its most derived class (m_ names); otherwise the curated API's properties. Narrow with `fields` (regex on names) or read one dotted `path`; `depth` opens nested objects.",
            json!({
                "server": server,
                "index": { "type": "integer" },
                "fields": { "type": "string" },
                "path": { "type": "string" },
                "depth": { "type": "integer", "default": 1 },
                "view": { "type": "string", "enum": ["schema", "api"] },
            }),
            &["server", "index"],
        ),
        tool(
            "set_entity_field",
            "Write one field of an entity by dotted path (as inspect_entity names it). A raw write: some fields need the game's own setters (never write a pawn's level).",
            json!({ "server": server, "index": { "type": "integer" }, "path": { "type": "string" }, "value": {}, "view": { "type": "string", "enum": ["schema", "api"] } }),
            &["server", "index", "path", "value"],
        ),
        tool(
            "eval_csharp",
            "Compile and run C# on the game thread with the Deadworks API in scope (and DeadworksManaged.Game when present). A lone expression is the result; statements use `return`. Print(x) collects output, Ent(index) gets an entity. Runs synchronously within one tick.",
            json!({ "server": server, "code": { "type": "string" }, "depth": { "type": "integer", "default": 2 }, "timeout_ms": timeout }),
            &["server", "code"],
        ),
        tool(
            "trace_start",
            "Start recording server events: kinds from damage, add_modifier, modifier_event, ability_attempt, chat, entity_created, entity_spawned, entity_deleted, game_state, client, hero_initialized; plus game events by name (with the keys to read from them).",
            json!({
                "server": server,
                "kinds": { "type": "array", "items": { "type": "string" } },
                "game_events": { "type": "array", "items": { "type": "string" } },
                "game_event_keys": { "type": "array", "items": { "type": "string" } },
            }),
            &["server"],
        ),
        tool(
            "run_tests",
            "Run the [GameTest] methods of the loaded plugins on the game thread, one at a time, and report each result. A test fails by throwing; one returning IEnumerable yields ticks (int), a TimeSpan or a Func<bool> to wait between steps.",
            json!({ "server": server, "filter": { "type": "string", "description": "Regex on Namespace.Class.Method." }, "list_only": { "type": "boolean" }, "timeout_ms": timeout }),
            &["server"],
        ),
        tool(
            "demo_setup",
            "Turn SourceTV on for a server so it can record demos (a restart applies it). Demos show what players actually receive.",
            json!({ "server": server }),
            &["server"],
        ),
        tool("demo_start", "Start recording a SourceTV demo on a running server set up with demo_setup.", json!({ "server": server, "name": { "type": "string" } }), &["server"]),
        tool("demo_stop", "Stop the recording and return the demo's path.", json!({ "server": server }), &["server"]),
        tool(
            "inspect_demo",
            "Decode a demo the way a game client does and print entities' networked fields: changes per tick, or a snapshot. Proves what \
reached players; a server-side write that never shows up here was not networked. Field names are the network's \
(CBodyComponent.m_cellX, not m_vecAbsOrigin); list classes first with classes: true.",
            json!({
                "path": { "type": "string" },
                "classes": { "type": "boolean", "description": "List the entity classes in the demo." },
                "class": { "type": "string", "description": "Regex on the network class name, e.g. CNPC_Boss_Tier3 or CCitadelPlayerPawn." },
                "entity": { "type": "integer" },
                "field": { "type": "string", "description": "Regex on field names." },
                "from": { "type": "integer" },
                "to": { "type": "integer" },
                "snapshot": { "type": "boolean", "description": "All matching fields at `to` (or the end) instead of changes." },
                "limit": { "type": "integer", "default": 400 },
            }),
            &["path"],
        ),
        tool("trace_read", "Read recorded events after a sequence number.", json!({ "server": server, "after": after, "limit": { "type": "integer" } }), &["server"]),
        tool("trace_stop", "Stop recording events.", json!({ "server": server }), &["server"]),
    ])
}

fn call_tool(client: &Client, name: &str, a: &Value) -> Result<String, String> {
    let with = |extra: Value| {
        let mut m = a.as_object().cloned().unwrap_or_default();
        if let Some(s) = m.remove("server") {
            m.insert("id".into(), s);
        }
        for (k, v) in extra.as_object().cloned().unwrap_or_default() {
            m.insert(k, v);
        }
        Value::Object(m)
    };
    match name {
        "hosting_status" => Ok(format_overview(&client.call("overview", json!({}))?)),
        "hosting_setup" => {
            let check = client.call("hosting.setup_check", json!({}))?;
            if check["clientGameDir"].is_null() {
                return Err("Deadlock isn't installed through Steam on this PC; install it first.".into());
            }
            if check["clientUpdating"].as_bool() == Some(true) {
                return Err("Steam is updating Deadlock; wait for it to finish.".into());
            }
            let root = a["folder"].as_str().map(String::from).unwrap_or_else(|| check["suggestedRoot"].as_str().unwrap_or_default().to_string());
            // Hardlinks need no space and no copy, but only on Deadlock's own drive.
            let drive_of = |p: &str| p.chars().take_while(|c| *c != '\\' && *c != '/').collect::<String>().to_uppercase();
            let same_drive = check["drives"]
                .as_array()
                .into_iter()
                .flatten()
                .any(|d| d["sameAsClient"].as_bool() == Some(true) && drive_of(d["root"].as_str().unwrap_or_default()) == drive_of(&root));
            let copy_mode = if same_drive { "hardlink" } else { "copy" };
            client.call(
                "hosting.install",
                json!({
                    "root": root,
                    "source": "client",
                    "copyMode": copy_mode,
                    "allowModified": a["allow_modified"].as_bool().unwrap_or(false),
                }),
            )?;
            Ok(format!(
                "Installing hosting into {root} ({}) from {}. Call hosting_status for progress.",
                if same_drive { "hardlinked" } else { "copied" },
                check["clientGameDir"].as_str().unwrap_or_default()
            ))
        }
        "list_maps" => Ok(client.call("maps", json!({}))?.as_array().map(|m| join(m, "\n")).unwrap_or_default()),
        "server_info" => {
            let mut info = client.call("server.get", with(json!({})))?;
            if let Ok(paths) = client.call("server.paths", with(json!({}))) {
                info["paths"] = paths;
            }
            Ok(pretty(&info))
        }
        "create_server" => Ok(pretty(&client.call("server.create", a.clone())?)),
        "update_server" => Ok(pretty(&client.call("server.update", with(json!({})))?)),
        "delete_server" => client.call("server.delete", with(json!({}))).map(|_| "Deleted.".into()),
        "start_server" | "restart_server" => {
            let method = if name == "start_server" { "server.start" } else { "server.restart" };
            let r = client.call(method, with(json!({})))?;
            Ok(format_start(&r))
        }
        "crash_report" => Ok(pretty(&client.call("server.crash_report", with(json!({})))?)),
        "stop_server" => {
            let r = client.call("server.stop", with(json!({})))?;
            Ok(format!("State: {}", r["runtime"]["state"].as_str().unwrap_or("?")))
        }
        "run_command" => {
            let r = client.call("console.command", with(json!({})))?;
            Ok(format_lines(&r["lines"], 0, &r["next"]))
        }
        "console_read" => {
            let limit = a.get("limit").and_then(Value::as_u64).unwrap_or(100);
            let r = client.call("console.read", with(json!({ "limit": limit })))?;
            Ok(format_lines(&r["lines"], r["skipped"].as_u64().unwrap_or(0), &r["next"]))
        }
        "console_wait" => {
            let r = client.call("console.wait", with(json!({})))?;
            let head = match &r["matched"] {
                Value::Null => format!("No match ({}).", r["reason"].as_str().unwrap_or("timeout")),
                m => format!("Matched: {}", m["text"].as_str().unwrap_or_default()),
            };
            Ok(format!("{head}\n\n{}", format_lines(&r["lines"], r["skipped"].as_u64().unwrap_or(0), &r["next"])))
        }
        "deploy_plugin" => {
            let r = client.call("plugin.deploy", with(json!({})))?;
            Ok(format_deploy(&r))
        }
        "sdk_info" => Ok(pretty(&client.call("sdk.info", json!({}))?)),
        "search_api" => {
            let sdk = client.call("sdk.info", json!({}))?;
            let dir = PathBuf::from(sdk["dir"].as_str().ok_or("The launcher didn't report its SDK folder.")?);
            let docs = client.docs(&dir)?;
            Ok(docs::search(&docs, a))
        }
        "create_plugin_project" => {
            let sdk = client.call("sdk.info", json!({}))?;
            let dir = PathBuf::from(a["directory"].as_str().ok_or("directory is required.")?);
            let name = a["name"].as_str().ok_or("name is required.")?;
            scaffold(&dir, name, &sdk)
        }
        "install_content" => {
            let r = client.call("content.install", with(json!({})))?;
            let names: Vec<&str> = r["installed"].as_array().into_iter().flatten().filter_map(|f| f["fileName"].as_str()).collect();
            Ok(format!(
                "Installed {} on the server; map is {}.{}",
                names.join(", "),
                r["config"]["map"].as_str().unwrap_or("?"),
                if r["restartNeeded"].as_bool() == Some(true) { " restart_server to load it." } else { " It loads on the next start." }
            ))
        }
        "list_plugins" => Ok(pretty(&client.call("plugin.list", json!({}))?)),
        "remove_plugin" => client.call("plugin.remove", a.clone()).map(|_| "Removed.".into()),
        "set_plugin_enabled" => {
            let r = client.call("plugin.enable", with(json!({})))?;
            Ok(format!("Enabled plugins: {}", join(r["plugins"].as_array().unwrap_or(&vec![]), ", ")))
        }
        "plugin_config" => match a.get("text") {
            Some(_) => client.call("plugin.config.write", with(json!({}))).map(|_| "Saved.".into()),
            None => Ok(client.call("plugin.config.read", with(json!({})))?.as_str().unwrap_or_default().to_string()),
        },
        "bridge_enable" => {
            let path = match a["path"].as_str() {
                Some(p) => PathBuf::from(p),
                None => kit_dir()?.join("DevBridge"),
            };
            if !path.exists() {
                return Err(format!("No DevBridge at {}. Is the agent kit installed?", path.display()));
            }
            let r = client.call("bridge.enable", with(json!({ "path": path })))?;
            Ok(format_deploy(&r))
        }
        "game_status" => {
            let status = bridge(client, a, "status", json!({}))?;
            let players = bridge(client, a, "players", json!({}))?;
            Ok(pretty(&json!({ "status": status, "players": players })))
        }
        "find_entities" => bridge(client, a, "find", args_without_server(a)).map(|r| pretty(&r)),
        "inspect_entity" => bridge(client, a, "inspect", args_without_server(a)).map(|r| pretty(&r)),
        "set_entity_field" => bridge(client, a, "set", args_without_server(a)).map(|r| pretty(&r)),
        "eval_csharp" => {
            let r = bridge(client, a, "eval", args_without_server(a))?;
            let mut out = String::new();
            for line in r["printed"].as_array().into_iter().flatten() {
                out.push_str(line.as_str().unwrap_or_default());
                out.push('\n');
            }
            match r.get("error").and_then(Value::as_str) {
                Some(e) => Err(format!("{out}The snippet threw:\n{e}")),
                None => Ok(format!("{out}=> {}", pretty(&r["value"]))),
            }
        }
        "run_tests" => {
            if a.get("list_only").and_then(Value::as_bool) == Some(true) {
                let r = bridge(client, a, "tests.list", args_without_server(a))?;
                return Ok(match r.as_array() {
                    Some(t) if !t.is_empty() => join(t, "\n"),
                    _ => "No [GameTest] methods in the loaded plugins.".into(),
                });
            }
            let mut a = a.clone();
            if a.get("timeout_ms").is_none() {
                a["timeout_ms"] = json!(300_000);
            }
            let r = bridge(client, &a, "tests.run", args_without_server(&a))?;
            Ok(format_tests(&r))
        }
        "demo_setup" => {
            let info = client.call("server.get", with(json!({})))?;
            let port = info["config"]["port"].as_u64().unwrap_or(27015);
            // Drop earlier SourceTV arguments with their values, then add ours.
            let mut args: Vec<String> = Vec::new();
            let mut skip = false;
            for a in info["config"]["launchArgs"].as_array().into_iter().flatten().filter_map(Value::as_str) {
                if skip {
                    skip = false;
                    continue;
                }
                if a.starts_with("+tv_") {
                    skip = true;
                    continue;
                }
                args.push(a.to_string());
            }
            // SourceTV's own port defaults to 27020, which is a game port of the launcher's servers.
            let tv_port = (port + 1000).to_string();
            args.extend(["+tv_enable", "1", "+tv_delay", "0", "+tv_port", tv_port.as_str()].map(String::from));
            client.call("server.update", with(json!({ "settings": { "launchArgs": args } })))?;
            Ok(format!("SourceTV is on (port {tv_port}) from the next start; restart_server if it's running."))
        }
        "demo_start" => {
            let name = a["name"].as_str().map(String::from).unwrap_or_else(|| format!("agent-{}", std::process::id()));
            let r = client.call("console.command", with(json!({ "command": format!("tv_record \"{name}\"") })))?;
            let text = format_lines(&r["lines"], 0, &r["next"]);
            if text.contains("Recording SourceTV") {
                Ok(format!("Recording {name}.dem. Act, then demo_stop."))
            } else {
                Err(format!("SourceTV didn't start recording. Is it on (demo_setup, then a restart)?\n{text}"))
            }
        }
        "demo_stop" => {
            let r = client.call("console.command", with(json!({ "command": "tv_stoprecord" })))?;
            let text = format_lines(&r["lines"], 0, &r["next"]);
            let file = Regex::new(r#"Completed SourceTV demo "([^"]+)""#).unwrap().captures(&text).map(|c| c[1].to_string());
            let Some(file) = file else { return Err(format!("No recording was running.\n{text}")) };
            let game_bin = client.call("server.paths", with(json!({})))?["gameBin"].as_str().unwrap_or_default().to_string();
            // game/bin/win64 -> game/citadel, where the engine writes demos.
            let game = PathBuf::from(game_bin).parent().and_then(|p| p.parent()).map(|p| p.to_path_buf()).unwrap_or_default();
            Ok(format!("Saved {}", game.join("citadel").join(&file).display()))
        }
        "inspect_demo" => inspect_demo(a),
        "trace_start" => bridge(client, a, "trace.start", args_without_server(a)).map(|r| pretty(&r)),
        "trace_read" => bridge(client, a, "trace.read", args_without_server(a)).map(|r| pretty(&r)),
        "trace_stop" => bridge(client, a, "trace.stop", json!({})).map(|_| "Stopped.".into()),
        _ => Err(format!("Unknown tool {name}")),
    }
}

/// One DevBridge op on the server named in the tool arguments.
fn bridge(client: &Client, a: &Value, op: &str, args: Value) -> Result<Value, String> {
    let mut params = json!({ "id": a.get("server").cloned().unwrap_or(Value::Null), "op": op, "args": args });
    if let Some(t) = a.get("timeout_ms") {
        params["timeout_ms"] = t.clone();
    }
    client.call("bridge.call", params)
}

fn args_without_server(a: &Value) -> Value {
    let mut m = a.as_object().cloned().unwrap_or_default();
    m.remove("server");
    m.remove("timeout_ms");
    Value::Object(m)
}

// ── Demos ──

/// The agent kit: DevBridge, demo-inspect and the rest the launcher doesn't ship. At
/// DEADWORKS_AGENT_KIT, or `Deadworksgent-kit` in the local app data folder.
fn kit_dir() -> Result<PathBuf, String> {
    if let Some(dir) = std::env::var_os("DEADWORKS_AGENT_KIT") {
        return Ok(PathBuf::from(dir));
    }
    let base = if cfg!(windows) {
        std::env::var_os("LOCALAPPDATA").map(PathBuf::from)
    } else {
        std::env::var_os("XDG_DATA_HOME")
            .map(PathBuf::from)
            .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".local/share")))
    };
    base.map(|b| b.join("Deadworks").join("agent-kit")).ok_or_else(|| "Couldn't tell where the agent kit is.".into())
}

/// Run the agent kit's demo-inspect over a demo.
fn inspect_demo(a: &Value) -> Result<String, String> {
    let exe = kit_dir()?.join(if cfg!(windows) { "demo-inspect.exe" } else { "demo-inspect" });
    if !exe.is_file() {
        return Err(format!("No demo-inspect at {}. Is the agent kit installed?", exe.display()));
    }
    let mut cmd = std::process::Command::new(&exe);
    cmd.arg(a["path"].as_str().ok_or("path is required.")?);
    if a["classes"].as_bool() == Some(true) {
        cmd.arg("--classes");
    }
    if a["snapshot"].as_bool() == Some(true) {
        cmd.arg("--snapshot");
    }
    for (key, flag) in [("class", "--class"), ("field", "--field")] {
        if let Some(v) = a[key].as_str() {
            cmd.args([flag, v]);
        }
    }
    for (key, flag) in [("entity", "--entity"), ("from", "--from"), ("to", "--to")] {
        if let Some(v) = a[key].as_i64() {
            cmd.args([flag, &v.to_string()]);
        }
    }
    cmd.args(["--limit", &a["limit"].as_u64().unwrap_or(400).to_string()]);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    let out = cmd.stdin(std::process::Stdio::null()).output().map_err(|e| format!("Couldn't run demo-inspect: {e}"))?;
    let stdout = String::from_utf8_lossy(&out.stdout);
    if !out.status.success() {
        return Err(format!("{}{}", stdout, String::from_utf8_lossy(&out.stderr)));
    }
    Ok(stdout.into_owned())
}

// ── Project scaffold ──

fn scaffold(dir: &std::path::Path, name: &str, sdk: &Value) -> Result<String, String> {
    let valid = name.chars().next().is_some_and(|c| c.is_ascii_alphabetic()) && name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_');
    if !valid {
        return Err("name must be a C# identifier: letters, digits and underscores, starting with a letter.".into());
    }
    if dir.read_dir().is_ok_and(|mut d| d.next().is_some()) {
        return Err(format!("{} isn't empty.", dir.display()));
    }
    let sdk_dir = sdk["dir"].as_str().ok_or("The launcher didn't report its SDK folder.")?;
    let files: Vec<&str> = sdk["files"].as_array().into_iter().flatten().filter_map(Value::as_str).collect();
    let reference = |assembly: &str| {
        format!(
            "    <Reference Include=\"{assembly}\">\n      <HintPath>$(DeadworksSdk)\\{assembly}.dll</HintPath>\n      <Private>false</Private>\n    </Reference>\n"
        )
    };
    let mut references = reference("DeadworksManaged.Api");
    if files.contains(&"DeadworksManaged.Game.dll") {
        references.push_str(&reference("DeadworksManaged.Game"));
    }
    references.push_str(&reference("Google.Protobuf"));
    let csproj = format!(
        r#"<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <!-- The Deadworks launcher keeps this folder in step with the Deadworks release it hosts. -->
    <DeadworksSdk>{sdk_dir}</DeadworksSdk>
  </PropertyGroup>

  <ItemGroup>
{references}  </ItemGroup>

</Project>
"#
    );
    let plugin = format!(
        r#"using DeadworksManaged.Api;

namespace {name};

public class {name}Plugin : DeadworksPluginBase
{{
    public override string Name => "{name}";

    public override void OnLoad(bool isReload)
    {{
        Console.WriteLine($"[{name}] {{(isReload ? "Reloaded" : "Loaded")}}");
    }}
}}
"#
    );
    let tests = format!(
        r#"using DeadworksManaged.Api;

namespace {name};

/// <summary>
/// Run by the DevBridge plugin (the Deadworks MCP server's run_tests tool) on a development server.
/// A test fails by throwing. One that returns IEnumerable runs across ticks: yield an int to wait
/// that many ticks, a TimeSpan to wait that long, or a Func&lt;bool&gt; to wait until it is true.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class GameTestAttribute : Attribute
{{
    public int TimeoutSeconds {{ get; init; }} = 30;
}}

internal static class {name}Tests
{{
    [GameTest]
    public static IEnumerable<object> Server_is_ticking()
    {{
        int start = GlobalVars.TickCount;
        yield return 2;
        if (GlobalVars.TickCount - start < 2)
            throw new Exception("the server isn't ticking");
    }}
}}
"#
    );
    std::fs::create_dir_all(dir).map_err(|e| format!("Couldn't create {}: {e}", dir.display()))?;
    for (file, text) in [(format!("{name}.csproj"), csproj), (format!("{name}Plugin.cs"), plugin), ("GameTests.cs".to_string(), tests)] {
        std::fs::write(dir.join(&file), text).map_err(|e| format!("Couldn't write {file}: {e}"))?;
    }
    Ok(format!(
        "Created {name} in {} against Deadworks {}.\nBuild: dotnet build \"{}\"\nThen deploy_plugin with path {} and a server.",
        dir.display(),
        sdk["deadworks"].as_str().unwrap_or("?"),
        dir.join(format!("{name}.csproj")).display(),
        dir.join("bin").join("Debug").join("net10.0").join(format!("{name}.dll")).display(),
    ))
}

// ── Output ──

fn pretty(v: &Value) -> String {
    serde_json::to_string_pretty(v).unwrap_or_default()
}

fn join(items: &[Value], sep: &str) -> String {
    items.iter().map(|v| v.as_str().map(String::from).unwrap_or_else(|| v.to_string())).collect::<Vec<_>>().join(sep)
}

/// One line per console line: input as `> `, launcher messages as `# `.
fn format_lines(lines: &Value, skipped: u64, next: &Value) -> String {
    let mut out = String::new();
    if skipped > 0 {
        out.push_str(&format!("({skipped} earlier lines not shown)\n"));
    }
    for l in lines.as_array().into_iter().flatten() {
        let prefix = match l["kind"].as_str() {
            Some("in") => "> ",
            Some("sys") => "# ",
            _ => "",
        };
        out.push_str(&format!("[{}] {prefix}{}\n", l["seq"], l["text"].as_str().unwrap_or_default()));
    }
    if out.is_empty() {
        out.push_str("(no output)\n");
    }
    out.push_str(&format!("next: {next}"));
    out
}

fn format_overview(o: &Value) -> String {
    let mut out = String::new();
    if o["installed"].as_bool() != Some(true) {
        out.push_str("Hosting isn't installed yet: run hosting_setup (once).\n");
    }
    let u = &o["updates"];
    out.push_str(&format!(
        "Game build {}, Deadworks {}{}\n",
        u["installedBuildId"].as_str().unwrap_or("?"),
        u["deadworksInstalled"].as_str().unwrap_or("?"),
        if u["pending"].as_bool() == Some(true) { ", update pending" } else { "" },
    ));
    if let Some(hold) = u["holdReason"].as_str() {
        out.push_str(&format!("On hold: {hold}\n"));
    }
    if let Some(task) = o["task"].as_object() {
        let finished = task.get("finished").and_then(Value::as_bool) == Some(true);
        let gb = |k: &str| task.get(k).and_then(Value::as_u64).unwrap_or(0) as f64 / 1e9;
        match task.get("error").and_then(Value::as_str) {
            Some(e) => out.push_str(&format!("Last {} failed: {e}\n", task["kind"].as_str().unwrap_or("task"))),
            None if finished => out.push_str(&format!("Last {} finished.\n", task["kind"].as_str().unwrap_or("task"))),
            None => out.push_str(&format!(
                "{} running: {} ({:.1} of {:.1} GB, {} of {} files)\n",
                task["kind"].as_str().unwrap_or("task"),
                task["label"].as_str().unwrap_or_default(),
                gb("bytesDone"),
                gb("bytesTotal"),
                task.get("filesDone").and_then(Value::as_u64).unwrap_or(0),
                task.get("filesTotal").and_then(Value::as_u64).unwrap_or(0),
            )),
        }
        if let Some(files) = task.get("modifiedFiles").and_then(Value::as_array).filter(|f| !f.is_empty()) {
            out.push_str(&format!("{} game files differ from Steam's (mods?); hosting_setup with allow_modified uses them anyway.\n", files.len()));
        }
    }
    let servers = o["servers"].as_array().cloned().unwrap_or_default();
    if servers.is_empty() {
        out.push_str("No servers.");
    }
    for s in servers {
        let (c, r) = (&s["config"], &s["runtime"]);
        out.push_str(&format!(
            "- {} \"{}\": {}, {}, port {}, cheats {}, plugins [{}]{}\n",
            c["id"].as_str().unwrap_or_default(),
            c["name"].as_str().unwrap_or_default(),
            r["state"].as_str().unwrap_or_default(),
            c["map"].as_str().unwrap_or_default(),
            r["network"]["port"],
            c["cheats"],
            join(c["plugins"].as_array().unwrap_or(&vec![]), ", "),
            r["message"].as_str().map(|m| format!(", {m}")).unwrap_or_default(),
        ));
    }
    out
}

fn format_start(r: &Value) -> String {
    if r["alreadyRunning"].as_bool() == Some(true) {
        return "Already running.".into();
    }
    let rt = &r["server"]["runtime"];
    let mut out = format!("State: {}", rt["state"].as_str().unwrap_or("?"));
    if let Some(m) = rt["message"].as_str() {
        out.push_str(&format!(" ({m})"));
    }
    if let Some(c) = rt["network"]["connectCommand"].as_str() {
        out.push_str(&format!("\nJoin from the game console: {c}"));
    }
    if r["timedOut"].as_bool() == Some(true) {
        out.push_str("\nNot up yet after the wait; it may still come up (start_server again waits more).");
    }
    if r["up"].as_bool() == Some(false) {
        out.push_str("\n\nConsole of the attempt:\n");
        out.push_str(&format_lines(&r["console"], 0, &Value::Null));
    }
    out
}

fn format_deploy(r: &Value) -> String {
    let ids = |list: &Value| -> String {
        list.as_array().into_iter().flatten().filter_map(|p| p["id"].as_str()).collect::<Vec<_>>().join(", ")
    };
    // Without a server it is only a library import.
    if r.is_array() {
        return format!("Imported into the library: {}", ids(r));
    }
    let mut out = format!("Plugins: {}\n", ids(&r["plugins"]));
    match (r["loaded"].as_bool(), r["note"].as_str()) {
        (_, Some(note)) => out.push_str(note),
        (Some(true), _) => out.push_str("Loaded."),
        _ => out.push_str(&format!("Not loaded ({}).", r["reason"].as_str().unwrap_or("failed"))),
    }
    let loader = r["loaderLines"].as_array().cloned().unwrap_or_default();
    if !loader.is_empty() {
        out.push_str("\n\n");
        out.push_str(&format_lines(&Value::Array(loader), 0, &r["next"]));
    }
    out
}

fn format_tests(r: &Value) -> String {
    let tests = r["tests"].as_array().cloned().unwrap_or_default();
    if tests.is_empty() {
        return "No [GameTest] methods matched in the loaded plugins.".into();
    }
    let mut out = format!("{} passed, {} failed\n", r["passed"], r["failed"]);
    for t in &tests {
        let passed = t["passed"].as_bool() == Some(true);
        out.push_str(&format!("{} {} ({} ticks, {} ms)\n", if passed { "PASS" } else { "FAIL" }, t["name"].as_str().unwrap_or_default(), t["ticks"], t["ms"]));
        if let Some(e) = t["error"].as_str() {
            out.push_str(&format!("    {}\n", e.replace('\n', "\n    ")));
        }
    }
    out
}

// ── Control API client ──

#[derive(Default)]
struct Client {
    conn: Mutex<Option<Conn>>,
    /// Parsed documentation, and the modification time of the API's file when it was read.
    docs: Mutex<Option<(PathBuf, Option<std::time::SystemTime>, Arc<Vec<docs::Member>>)>>,
}

#[derive(Clone)]
struct Conn {
    url: String,
    auth: String,
}

impl Client {
    fn call(&self, method: &str, params: Value) -> Result<Value, String> {
        let conn = self.connect()?;
        let reply = match post(&conn, method, &params) {
            Ok(r) => r,
            // The launcher restarted since: find the new one once.
            Err(_) => {
                *self.conn.lock().unwrap() = None;
                post(&self.connect()?, method, &params)?
            }
        };
        match reply.get("error").and_then(Value::as_str) {
            Some(e) => Err(e.to_string()),
            None => Ok(reply.get("result").cloned().unwrap_or(Value::Null)),
        }
    }

    /// The SDK folder's documentation, parsed once per release.
    fn docs(&self, dir: &std::path::Path) -> Result<Arc<Vec<docs::Member>>, String> {
        let stamp = std::fs::metadata(dir.join("DeadworksManaged.Api.xml")).and_then(|m| m.modified()).ok();
        let mut cache = self.docs.lock().unwrap_or_else(|e| e.into_inner());
        if let Some((d, s, docs)) = cache.as_ref() {
            if d == dir && *s == stamp {
                return Ok(docs.clone());
            }
        }
        let docs = Arc::new(docs::load(dir));
        *cache = Some((dir.to_path_buf(), stamp, docs.clone()));
        Ok(docs)
    }

    fn connect(&self) -> Result<Conn, String> {
        let mut guard = self.conn.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(c) = guard.as_ref() {
            return Ok(c.clone());
        }
        let conn = match discover() {
            Some(c) => c,
            None => launch()?,
        };
        *guard = Some(conn.clone());
        Ok(conn)
    }
}

fn post(conn: &Conn, method: &str, params: &Value) -> Result<Value, String> {
    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(660))
        .build()
        .map_err(|e| e.to_string())?;
    let resp = client
        .post(&conn.url)
        .header("Authorization", &conn.auth)
        .json(&json!({ "method": method, "params": params }))
        .send()
        .map_err(|e| format!("The launcher didn't answer: {e}"))?;
    resp.json::<Value>().map_err(|e| format!("Bad reply from the launcher: {e}"))
}

fn discovery_path() -> Option<PathBuf> {
    let base = if cfg!(windows) {
        std::env::var_os("APPDATA").map(PathBuf::from)
    } else if cfg!(target_os = "macos") {
        std::env::var_os("HOME").map(|h| PathBuf::from(h).join("Library/Application Support"))
    } else {
        std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".config")))
    };
    base.map(|b| b.join(IDENTIFIER).join("control.json"))
}

/// The running launcher's control API, if one answers.
fn discover() -> Option<Conn> {
    let text = std::fs::read_to_string(discovery_path()?).ok()?;
    let d: Value = serde_json::from_str(&text).ok()?;
    let conn = Conn {
        url: format!("http://127.0.0.1:{}/rpc", d["port"].as_u64()?),
        auth: format!("Bearer {}", d["token"].as_str()?),
    };
    let hello = post(&conn, "hello", &Value::Null).ok()?;
    (hello["result"]["proto"].as_u64() == Some(CONTROL_PROTO)).then_some(conn)
}

/// Start the launcher (this executable) in the background and wait for its control API.
fn launch() -> Result<Conn, String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let started = Instant::now();
    let previous = discovery_path().and_then(|p| std::fs::metadata(p).and_then(|m| m.modified()).ok());
    let mut child = spawn_detached(&exe).map_err(|e| format!("Couldn't start the launcher ({}): {e}", exe.display()))?;
    while started.elapsed() < LAUNCH_WAIT {
        std::thread::sleep(Duration::from_millis(500));
        // A launcher already running takes the new one's arguments and the new one exits at once:
        // that one was started without the agent tools, or it would have answered.
        if started.elapsed() < Duration::from_secs(10) && child.try_wait().ok().flatten().is_some() && discover().is_none() {
            break;
        }
        let fresh = discovery_path().and_then(|p| std::fs::metadata(p).and_then(|m| m.modified()).ok());
        if fresh.is_some() && fresh != previous {
            if let Some(c) = discover() {
                return Ok(c);
            }
        }
    }
    Err(concat!(
        "Couldn't reach the Deadworks launcher's agent tools. If the launcher is already running, it was started without them: ",
        "quit it from its tray icon and call a tool again, or set \"agent_tools\": true in ",
        "%APPDATA%\\net.deadworks.launcher\\settings.json while it is closed. Otherwise the launcher may be too old."
    )
    .into())
}

/// The launcher outlives this process, so it must not hold the agent's stdio pipes open (the
/// agent would wait on them forever) nor die with the agent's process tree.
#[cfg(windows)]
fn spawn_detached(exe: &std::path::Path) -> std::io::Result<std::process::Child> {
    use std::os::windows::process::CommandExt;
    use windows_sys::Win32::Foundation::{SetHandleInformation, HANDLE_FLAG_INHERIT};
    use windows_sys::Win32::System::Console::{GetStdHandle, STD_ERROR_HANDLE, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE};
    use windows_sys::Win32::System::Threading::{CREATE_BREAKAWAY_FROM_JOB, CREATE_NEW_PROCESS_GROUP, DETACHED_PROCESS};
    // Every inheritable handle goes to a child, whatever its stdio is set to.
    for std in [STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE] {
        unsafe { SetHandleInformation(GetStdHandle(std), HANDLE_FLAG_INHERIT, 0) };
    }
    let command = |flags: u32| {
        let mut c = std::process::Command::new(exe);
        c.arg("--background")
            .env("DEADWORKS_AGENT_TOOLS", "1")
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | flags);
        c
    };
    // Breaking away fails inside a job that forbids it; then the launcher lives as long as the job.
    command(CREATE_BREAKAWAY_FROM_JOB).spawn().or_else(|_| command(0).spawn())
}

#[cfg(not(windows))]
fn spawn_detached(exe: &std::path::Path) -> std::io::Result<std::process::Child> {
    use std::os::unix::process::CommandExt;
    std::process::Command::new(exe)
        .arg("--background")
        .env("DEADWORKS_AGENT_TOOLS", "1")
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .process_group(0)
        .spawn()
}
