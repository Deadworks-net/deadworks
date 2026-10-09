//! A server's console: the scrollback the UI shows, and everything the
//! launcher learns by reading it (readiness, SDR identity, public address,
//! players, unsupported-build failures).
//!
//! The launcher polls `status` (and `dw_host_status` on Deadworks builds that
//! have it) every few seconds. Their output would bury the log, so lines that
//! look like poll output are consumed while a poll is outstanding; anything
//! else printed meanwhile (other threads log in between) still shows.

use std::collections::VecDeque;
use std::fs::File;
use std::io::{BufWriter, Write};
use std::path::Path;

use serde::Deserialize;

use super::types::{ConsoleLine, LineKind, PlayerInfo};

const MAX_LINES: usize = 5000;

pub struct ConsoleBuffer {
    lines: VecDeque<ConsoleLine>,
    pending: Vec<ConsoleLine>,
    next_seq: u64,
    log: Option<BufWriter<File>>,
}

impl Default for ConsoleBuffer {
    fn default() -> Self {
        Self { lines: VecDeque::new(), pending: Vec::new(), next_seq: 1, log: None }
    }
}

impl ConsoleBuffer {
    pub fn push(&mut self, kind: LineKind, text: impl Into<String>) {
        let line = ConsoleLine { seq: self.next_seq, kind, text: text.into() };
        self.next_seq += 1;
        if let Some(log) = &mut self.log {
            let prefix = match kind {
                LineKind::Out => "",
                LineKind::In => "> ",
                LineKind::Sys => "# ",
            };
            let _ = writeln!(log, "{prefix}{}", line.text);
        }
        if self.lines.len() >= MAX_LINES {
            self.lines.pop_front();
        }
        self.lines.push_back(line.clone());
        self.pending.push(line);
    }

    /// The sequence number the next line will get.
    pub fn next_seq(&self) -> u64 {
        self.next_seq
    }

    /// Text of every line from `seq` on that is still in the scrollback.
    pub fn since(&self, seq: u64) -> Vec<String> {
        self.lines.iter().filter(|l| l.seq >= seq).map(|l| l.text.clone()).collect()
    }

    pub fn history(&self) -> Vec<ConsoleLine> {
        self.lines.iter().cloned().collect()
    }

    pub fn take_pending(&mut self) -> Vec<ConsoleLine> {
        if let Some(log) = &mut self.log {
            let _ = log.flush();
        }
        std::mem::take(&mut self.pending)
    }

    /// Start a new log file for this run, keeping the ten most recent.
    pub fn open_log(&mut self, dir: &Path) {
        let _ = std::fs::create_dir_all(dir);
        if let Ok(rd) = std::fs::read_dir(dir) {
            let mut logs: Vec<_> = rd.flatten().map(|e| e.path()).filter(|p| p.extension().is_some_and(|x| x == "log")).collect();
            logs.sort();
            while logs.len() >= 10 {
                let _ = std::fs::remove_file(logs.remove(0));
            }
        }
        let name = format!("console-{}.log", super::store::now_secs());
        self.log = File::create(dir.join(name)).ok().map(BufWriter::new);
    }

    pub fn close_log(&mut self) {
        if let Some(mut log) = self.log.take() {
            let _ = log.flush();
        }
    }
}

/// What a console line told us.
#[derive(Debug, PartialEq)]
pub enum Event {
    /// Logged on to Steam: the server is up.
    Ready,
    SdrId(String),
    RelayReady,
    PublicAddress(String),
    /// This Deadworks release can't hook the current game build.
    UnsupportedBuild,
    /// A complete `status` player table.
    StatusPlayers(Vec<StatusRow>),
    HostStatus(HostStatus),
    /// The server doesn't know `dw_host_status` (older Deadworks).
    NoHostStatus,
}

