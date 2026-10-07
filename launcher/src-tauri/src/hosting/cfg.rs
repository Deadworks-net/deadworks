//! What a server is started with: its generated cfg, its argv, and the
//! Deadworks config files the launcher owns.
//!
//! Settings go in a cfg rather than argv: `deadworks.exe` re-joins argv with
//! spaces, so a hostname with a space would split. Cheat mode is the exception:
//! `sv_cheats` set from a cfg is reverted on map load, but `+sv_cheats 1` sticks.

use serde_json::{json, Map, Value};

use super::types::{NetworkMode, ServerConfig};

pub const LAUNCHER_CFG: &str = "deadworks_launcher.cfg";
pub const MAX_PLAYERS: u32 = 31;

pub fn validate(c: &ServerConfig) -> Result<(), String> {
    let name = c.name.trim();
    if name.is_empty() {
        return Err("Give the server a name.".into());
    }
    if name.chars().count() > 64 {
        return Err("The server name can be at most 64 characters.".into());
    }
    if !cfg_safe(&c.name) {
        return Err("The server name can't contain quotes or line breaks.".into());
    }
    if !cfg_safe(&c.password) {
        return Err("The password can't contain quotes or line breaks.".into());
    }
    if !(1..=MAX_PLAYERS).contains(&c.max_players) {
        return Err(format!("Max players must be between 1 and {MAX_PLAYERS}."));
    }
    if c.port < 1024 {
        return Err("Pick a port between 1024 and 65535.".into());
    }
    if !is_token(&c.map) {
        return Err("Pick a map.".into());
    }
    for cv in &c.cvars {
        if !is_token(&cv.key) {
            return Err(format!("'{}' isn't a valid console variable name.", cv.key));
        }
        if !cfg_safe(&cv.value) {
            return Err(format!("The value for {} can't contain quotes or line breaks.", cv.key));
        }
    }
    for arg in &c.launch_args {
        if arg.is_empty() || arg.chars().any(|ch| ch.is_whitespace() || ch == '"') {
            return Err(format!("Launch argument '{arg}' can't contain spaces or quotes; put each word on its own line."));
        }
    }
    for name in c.content_addons.iter().chain(&c.extra_maps) {
        if !is_token(name.trim_end_matches(".vpk")) {
            return Err(format!("'{name}' isn't a valid content file name."));
        }
    }
    for id in &c.plugins {
        if !super::plugins::safe_id(id) {
            return Err(format!("'{id}' isn't a usable plugin name."));
        }
    }
    Ok(())
}

fn cfg_safe(s: &str) -> bool {
    !s.contains(['"', '\n', '\r'])
}

/// A console token: letters, digits, `_`, `-`, `.`.
fn is_token(s: &str) -> bool {
    !s.is_empty() && s.len() <= 128 && s.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '-' | '.'))
}

/// `rcon_password` is set where commands reach the server over RCON (Linux, under Wine).
pub fn launcher_cfg(c: &ServerConfig, rcon_password: Option<&str>) -> String {
    let mut out = String::from(
        "// Written by the Deadworks launcher every time the server starts; edits here are lost.\n\
         // Put your own settings in deadworks_user.cfg next to this file instead.\n",
    );
    out.push_str(&format!("hostname \"{}\"\n", c.name.trim()));
    out.push_str(&format!("sv_password \"{}\"\n", c.password));
    out.push_str(&format!("sv_hibernate_when_empty {}\n", u8::from(c.hibernate_when_empty)));
    if let Some(password) = rcon_password {
        out.push_str(&format!("rcon_password \"{password}\"\n"));
    }
    for cv in &c.cvars {
        out.push_str(&format!("{} \"{}\"\n", cv.key, cv.value));
    }
    out
}

/// `usercon` opens the RCON listener on the game port; only used where it is the console.
pub fn argv(c: &ServerConfig, usercon: bool) -> Vec<String> {
    let mut a: Vec<String> = [
        "-dedicated",
        "-console",
        "-insecure",
        "-allow_no_lobby_connect",
        "-maxplayers",
    ]
    .iter()
    .map(|s| s.to_string())
    .collect();
    a.push(c.max_players.to_string());
    if usercon {
        a.push("-usercon".into());
    }
    for (k, v) in [
        ("+hostport", c.port.to_string()),
        ("+tv_citadel_auto_record", "0".into()),
        ("+spec_replay_enable", "0".into()),
        ("+tv_enable", "0".into()),
        ("+citadel_upload_replay_enabled", "0".into()),
    ] {
        a.push(k.into());
        a.push(v);
    }
    if c.cheats {
        a.extend(["+sv_cheats".into(), "1".into()]);
    }
    a.extend(["+exec".into(), LAUNCHER_CFG.into()]);
    a.extend(["+exec".into(), "deadworks_user.cfg".into()]);
    a.extend(c.launch_args.iter().cloned());
    a.extend(["+map".into(), c.map.clone()]);
    a
}

