//! Reads the dw_* keys a Deadworks server publishes in A2S_RULES. The format is specified on
//! `ContentManifest` in the Deadworks server framework; this is the client side of it.
//!
//! Everything here is untrusted input from whatever answered the query, so names are checked
//! before they can ever become file names, and URLs before they are ever fetched.

use std::collections::HashMap;

/// Length of the content hash a server advertises: the first 16 hex digits of the SHA-256 of the
/// uncompressed .vpk.
pub const HASH_LEN: usize = 16;

/// No file larger than this is ever installed, so an entry claiming more can never be satisfied.
pub const MAX_FILE_BYTES: u64 = 4 * 1024 * 1024 * 1024;
/// More entries than a real server can fit in its reply. Anything past it is ignored.
const MAX_ENTRIES: usize = 64;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Addon,
    Map,
}

impl Kind {
    /// The kind string used by `target_dir_for` and versions.json.
    pub fn as_str(self) -> &'static str {
        match self {
            Kind::Addon => "addon",
            Kind::Map => "map",
        }
    }

    fn fastdl_dir(self) -> &'static str {
        match self {
            Kind::Addon => "addons",
            Kind::Map => "maps",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    pub name: String,
    pub kind: Kind,
    /// None when the server could not read the file itself, so there is nothing to verify against.
    pub hash: Option<String>,
    /// The file's uncompressed size in bytes, as the server read it.
    pub size: Option<u64>,
}

#[derive(Debug, Default, PartialEq)]
pub struct Advertised {
    /// Entries with safe names. Unsafe ones are left out entirely.
    pub entries: Vec<Entry>,
    /// Names that were refused because they are not safe to use as a file name.
    pub rejected: Vec<String>,
    /// True when every entry was listed, safe, and hashed - i.e. the rules alone are enough to
    /// fetch and verify everything the server runs.
    pub complete: bool,
    /// The server's dw_fastdl base URL, if it set a valid one.
    pub fastdl: Option<String>,
}

/// None when the server does not publish the format at all (no dw_ver 1).
pub fn parse(rules: &HashMap<String, String>) -> Option<Advertised> {
    if rules.get("dw_ver").map(String::as_str) != Some("1") {
        return None;
    }

    let mut advertised = Advertised {
        complete: true,
        ..Default::default()
    };
    for (kind, list_key, count_key) in [
        (Kind::Addon, "dw_addons", "dw_addons_n"),
        (Kind::Map, "dw_maps", "dw_maps_n"),
    ] {
        let listed: Vec<&str> = rules
            .get(list_key)
            .map(|v| v.split(',').map(str::trim).filter(|s| !s.is_empty()).collect())
            .unwrap_or_default();
        let total = rules
            .get(count_key)
            .and_then(|v| v.parse::<usize>().ok())
            .unwrap_or(listed.len());
        if listed.len() < total || listed.len() > MAX_ENTRIES {
            // The server's list did not fit in the reply; the rest is not known.
            advertised.complete = false;
        }
        let listed = &listed[..listed.len().min(MAX_ENTRIES)];

        for &item in listed {
            // name, name:hash or name:hash:size
            let mut parts = item.splitn(3, ':');
            let name = parts.next().unwrap_or_default();
            let hash = parts.next();
            let size = parts.next();
            if !is_safe_name(name)
                || hash.is_some_and(|h| !is_hash(h))
                || size.is_some_and(|s| parse_size(s).is_none())
            {
                advertised.rejected.push(item.to_string());
                advertised.complete = false;
                continue;
            }
            if hash.is_none() {
                advertised.complete = false;
            }
            advertised.entries.push(Entry {
                name: name.to_string(),
                kind,
                hash: hash.map(str::to_string),
                size: size.and_then(parse_size),
            });
        }
    }

    advertised.fastdl = rules.get("dw_fastdl").and_then(|u| normalize_base_url(u));
    Some(advertised)
}

/// Whether `name` can safely become a file name: 1-64 characters of a-z, 0-9 and '_' - so no
/// separators, dots or anything else that could climb out of the install directory - and not a
/// Windows device name, which Windows treats as a device even with an extension.
pub fn is_safe_name(name: &str) -> bool {
    const DEVICES: [&str; 22] = [
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7",
        "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];
    !name.is_empty()
        && name.len() <= 64
        && name
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'_')
        && !DEVICES.contains(&name)
}

fn is_hash(hash: &str) -> bool {
    hash.len() == HASH_LEN && hash.bytes().all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f'))
}

/// Plain decimal digits only: `str::parse` would also take a leading '+'.
fn parse_size(size: &str) -> Option<u64> {
    if size.is_empty() || !size.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    size.parse().ok().filter(|&bytes| bytes <= MAX_FILE_BYTES)
}

/// Accepts only an absolute http(s) URL with no credentials, query or fragment, since paths are
/// appended to it. Returns it without a trailing '/'.
pub fn normalize_base_url(raw: &str) -> Option<String> {
    let trimmed = raw.trim().trim_end_matches('/');
    let url = reqwest::Url::parse(trimmed).ok()?;
    let acceptable = matches!(url.scheme(), "http" | "https")
        && url.host().is_some()
        && url.username().is_empty()
        && url.password().is_none()
        && url.query().is_none()
        && url.fragment().is_none();
    acceptable.then(|| trimmed.to_string())
}