#[derive(Debug, Clone, PartialEq)]
pub struct StatusRow {
    pub id: i32,
    pub seconds: u64,
    pub ping: u32,
    pub bot: bool,
    pub name: String,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostStatus {
    #[serde(default)]
    pub players: Vec<HostPlayer>,
    #[serde(default)]
    pub next: Option<i32>,
    #[serde(default)]
    pub player_count: Option<u32>,
    /// Deadworks' kick and ban commands are registered (the Admin plugin is loaded).
    #[serde(default)]
    pub moderation: bool,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostPlayer {
    pub slot: i32,
    #[serde(default)]
    pub steam_id64: Option<String>,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub team: Option<i32>,
    #[serde(default)]
    pub hero: Option<String>,
    #[serde(default)]
    pub bot: bool,
    #[serde(default)]
    pub connected: Option<u64>,
    #[serde(default)]
    pub roles: Vec<String>,
}

/// Reads the console line by line. Owns the poll-hiding state.
#[derive(Default)]
pub struct Monitor {
    /// Polls sent and not yet answered.
    pub polls_pending: u32,
    in_status: bool,
    in_players: bool,
    rows: Vec<StatusRow>,
    awaiting_host_status: bool,
    /// Made for this run and sent with every `dw_host_status`. Only a reply that carries it is
    /// believed: players get text into the console (names, chat a plugin logs), and must not be
    /// able to pass it off as the server's answer.
    token: String,
}

impl Monitor {
    pub fn new(token: String) -> Self {
        Self { token, ..Self::default() }
    }

    /// The poll for the players in slot `from` and up.
    pub fn host_status_command(&self, from: i32) -> String {
        format!("dw_host_status {from} {}", self.token)
    }

    /// The JSON of a `DWHOST <token> {json}` reply to our own poll.
    fn host_status_reply<'a>(&self, message: &'a str) -> Option<&'a str> {
        if self.token.is_empty() {
            return None;
        }
        message.strip_prefix("DWHOST ")?.strip_prefix(self.token.as_str())?.strip_prefix(' ')
    }

    /// The console echoing a poll of ours back.
    fn is_host_status_echo(&self, t: &str) -> bool {
        !self.token.is_empty()
            && t.strip_prefix("dw_host_status ")
                .and_then(|rest| rest.strip_suffix(self.token.as_str()))
                .is_some_and(|from| from.trim().parse::<i32>().is_ok())
    }

    /// A poll was just injected (`status`, plus `dw_host_status` when `host_status`).
    pub fn poll_sent(&mut self, host_status: bool) {
        // A poll whose `#end` never came (server stalled) must not hide output forever.
        self.polls_pending = (self.polls_pending + 1).min(2);
        self.awaiting_host_status |= host_status;
    }

    /// Returns `(show this line in the console, events)`.
    pub fn feed(&mut self, line: &str) -> (bool, Vec<Event>) {
        let mut ev = Vec::new();
        let t = line.trim_end();

        // Unconditional signals, visible as normal log lines. Each is matched from the start of
        // the line or of the log message, never as a substring: player names appear inside lines.
        if let Some(id) = sdr_identity(t) {
            ev.push(Event::SdrId(id));
        }
        if t.contains("Gameserver logged on to Steam") || t.contains("Connection to Steam servers successful") {
            ev.push(Event::Ready);
        }
        if t.starts_with("SDR RelayNetworkStatus:") && t.contains("avail=OK") {
            ev.push(Event::RelayReady);
        }
        let log = deadworks_log(t);
        if let Some((level, message)) = log {
            if matches!(level, "ERR" | "CRT")
                && (message.starts_with("Failed to find signature") || message.starts_with("Failed to load data: Failed to find signature"))
            {
                ev.push(Event::UnsupportedBuild);
            }
        }

        if let Some(json) = self.host_status_reply(log_message(t)) {
            if let Ok(hs) = serde_json::from_str::<HostStatus>(json) {
                ev.push(Event::HostStatus(hs));
            }
            self.awaiting_host_status = false;
            return (false, ev);
        }
        if self.awaiting_host_status && t.contains("dw_host_status") && t.to_ascii_lowercase().starts_with("unknown command") {
            self.awaiting_host_status = false;
            ev.push(Event::NoHostStatus);
            return (false, ev);
        }
        if self.is_host_status_echo(t) {
            return (false, ev);
        }

        let hiding = self.polls_pending > 0;
        if hiding && t == "status" {
            self.in_status = true;
            self.in_players = false;
            self.rows.clear();
            return (false, ev);
        }

        if let Some(addr) = public_address(t) {
            ev.push(Event::PublicAddress(addr));
        }

        if self.in_status {
            if t == "#end" {
                self.in_status = false;
                self.in_players = false;
                self.polls_pending = self.polls_pending.saturating_sub(1);
                ev.push(Event::StatusPlayers(std::mem::take(&mut self.rows)));
                return (!hiding, ev);
            }
            if t.starts_with("---------players") {
                self.in_players = true;
                return (!hiding, ev);
            }
            if self.in_players {
                if t.trim_start().starts_with("id ") {
                    return (!hiding, ev);
                }
                if let Some(row) = parse_status_row(t) {
                    if row.id != 65535 {
                        self.rows.push(row);
                    }
                    return (!hiding, ev);
                }
            }
            if is_status_line(t) {
                return (!hiding, ev);
            }
        }
        (true, ev)
    }
}