/// `configs/deadworks.jsonc`: keep whatever else is in it, own the browser
/// listing and the advertised content.
///
/// A file that doesn't parse is an error, not an empty file: writing over it would throw away
/// whatever its owner had in it. Unchanged content is returned as it was, comments and all.
pub fn deadworks_jsonc(existing: Option<&str>, c: &ServerConfig) -> Result<String, String> {
    let before = parse_existing("deadworks.jsonc", existing)?;
    let mut root = Value::Object(before.clone().unwrap_or_default());
    let obj = root.as_object_mut().unwrap();
    let sb = obj.entry("serverbrowser").or_insert_with(|| json!({}));
    if !sb.is_object() {
        *sb = json!({});
    }
    let sb = sb.as_object_mut().unwrap();
    // Only a forwarded port is reachable at the address the browser would show.
    sb.insert("unlisted".into(), json!(!(c.listed && c.network == NetworkMode::PortForward)));
    sb.insert("content_addons".into(), json!(stems(&c.content_addons)));
    sb.insert("extra_maps".into(), json!(stems(&c.extra_maps)));
    Ok(render(existing, before, root))
}

/// The object in a config file that is already there. `None` when there is no file (or an
/// empty one).
fn parse_existing(file: &str, existing: Option<&str>) -> Result<Option<Map<String, Value>>, String> {
    let Some(text) = existing.filter(|t| !t.trim().is_empty()) else { return Ok(None) };
    match serde_json::from_str::<Value>(&strip_jsonc(text)) {
        Ok(Value::Object(map)) => Ok(Some(map)),
        Ok(_) => Err(format!("configs\\{file} should hold a {{ ... }} object. Fix or delete the file, then try again.")),
        Err(e) => Err(format!("configs\\{file} has a mistake in it ({e}). Fix or delete the file, then try again.")),
    }
}

fn render(existing: Option<&str>, before: Option<Map<String, Value>>, after: Value) -> String {
    match (existing, &after) {
        (Some(text), Value::Object(after)) if before.as_ref() == Some(after) => text.to_string(),
        _ => serde_json::to_string_pretty(&after).unwrap_or_default(),
    }
}

/// `configs/plugins.jsonc`: the enabled plugins switched on, everything else as found.
pub fn plugins_jsonc(existing: Option<&str>, enabled: &[String]) -> Result<String, String> {
    let before = parse_existing("plugins.jsonc", existing)?;
    let mut map = before.clone().unwrap_or_default();
    for id in enabled {
        map.insert(id.clone(), json!(true));
    }
    Ok(render(existing, before, Value::Object(map)))
}

fn stems(names: &[String]) -> Vec<String> {
    names.iter().map(|n| n.trim_end_matches(".vpk").to_string()).collect()
}

/// Remove `//` and `/* */` comments and trailing commas so serde_json can read JSONC.
pub fn strip_jsonc(text: &str) -> String {
    strip_trailing_commas(&strip_comments(text))
}

fn strip_comments(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();
    let mut in_str = false;
    while let Some(c) = chars.next() {
        if in_str {
            out.push(c);
            if c == '\\' {
                if let Some(escaped) = chars.next() {
                    out.push(escaped);
                }
            } else if c == '"' {
                in_str = false;
            }
            continue;
        }
        match (c, chars.peek().copied()) {
            ('"', _) => {
                in_str = true;
                out.push('"');
            }
            ('/', Some('/')) => {
                while chars.peek().is_some_and(|&n| n != '\n') {
                    chars.next();
                }
            }
            ('/', Some('*')) => {
                chars.next();
                let mut prev = ' ';
                for n in chars.by_ref() {
                    if prev == '*' && n == '/' {
                        break;
                    }
                    prev = n;
                }
                // So `1/**/2` doesn't become `12`.
                out.push(' ');
            }
            _ => out.push(c),
        }
    }
    out
}