/// Where an entry is fetched from under a fastDL base URL. None for an entry with no hash.
pub fn fastdl_url(base: &str, entry: &Entry) -> Option<String> {
    let hash = entry.hash.as_deref()?;
    Some(format!(
        "{}/{}/{}_{}.vpk.bz2",
        base,
        entry.kind.fastdl_dir(),
        entry.name,
        hash
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    const H: &str = "9330149c74886724";

    fn rules(pairs: &[(&str, &str)]) -> HashMap<String, String> {
        pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
    }

    #[test]
    fn ignores_servers_without_the_format() {
        assert_eq!(parse(&rules(&[])), None);
        assert_eq!(parse(&rules(&[("dw_ver", "2"), ("dw_addons", "x")])), None);
    }

    #[test]
    fn reads_a_complete_advertisement() {
        let adv = parse(&rules(&[
            ("dw_ver", "1"),
            ("dw_addons", &format!("turbo:{H}:11965")),
            ("dw_addons_n", "1"),
            ("dw_maps", &format!("dl_midtown:{H}:1053579891")),
            ("dw_maps_n", "1"),
            ("dw_fastdl", "https://dl.example.com/dw/"),
        ]))
        .unwrap();
        assert!(adv.complete);
        assert_eq!(adv.fastdl.as_deref(), Some("https://dl.example.com/dw"));
        assert_eq!(
            adv.entries,
            vec![
                Entry { name: "turbo".into(), kind: Kind::Addon, hash: Some(H.into()), size: Some(11965) },
                Entry { name: "dl_midtown".into(), kind: Kind::Map, hash: Some(H.into()), size: Some(1053579891) },
            ]
        );
    }

    #[test]
    fn size_is_optional_but_must_be_plain_digits() {
        let adv = parse(&rules(&[
            ("dw_ver", "1"),
            ("dw_addons", &format!("a:{H},b:{H}:12x,c:{H}:+5,d:{H}:,e:{H}:99999999999999999999999,f:{H}:7")),
        ]))
        .unwrap();
        let named: Vec<(&str, Option<u64>)> = adv.entries.iter().map(|e| (e.name.as_str(), e.size)).collect();
        assert_eq!(named, vec![("a", None), ("f", Some(7))]);
        assert_eq!(adv.rejected.len(), 4);
    }

    #[test]
    fn a_reply_cannot_ask_for_an_impossible_file_or_an_endless_list() {
        let too_big = format!("huge:{H}:{}", MAX_FILE_BYTES + 1);
        let adv = parse(&rules(&[("dw_ver", "1"), ("dw_addons", &format!("{too_big},fits:{H}:{MAX_FILE_BYTES}"))])).unwrap();
        assert_eq!(adv.entries.iter().map(|e| e.name.as_str()).collect::<Vec<_>>(), vec!["fits"]);
        assert!(!adv.complete);

        let many = (0..200).map(|i| format!("a{i}:{H}:1")).collect::<Vec<_>>().join(",");
        let adv = parse(&rules(&[("dw_ver", "1"), ("dw_addons", &many)])).unwrap();
        assert_eq!(adv.entries.len(), 64);
        assert!(!adv.complete);
    }

    #[test]
    fn hashless_and_truncated_lists_are_incomplete() {
        let hashless = parse(&rules(&[("dw_ver", "1"), ("dw_addons", "ware"), ("dw_addons_n", "1")])).unwrap();
        assert!(!hashless.complete);
        assert_eq!(hashless.entries[0].hash, None);

        let truncated = parse(&rules(&[("dw_ver", "1"), ("dw_addons", &format!("turbo:{H}")), ("dw_addons_n", "5")])).unwrap();
        assert!(!truncated.complete);
    }

    #[test]
    fn refuses_unsafe_names_and_malformed_hashes() {
        let adv = parse(&rules(&[
            ("dw_ver", "1"),
            ("dw_addons", &format!("../../evil:{H},con:{H},Turbo:{H},ok:NOTAHASH,good:{H}")),
            ("dw_addons_n", "5"),
        ]))
        .unwrap();
        assert!(!adv.complete);
        assert_eq!(adv.entries.len(), 1);
        assert_eq!(adv.entries[0].name, "good");
        assert_eq!(adv.rejected.len(), 4);
    }

    #[test]
    fn safe_names_are_plain_file_names() {
        for good in ["turbo", "ware_arena", "dl_midtown", "a1"] {
            assert!(is_safe_name(good), "{good}");
        }
        for bad in ["", "..", "a/b", "a\\b", "c:x", "a.vpk", "UPPER", "nul", "com1", &"x".repeat(65)] {
            assert!(!is_safe_name(bad), "{bad}");
        }
    }

    #[test]
    fn base_urls_must_be_plain_http_or_https() {
        assert_eq!(normalize_base_url(" http://10.0.0.5:8080/ ").as_deref(), Some("http://10.0.0.5:8080"));
        for bad in [
            "ftp://dl.example.com",
            "dl.example.com/fastdl",
            "https://dl.example.com/fastdl?token=1",
            "https://dl.example.com/fastdl#top",
            "https://user:secret@dl.example.com/fastdl",
            "file:///C:/Windows",
        ] {
            assert_eq!(normalize_base_url(bad), None, "{bad}");
        }
    }

    #[test]
    fn fastdl_urls_follow_the_server_layout() {
        let addon = Entry { name: "turbo".into(), kind: Kind::Addon, hash: Some(H.into()), size: Some(11965) };
        let map = Entry { name: "ware_arena".into(), kind: Kind::Map, hash: Some(H.into()), size: None };
        assert_eq!(
            fastdl_url("https://dl.example.com/dw", &addon).unwrap(),
            format!("https://dl.example.com/dw/addons/turbo_{H}.vpk.bz2")
        );
        assert_eq!(
            fastdl_url("https://dl.example.com/dw", &map).unwrap(),
            format!("https://dl.example.com/dw/maps/ware_arena_{H}.vpk.bz2")
        );
        assert_eq!(fastdl_url("https://x", &Entry { hash: None, ..addon }), None);
    }
}
