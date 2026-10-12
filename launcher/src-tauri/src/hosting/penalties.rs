//! A server's bans, gags and mutes, read from and lifted in the file Deadworks keeps them in
//! (`configs/penalties/penalties.jsonc`, see `JsonPenaltyStore`).

use std::path::{Path, PathBuf};

use serde_json::{Map, Value};

use super::cfg;
use super::types::PenaltyInfo;

pub const KINDS: [&str; 3] = ["ban", "gag", "mute"];

pub fn path(configs: &Path) -> PathBuf {
    configs.join("penalties").join("penalties.jsonc")
}

/// Deadworks matches property names without regard to case. An explicit null counts as absent.
fn field<'a>(entry: &'a Value, name: &str) -> Option<&'a Value> {
    entry.as_object()?.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v).filter(|v| !v.is_null())
}

fn text(entry: &Value, name: &str) -> Option<String> {
    field(entry, name)?.as_str().map(str::to_string)
}

/// The penalty type, which Deadworks writes as a name and also accepts as a number.
fn kind(entry: &Value) -> Option<&'static str> {
    match field(entry, "type")? {
        Value::String(s) => KINDS.into_iter().find(|k| k.eq_ignore_ascii_case(s)),
        Value::Number(n) => KINDS.get(usize::try_from(n.as_u64()?).ok()?).copied(),
        _ => None,
    }
}

fn steam_id(entry: &Value) -> Option<String> {
    match field(entry, "steamId64")? {
        Value::Number(n) => n.as_u64().map(|id| id.to_string()),
        Value::String(s) => Some(s.clone()),
        _ => None,
    }
}

fn parse(file: &str) -> Result<Value, String> {
    let root: Value =
        serde_json::from_str(&cfg::strip_jsonc(file)).map_err(|e| format!("penalties.jsonc is invalid ({e})."))?;
    if root.is_object() {
        Ok(root)
    } else {
        Err("penalties.jsonc must be a JSON object.".into())
    }
}

fn entries(root: &Value) -> &[Value] {
    field(root, "penalties").and_then(Value::as_array).map_or(&[], Vec::as_slice)
}

fn lifted(entry: &Value) -> bool {
    field(entry, "removedUtc").is_some()
}

/// Every penalty that has not been lifted. Ones that have run out are still listed; the dates say so.
pub fn pending(file: &str) -> Result<Vec<PenaltyInfo>, String> {
    let root = parse(file)?;
    Ok(entries(&root)
        .iter()
        .filter(|e| !lifted(e))
        .filter_map(|e| {
            Some(PenaltyInfo {
                kind: kind(e)?.to_string(),
                steam_id64: steam_id(e)?,
                player_name: text(e, "playerName"),
                created_utc: text(e, "createdUtc"),
                expires_utc: text(e, "expiresUtc"),
                reason: text(e, "reason").unwrap_or_default(),
                admin_name: text(e, "adminName"),
            })
        })
        .collect())
}

fn set(entry: &mut Map<String, Value>, name: &str, value: Value) {
    entry.retain(|k, _| !k.eq_ignore_ascii_case(name));
    entry.insert(name.to_string(), value);
}

/// The file with every penalty of `kind` on `steam_id64` marked as lifted from the server console at
/// `now` (see [`utc_iso`]), the way `PenaltyManager.Remove` records it. None when there is none to lift.
pub fn lift(file: &str, kind_wanted: &str, steam_id64: &str, now: &str) -> Result<Option<String>, String> {
    let mut root = parse(file)?;
    let list = root
        .as_object_mut()
        .and_then(|o| o.iter_mut().find(|(k, _)| k.eq_ignore_ascii_case("penalties")))
        .and_then(|(_, v)| v.as_array_mut());
    let mut changed = false;
    for entry in list.into_iter().flatten() {
        if lifted(entry) || kind(entry) != Some(kind_wanted) || steam_id(entry).as_deref() != Some(steam_id64) {
            continue;
        }
        if let Some(fields) = entry.as_object_mut() {
            set(fields, "removedUtc", now.into());
            set(fields, "removedBySteamId64", 0.into());
            set(fields, "removedByName", "Console".into());
            changed = true;
        }
    }
    if !changed {
        return Ok(None);
    }
    // Deadworks keeps only its own header comment when it rewrites the file; so does this.
    let header: String = file.lines().take_while(|l| l.trim_start().starts_with("//")).map(|l| format!("{l}\n")).collect();
    let body = serde_json::to_string_pretty(&root).map_err(|e| e.to_string())?;
    Ok(Some(format!("{header}{body}\n")))
}