/// `SV:  ServerSteamID=[A:1:570253314:51555] (90293420156608514).` -> the bracketed id.
fn sdr_identity(t: &str) -> Option<String> {
    let rest = t.strip_prefix("SV:").unwrap_or(t).trim_start().strip_prefix("ServerSteamID=")?;
    let id = &rest[..=rest.find(']')?];
    let fields: Vec<&str> = id.strip_prefix("[A:")?.strip_suffix(']')?.split(':').collect();
    let numeric = fields.iter().all(|f| !f.is_empty() && f.bytes().all(|b| b.is_ascii_digit()));
    (fields.len() == 3 && numeric).then(|| id.to_string())
}

/// `[2026-10-07 17:44:26.664] [deadworks] [INF] message` -> `("INF", "message")`.
///
/// The timestamp is required. Some engine lines begin with a player's name, and a name (32
/// characters at most) is too short to hold the whole prefix.
pub fn deadworks_log(line: &str) -> Option<(&str, &str)> {
    let (stamp, rest) = line.strip_prefix('[')?.split_once("] [deadworks] [")?;
    let shaped = stamp.len() == 23 && stamp.bytes().all(|b| b.is_ascii_digit() || matches!(b, b'-' | b':' | b'.' | b' '));
    if !shaped {
        return None;
    }
    rest.split_once("] ")
}

/// What a line says once Deadworks' log prefix (and its `[managed]` tag) is off. A line without
/// the prefix is its own message: over RCON replies come back bare.
pub fn log_message(line: &str) -> &str {
    let line = line.trim_end();
    match deadworks_log(line) {
        Some((_, message)) => message.strip_prefix("[managed] ").unwrap_or(message),
        None => line,
    }
}

/// `udp/ip   : 172.22.32.1:27020 (public 146.70.168.146:27020)` -> the public part.
fn public_address(t: &str) -> Option<String> {
    if !t.starts_with("udp/ip") {
        return None;
    }
    let start = t.find("(public ")? + 8;
    let end = t[start..].find(')')? + start;
    let addr = t[start..end].trim();
    (!addr.is_empty()).then(|| addr.to_string())
}

const STATUS_PREFIXES: &[&str] = &[
    "Server:",
    "Client:",
    "----- Status",
    "@ Current",
    "source ",
    "hostname ",
    "spawn ",
    "version ",
    "steamid ",
    "udp/ip ",
    "os/type ",
    "players ",
    "---------spawngroups",
    "loaded spawngroup",
    "Citadel Specific",
    "=====",
    "Game State:",
];

fn is_status_line(t: &str) -> bool {
    STATUS_PREFIXES.iter().any(|p| t.starts_with(p))
}

/// `    2 00:01:23   35    0     active 786432 1.2.3.4:27005 'Name'`
pub fn parse_status_row(line: &str) -> Option<StatusRow> {
    let (head, name) = match (line.find('\''), line.rfind('\'')) {
        (Some(a), Some(b)) if b > a => (&line[..a], line[a + 1..b].to_string()),
        _ => return None,
    };
    let tok: Vec<&str> = head.split_whitespace().collect();
    let id: i32 = tok.first()?.parse().ok()?;
    let time = tok.get(1).copied().unwrap_or("");
    let bot = time.eq_ignore_ascii_case("BOT");
    let seconds = parse_duration(time).unwrap_or(0);
    let ping = tok.get(2).and_then(|p| p.parse().ok()).unwrap_or(0);
    Some(StatusRow { id, seconds, ping, bot, name })
}

fn parse_duration(s: &str) -> Option<u64> {
    let mut total = 0u64;
    for part in s.split(':') {
        total = total * 60 + part.parse::<u64>().ok()?;
    }
    Some(total)
}