fn strip_trailing_commas(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut chars = text.char_indices();
    let mut in_str = false;
    while let Some((i, c)) = chars.next() {
        if in_str {
            out.push(c);
            if c == '\\' {
                if let Some((_, escaped)) = chars.next() {
                    out.push(escaped);
                }
            } else if c == '"' {
                in_str = false;
            }
            continue;
        }
        match c {
            '"' => {
                in_str = true;
                out.push('"');
            }
            ',' => {
                let rest = text[i + 1..].trim_start();
                if !(rest.starts_with('}') || rest.starts_with(']')) {
                    out.push(',');
                }
            }
            _ => out.push(c),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hosting::types::CvarEntry;

    pub fn sample() -> ServerConfig {
        ServerConfig {
            id: "s1".into(),
            name: "My Server".into(),
            password: "".into(),
            max_players: 12,
            map: "dl_midtown".into(),
            port: 27020,
            network: NetworkMode::PortForward,
            listed: false,
            cheats: false,
            hibernate_when_empty: true,
            plugins: vec![],
            cvars: vec![CvarEntry { key: "citadel_trooper_spawn_enabled".into(), value: "0".into() }],
            launch_args: vec![],
            content_addons: vec![],
            extra_maps: vec![],
        }
    }

    #[test]
    fn names_with_spaces_go_in_the_cfg_not_argv() {
        let c = sample();
        let cfg = launcher_cfg(&c, None);
        assert!(cfg.contains("hostname \"My Server\"\n"));
        assert!(cfg.contains("citadel_trooper_spawn_enabled \"0\"\n"));
        let a = argv(&c, false);
        assert!(a.iter().all(|t| !t.contains(' ')));
        assert_eq!(a.last().unwrap(), "dl_midtown");
        assert!(a.windows(2).any(|w| w == ["-maxplayers", "12"]));
        assert!(a.windows(2).any(|w| w == ["+hostport", "27020"]));
        assert!(!a.contains(&"+sv_cheats".to_string()));
    }

    #[test]
    fn validation_rejects_what_would_break_the_cfg() {
        let mut c = sample();
        assert!(validate(&c).is_ok());
        c.name = "evil\"; quit".into();
        assert!(validate(&c).is_err());
        let mut c = sample();
        c.launch_args = vec!["+map dl_hideout".into()];
        assert!(validate(&c).is_err());
        let mut c = sample();
        c.max_players = 64;
        assert!(validate(&c).is_err());
        let mut c = sample();
        c.cvars[0].key = "bad key".into();
        assert!(validate(&c).is_err());
    }

    #[test]
    fn deadworks_jsonc_keeps_unknown_keys_and_lists_only_forwarded_servers() {
        let existing = "{\n  // comment\n  \"serverbrowser\": { \"api_url\": \"https://x\", \"unlisted\": false, },\n  \"other\": 1\n}";
        let mut c = sample();
        let out: Value = serde_json::from_str(&deadworks_jsonc(Some(existing), &c).unwrap()).unwrap();
        assert_eq!(out["serverbrowser"]["api_url"], "https://x");
        assert_eq!(out["other"], 1);
        assert_eq!(out["serverbrowser"]["unlisted"], true);
        c.listed = true;
        let out: Value = serde_json::from_str(&deadworks_jsonc(None, &c).unwrap()).unwrap();
        assert_eq!(out["serverbrowser"]["unlisted"], false);
        c.network = NetworkMode::Sdr;
        let out: Value = serde_json::from_str(&deadworks_jsonc(None, &c).unwrap()).unwrap();
        assert_eq!(out["serverbrowser"]["unlisted"], true, "an SDR server can't be joined from the browser");
    }

    #[test]
    fn plugins_jsonc_turns_enabled_on_and_keeps_the_rest() {
        let out: Value = serde_json::from_str(&plugins_jsonc(Some("{ \"Other\": false, }"), &["Mine".into()]).unwrap()).unwrap();
        assert_eq!(out["Mine"], true);
        assert_eq!(out["Other"], false);
    }

    #[test]
    fn a_config_file_that_does_not_parse_is_refused_not_replaced() {
        let c = sample();
        for broken in ["{ \"permissions\": { \"store\": \"json\" ", "[1, 2]", "{ \"a\": tru }"] {
            assert!(deadworks_jsonc(Some(broken), &c).is_err(), "{broken}");
            assert!(plugins_jsonc(Some(broken), &[]).is_err(), "{broken}");
        }
        // Missing and empty files are simply new.
        assert!(deadworks_jsonc(Some("  \n"), &c).is_ok());
        assert!(plugins_jsonc(None, &[]).is_ok());
    }

    #[test]
    fn a_config_file_that_needs_no_change_keeps_its_comments() {
        let c = sample();
        let written = deadworks_jsonc(None, &c).unwrap();
        let commented = written.replacen('{', "{ // set by hand", 1);
        assert_eq!(deadworks_jsonc(Some(&commented), &c).unwrap(), commented);
        let plugins = "{\n  \"Mine\": true, // keep\n}";
        assert_eq!(plugins_jsonc(Some(plugins), &["Mine".into()]).unwrap(), plugins);
    }

    #[test]
    fn a_comment_between_a_trailing_comma_and_the_bracket_is_fine() {
        let text = "{\n  \"require_steam_auth\": false, // LAN\n  \"list\": [1, 2, /* more */ ],\n}";
        let v: Value = serde_json::from_str(&strip_jsonc(text)).unwrap();
        assert_eq!(v["require_steam_auth"], false);
        assert_eq!(v["list"], json!([1, 2]));
    }

    #[test]
    fn strip_jsonc_keeps_strings_intact() {
        let s = strip_jsonc("{\"a\": \"http://x // not a comment\", /* c */ \"b\": \"ü\",}");
        let v: Value = serde_json::from_str(&s).unwrap();
        assert_eq!(v["a"], "http://x // not a comment");
        assert_eq!(v["b"], "ü");
    }
}