/// Unix seconds as the UTC timestamp Deadworks reads: `2026-10-08T17:03:20Z`.
pub fn utc_iso(unix_seconds: u64) -> String {
    let (days, rest) = (unix_seconds / 86_400, unix_seconds % 86_400);
    // Days to a calendar date, counting from 0000-03-01 so leap days fall at the end of a year.
    let z = days + 719_468;
    let (era, day_of_era) = (z / 146_097, z % 146_097);
    let year_of_era = (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let shifted_month = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * shifted_month + 2) / 5 + 1;
    let month = if shifted_month < 10 { shifted_month + 3 } else { shifted_month - 9 };
    let year = year_of_era + era * 400 + u64::from(month <= 2);
    format!("{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z", rest / 3_600, rest % 3_600 / 60, rest % 60)
}

#[cfg(test)]
mod tests {
    use super::*;

    const FILE: &str = r#"// Bans, gags and mutes.
// Second header line.

{
  "penalties": [
    {
      "id": "6f1c1c0e-0000-4000-8000-000000000001",
      "type": "Ban",
      "steamId64": 76561198000000001,
      "playerName": "will // not a comment",
      "createdUtc": "2026-10-08T10:00:00.1234567Z",
      "reason": "griefing",
      "adminSteamId64": 0,
      "adminName": "Console",
      "removedBySteamId64": 0
    },
    {
      "id": "6f1c1c0e-0000-4000-8000-000000000002",
      "Type": 1,
      "SteamId64": 76561198000000001,
      "createdUtc": "2026-10-08T10:00:00Z",
      "expiresUtc": "2026-10-08T11:00:00Z",
      "reason": "",
      "removedUtc": null
    },
    {
      "id": "6f1c1c0e-0000-4000-8000-000000000003",
      "type": "Ban",
      "steamId64": 76561198000000002,
      "createdUtc": "2026-10-01T10:00:00Z",
      "reason": "old",
      "removedUtc": "2026-10-02T10:00:00Z",
      "removedByName": "will"
    },
  ],
}
"#;

    #[test]
    fn lists_penalties_that_were_not_lifted() {
        let list = pending(FILE).unwrap();
        assert_eq!(list.len(), 2);
        assert_eq!(list[0].kind, "ban");
        assert_eq!(list[0].steam_id64, "76561198000000001");
        assert_eq!(list[0].player_name.as_deref(), Some("will // not a comment"));
        assert_eq!(list[0].expires_utc, None);
        assert_eq!(list[0].admin_name.as_deref(), Some("Console"));
        // Written by hand with capitals and a numeric type: read the way Deadworks reads it.
        assert_eq!(list[1].kind, "gag");
        assert_eq!(list[1].expires_utc.as_deref(), Some("2026-10-08T11:00:00Z"));
    }

    #[test]
    fn lifting_marks_only_the_matching_penalty() {
        let next = lift(FILE, "ban", "76561198000000001", "2026-10-08T12:00:00Z").unwrap().unwrap();
        assert!(next.starts_with("// Bans, gags and mutes.\n// Second header line.\n{"));

        let left = pending(&next).unwrap();
        assert_eq!(left.len(), 1);
        assert_eq!(left[0].kind, "gag");

        let root = parse(&next).unwrap();
        let ban = &entries(&root)[0];
        assert_eq!(text(ban, "removedUtc").as_deref(), Some("2026-10-08T12:00:00Z"));
        assert_eq!(text(ban, "removedByName").as_deref(), Some("Console"));
        // The SteamID survives as the number Deadworks wrote, and the earlier lift is left alone.
        assert_eq!(ban["steamId64"].as_u64(), Some(76561198000000001));
        assert_eq!(text(&entries(&root)[2], "removedByName").as_deref(), Some("will"));
    }

    #[test]
    fn nothing_to_lift_changes_nothing() {
        assert_eq!(lift(FILE, "mute", "76561198000000001", "2026-10-08T12:00:00Z").unwrap(), None);
        assert_eq!(lift(FILE, "ban", "76561198000000002", "2026-10-08T12:00:00Z").unwrap(), None);
        assert!(lift("{ not json", "ban", "76561198000000001", "x").is_err());
        assert_eq!(pending("{}").unwrap(), vec![]);
    }

    #[test]
    fn timestamps_are_utc_calendar_dates() {
        assert_eq!(utc_iso(0), "1970-01-01T00:00:00Z");
        assert_eq!(utc_iso(1_700_000_000), "2023-11-14T22:13:20Z");
        assert_eq!(utc_iso(951_782_400), "2000-02-29T00:00:00Z");
        assert_eq!(utc_iso(1_798_761_599), "2026-12-31T23:59:59Z");
    }
}