/// Combine the two player sources: `dw_host_status` knows slots, SteamIDs and
/// heroes; `status` knows ping and user ids. Without the former, status rows
/// stand alone and their id doubles as the slot.
pub fn merge_players(host: Option<&[HostPlayer]>, rows: &[StatusRow]) -> Vec<PlayerInfo> {
    match host {
        Some(host) => host
            .iter()
            .map(|h| {
                // The name is all the two share. When two players use the same one there is no
                // telling which row is whose, and a wrong guess would kick the wrong person.
                let unique = host.iter().filter(|o| o.name == h.name).count() == 1
                    && rows.iter().filter(|r| r.name == h.name).count() == 1;
                let row = rows.iter().find(|r| unique && r.name == h.name);
                PlayerInfo {
                    user_id: row.map(|r| r.id).unwrap_or(-1),
                    slot: h.slot,
                    steam_id64: h.steam_id64.clone().unwrap_or_default(),
                    name: h.name.clone(),
                    ping_ms: row.map(|r| r.ping).unwrap_or(0),
                    connected_seconds: h.connected.or(row.map(|r| r.seconds)).unwrap_or(0),
                    team: h.team.unwrap_or(0),
                    hero: h.hero.clone().unwrap_or_default(),
                    roles: h.roles.clone(),
                    bot: h.bot,
                }
            })
            .collect(),
        None => rows
            .iter()
            .map(|r| PlayerInfo {
                user_id: r.id,
                slot: r.id,
                steam_id64: String::new(),
                name: r.name.clone(),
                ping_ms: r.ping,
                connected_seconds: r.seconds,
                team: 0,
                hero: String::new(),
                roles: Vec::new(),
                bot: r.bot,
            })
            .collect(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const STATUS: &str = "status
Server:  Running [172.22.32.1:27020]
Client:  Disconnected
----- Status -----
@ Current  :  game
source   : console
hostname : Deadlock
citadel_new_years_fireworks at (0.000, 0.000, 2944.000) has no model name!
spawn    : 1
version  : 48/48 10725 insecure  public
steamid  : [A:1:379495426:51555] (90293419965850626)
udp/ip   : 172.22.32.1:27020 (public 146.70.168.146:27020)
os/type  : Windows dedicated
players  : 1 humans, 0 bots (0 max) (not hibernating) (unreserved)
---------spawngroups----
loaded spawngroup(  1)  : SV:  [1: dl_midtown | main lump | mapload]
---------players--------
  id     time ping loss      state   rate adr name
65535 [NoChan]    0    0 challenging      0unknown ''
    2    01:23   35    0     active 786432 1.2.3.4:27005 'Some Player'
Citadel Specific
===========================
Game State: 1 (Init)
#end";

    #[test]
    fn a_polled_status_is_hidden_but_interleaved_log_lines_show() {
        let mut m = Monitor::default();
        m.poll_sent(false);
        let mut shown = Vec::new();
        let mut events = Vec::new();
        for l in STATUS.lines() {
            let (show, ev) = m.feed(l);
            if show {
                shown.push(l);
            }
            events.extend(ev);
        }
        assert_eq!(shown, vec!["citadel_new_years_fireworks at (0.000, 0.000, 2944.000) has no model name!"]);
        assert!(events.contains(&Event::PublicAddress("146.70.168.146:27020".into())));
        let players = events.iter().find_map(|e| match e {
            Event::StatusPlayers(p) => Some(p.clone()),
            _ => None,
        });
        assert_eq!(
            players,
            Some(vec![StatusRow { id: 2, seconds: 83, ping: 35, bot: false, name: "Some Player".into() }])
        );
        assert_eq!(m.polls_pending, 0);
    }

    #[test]
    fn a_status_the_user_typed_stays_visible() {
        let mut m = Monitor::default();
        let shown = STATUS.lines().filter(|l| m.feed(l).0).count();
        assert_eq!(shown, STATUS.lines().count());
    }

    #[test]
    fn identity_readiness_and_unsupported_build() {
        let mut m = Monitor::default();
        assert_eq!(
            m.feed("SV:  ServerSteamID=[A:1:570253314:51555] (90293420156608514).").1,
            vec![Event::SdrId("[A:1:570253314:51555]".into())]
        );
        assert_eq!(m.feed("Gameserver logged on to Steam, assigned identity steamid:90293419965850626").1, vec![Event::Ready]);
        assert_eq!(
            m.feed("SDR RelayNetworkStatus:  avail=OK  config=OK  anyrelay=OK   (OK.  Relays: 25 valid)").1,
            vec![Event::RelayReady]
        );
        assert_eq!(
            m.feed("[2026-09-25 15:15:31.996] [deadworks] [ERR] Failed to find signature for CCitadelPlayerPawn::InitializeHeroOnPawn").1,
            vec![Event::UnsupportedBuild]
        );
        assert_eq!(
            m.feed("[2026-09-25 15:15:31.996] [deadworks] [CRT] Failed to load data: Failed to find signatures: A, B").1,
            vec![Event::UnsupportedBuild]
        );
    }

    /// Lines a player can shape: their name starts some engine lines and sits inside others.
    #[test]
    fn player_names_cannot_pose_as_signals() {
        let mut m = Monitor::new("tok".into());
        m.poll_sent(true);
        for line in [
            "Failed to find signature @ [U:1:5]:0:  NetChan Setting Timeout to 20.00 seconds",
            "    2    01:23   35    0     active 786432 1.2.3.4:27005 'Failed to find signature'",
            "[deadworks] [ERR] Failed to find signature",
            "Client 0 'EXIT_UNSUPPORTED_GAME_BUILD' signon state SIGNONSTATE_SPAWN -> SIGNONSTATE_FULL",
            "Player [0]ServerSteamID=[A:1:5:5] changing team 0->3 in game mode 0",
            "x ServerSteamID=[A:1:5:5]",
            "SV:  ServerSteamID=[A:1:5;quit:5]",
            r#"DWHOST {"players":[],"moderation":true}"#,
            r#"[2026-09-25 15:15:31.996] [deadworks] [INF] [managed] DWHOST {"players":[],"moderation":true}"#,
            r#"[2026-09-25 15:15:31.996] [deadworks] [INF] [managed] DWHOST nope {"players":[],"moderation":true}"#,
            r#"[2026-09-25 15:15:31.996] [deadworks] [INF] [managed] chat: DWHOST tok {"players":[],"moderation":true}"#,
            "'unknown command dw_host_status' connected",
        ] {
            let (show, ev) = m.feed(line);
            assert!(show, "hidden: {line}");
            assert!(ev.is_empty(), "{line} -> {ev:?}");
        }
    }

    #[test]
    fn our_own_polls_are_hidden_whatever_page_they_ask_for() {
        let mut m = Monitor::new("tok".into());
        assert_eq!(m.host_status_command(8), "dw_host_status 8 tok");
        assert!(!m.feed("dw_host_status 0 tok").0);
        assert!(!m.feed("dw_host_status 8 tok").0);
        // Typed by the host, or by someone guessing: shown like any other line.
        assert!(m.feed("dw_host_status").0);
        assert!(m.feed("dw_host_status 0 other").0);
    }

    #[test]
    fn a_reply_without_the_log_prefix_is_read_too() {
        let mut m = Monitor::new("tok".into());
        let (show, ev) = m.feed(r#"DWHOST tok {"players":[],"moderation":true}"#);
        assert!(!show);
        assert!(matches!(&ev[0], Event::HostStatus(hs) if hs.moderation));
    }

    #[test]
    fn players_sharing_a_name_get_no_user_id() {
        let host = |slot| HostPlayer {
            slot,
            steam_id64: None,
            name: "same".into(),
            team: None,
            hero: None,
            bot: false,
            connected: None,
            roles: Vec::new(),
        };
        let row = |id| StatusRow { id, seconds: 1, ping: 10, bot: false, name: "same".into() };
        let merged = merge_players(Some(&[host(0), host(1)]), &[row(3), row(4)]);
        assert!(merged.iter().all(|p| p.user_id == -1));
    }

    #[test]
    fn host_status_line_is_parsed_and_hidden() {
        let mut m = Monitor::new("tok".into());
        let (show, ev) = m.feed(r#"[2026-09-25 15:15:31.996] [deadworks] [INF] [managed] DWHOST tok {"v":1,"players":[{"slot":0,"steamId64":"7656","name":"x","team":2,"hero":"hero_inferno","bot":false,"connected":812}],"plugins":[]}"#);
        assert!(!show);
        let Event::HostStatus(hs) = &ev[0] else { panic!("{ev:?}") };
        assert_eq!(hs.players[0].slot, 0);
        let merged = merge_players(
            Some(&hs.players),
            &[StatusRow { id: 3, seconds: 5, ping: 40, bot: false, name: "x".into() }],
        );
        assert_eq!(merged[0].ping_ms, 40);
        assert_eq!(merged[0].user_id, 3);
        assert_eq!(merged[0].connected_seconds, 812);
        assert_eq!(merged[0].hero, "hero_inferno");
    }

    #[test]
    fn bot_rows_parse() {
        let row = parse_status_row("    1      BOT    0    0     active      0 'Bot Haze'").unwrap();
        assert!(row.bot);
        assert_eq!(row.name, "Bot Haze");
    }
}
