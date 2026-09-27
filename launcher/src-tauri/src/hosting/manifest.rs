//! What Steam says Deadlock consists of.
//!
//! `appmanifest_1422450.acf` names the installed build and the manifest id of
//! each depot; Steam keeps those depot manifests in `depotcache`. Each one lists
//! every shipped file with its size and the SHA1 Steam itself verifies against,
//! so a copy driven by them contains exactly the game — none of the mods,
//! plugins, replays or downloaded maps that live alongside it — and every file
//! can be checked against Steam's own hash.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub const DEADLOCK_APP_ID: &str = "1422450";

const MAGIC_PAYLOAD: u32 = 0x71F6_17D0;
const MAGIC_METADATA: u32 = 0x1F48_12BE;
const MAGIC_END: u32 = 0x32C4_15AB;
const FLAG_DIRECTORY: u64 = 64;
const FLAG_SYMLINK: u64 = 512;

/// `StateFlags` value Steam writes once an install is complete and current.
const STATE_FULLY_INSTALLED: u64 = 4;

#[derive(Debug, Clone)]
pub struct AppManifest {
    pub build_id: String,
    pub state_flags: u64,
    pub size_on_disk: u64,
    /// `(depot id, manifest id)` in manifest order.
    pub depots: Vec<(String, String)>,
}

impl AppManifest {
    /// Steam is patching, or the last update did not finish. Copying now would
    /// copy a half-updated game.
    pub fn updating(&self) -> bool {
        self.state_flags != STATE_FULLY_INSTALLED
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DepotFile {
    /// Relative to the app's install dir, with `\` separators as Steam stores it
    /// (e.g. `game\bin\win64\engine2.dll`).
    pub path: String,
    pub size: u64,
    pub sha1: [u8; 20],
}

pub fn sha1_hex(sha: &[u8; 20]) -> String {
    sha.iter().map(|b| format!("{b:02x}")).collect()
}

pub fn parse_sha1_hex(s: &str) -> Option<[u8; 20]> {
    if s.len() != 40 {
        return None;
    }
    let mut out = [0u8; 20];
    for (i, chunk) in s.as_bytes().chunks(2).enumerate() {
        out[i] = u8::from_str_radix(std::str::from_utf8(chunk).ok()?, 16).ok()?;
    }
    Some(out)
}

/// The appmanifest that sits next to a `...\steamapps\common\Deadlock\game` dir.
pub fn appmanifest_path_for_game_dir(game_dir: &Path) -> Option<PathBuf> {
    // game -> Deadlock -> common -> steamapps
    let steamapps = game_dir.parent()?.parent()?.parent()?;
    Some(steamapps.join(format!("appmanifest_{DEADLOCK_APP_ID}.acf")))
}

pub fn read_appmanifest(path: &Path) -> Result<AppManifest, String> {
    let text = std::fs::read_to_string(path)
        .map_err(|e| format!("Couldn't read Steam's install record ({}): {e}", path.display()))?;
    parse_appmanifest(&text)
}

pub fn parse_appmanifest(text: &str) -> Result<AppManifest, String> {
    let root = parse_vdf(text);
    let state = root
        .get("AppState")
        .and_then(Vdf::as_map)
        .ok_or("Steam's install record for Deadlock is malformed")?;
    let get = |k: &str| state.get(k).and_then(Vdf::as_str).unwrap_or("").to_string();
    let depots = state
        .get("InstalledDepots")
        .and_then(Vdf::as_map)
        .map(|m| {
            m.iter()
                .filter_map(|(depot, v)| {
                    let manifest = v.as_map()?.get("manifest")?.as_str()?;
                    Some((depot.clone(), manifest.to_string()))
                })
                .collect()
        })
        .unwrap_or_default();
    Ok(AppManifest {
        build_id: get("buildid"),
        state_flags: get("StateFlags").parse().unwrap_or(0),
        size_on_disk: get("SizeOnDisk").parse().unwrap_or(0),
        depots,
    })
}

/// Every shipped file of the installed build, sorted by path, read from the
/// depot manifests Steam cached. `depotcache_dirs` are searched in order.
pub fn load_depot_files(app: &AppManifest, depotcache_dirs: &[PathBuf]) -> Result<Vec<DepotFile>, String> {
    if app.depots.is_empty() {
        return Err("Steam's install record lists no game depots".into());
    }
    let mut files = BTreeMap::new();
    for (depot, manifest) in &app.depots {
        let name = format!("{depot}_{manifest}.manifest");
        let path = depotcache_dirs
            .iter()
            .map(|d| d.join(&name))
            .find(|p| p.is_file())
            .ok_or_else(|| {
                format!("Steam's file list for Deadlock (depot {depot}) is missing. Verify the game files in Steam and try again.")
            })?;
        let bytes = std::fs::read(&path).map_err(|e| format!("Couldn't read {}: {e}", path.display()))?;
        for f in parse_depot_manifest(&bytes)? {
            files.insert(f.path.to_ascii_lowercase(), f);
        }
    }
    Ok(files.into_values().collect())
}

/// Where Steam keeps depot manifests: the Steam root's `depotcache`, and (newer
/// clients, SteamCMD) one inside the library's `steamapps`.
pub fn depotcache_dirs(steam_root: Option<&Path>, game_dir: &Path) -> Vec<PathBuf> {
    let mut dirs = Vec::new();
    if let Some(root) = steam_root {
        dirs.push(root.join("depotcache"));
    }
    if let Some(steamapps) = game_dir.parent().and_then(Path::parent).and_then(Path::parent) {
        dirs.push(steamapps.join("depotcache"));
        if let Some(library) = steamapps.parent() {
            dirs.push(library.join("depotcache"));
        }
    }
    dirs
}

/// Parse a Steam `ContentManifest` file: sections of `(magic u32, len u32,
/// protobuf)` ending in an end marker. Only the payload's file mappings are
/// read; filenames must not be encrypted (Steam stores them decrypted once the
/// depot is installed).
pub fn parse_depot_manifest(bytes: &[u8]) -> Result<Vec<DepotFile>, String> {
    let mut i = 0usize;
    let mut out = Vec::new();
    let mut encrypted = false;
    while i + 4 <= bytes.len() {
        let magic = u32::from_le_bytes(bytes[i..i + 4].try_into().unwrap());
        if magic == MAGIC_END {
            break;
        }
        if i + 8 > bytes.len() {
            return Err("truncated depot manifest".into());
        }
        let len = u32::from_le_bytes(bytes[i + 4..i + 8].try_into().unwrap()) as usize;
        i += 8;
        let section = bytes.get(i..i + len).ok_or("truncated depot manifest section")?;
        i += len;
        match magic {
            MAGIC_PAYLOAD => {
                for field in Proto::new(section) {
                    let (1, Wire::Bytes(mapping)) = field? else { continue };
                    if let Some(f) = parse_file_mapping(mapping)? {
                        out.push(f);
                    }
                }
            }
            MAGIC_METADATA => {
                for field in Proto::new(section) {
                    if let (4, Wire::Varint(v)) = field? {
                        encrypted = v != 0;
                    }
                }
            }
            _ => {}
        }
    }
    if encrypted {
        return Err("Steam's file list for Deadlock is encrypted. Verify the game files in Steam and try again.".into());
    }
    Ok(out)
}

fn parse_file_mapping(bytes: &[u8]) -> Result<Option<DepotFile>, String> {
    let mut path = None;
    let mut size = 0u64;
    let mut flags = 0u64;
    let mut sha = None;
    for field in Proto::new(bytes) {
        match field? {
            (1, Wire::Bytes(b)) => path = Some(String::from_utf8_lossy(b).into_owned()),
            (2, Wire::Varint(v)) => size = v,
            (3, Wire::Varint(v)) => flags = v,
            (5, Wire::Bytes(b)) if b.len() == 20 => sha = Some(<[u8; 20]>::try_from(b).unwrap()),
            _ => {}
        }
    }
    if flags & (FLAG_DIRECTORY | FLAG_SYMLINK) != 0 {
        return Ok(None);
    }
    let path = path.ok_or("depot manifest entry without a filename")?;
    // An empty file has no content hash; SHA1 of nothing is well known.
    let sha1 = sha.unwrap_or([
        0xda, 0x39, 0xa3, 0xee, 0x5e, 0x6b, 0x4b, 0x0d, 0x32, 0x55, 0xbf, 0xef, 0x95, 0x60, 0x18, 0x90, 0xaf, 0xd8, 0x07,
        0x09,
    ]);
    if path.split(['\\', '/']).any(|seg| seg == ".." || seg.is_empty()) || path.contains(':') {
        return Err(format!("refusing unsafe path in depot manifest: {path}"));
    }
    Ok(Some(DepotFile { path, size, sha1 }))
}

// ── Minimal protobuf reader ──

enum Wire<'a> {
    Varint(u64),
    Bytes(&'a [u8]),
    Fixed,
}

struct Proto<'a> {
    b: &'a [u8],
    i: usize,
}

impl<'a> Proto<'a> {
    fn new(b: &'a [u8]) -> Self {
        Self { b, i: 0 }
    }

    fn varint(&mut self) -> Result<u64, String> {
        let mut out = 0u64;
        for shift in (0..64).step_by(7) {
            let byte = *self.b.get(self.i).ok_or("truncated varint")?;
            self.i += 1;
            out |= u64::from(byte & 0x7f) << shift;
            if byte < 0x80 {
                return Ok(out);
            }
        }
        Err("varint too long".into())
    }
}

impl<'a> Iterator for Proto<'a> {
    type Item = Result<(u32, Wire<'a>), String>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.i >= self.b.len() {
            return None;
        }
        let step = (|| {
            let key = self.varint()?;
            let field = (key >> 3) as u32;
            let wire = match key & 7 {
                0 => Wire::Varint(self.varint()?),
                1 => {
                    self.i += 8;
                    Wire::Fixed
                }
                2 => {
                    let len = self.varint()? as usize;
                    let data = self.b.get(self.i..self.i + len).ok_or("truncated field")?;
                    self.i += len;
                    Wire::Bytes(data)
                }
                5 => {
                    self.i += 4;
                    Wire::Fixed
                }
                other => return Err(format!("unsupported protobuf wire type {other}")),
            };
            Ok((field, wire))
        })();
        if step.is_err() {
            self.i = self.b.len();
        }
        Some(step)
    }
}

// ── Minimal KeyValues (VDF) reader ──

#[derive(Debug, Clone)]
pub enum Vdf {
    Str(String),
    Map(BTreeMap<String, Vdf>),
}

impl Vdf {
    pub fn as_str(&self) -> Option<&str> {
        match self {
            Vdf::Str(s) => Some(s),
            Vdf::Map(_) => None,
        }
    }

    pub fn as_map(&self) -> Option<&BTreeMap<String, Vdf>> {
        match self {
            Vdf::Map(m) => Some(m),
            Vdf::Str(_) => None,
        }
    }
}

/// Parse Valve KeyValues text (quoted or bare tokens, `//` comments, braces).
/// Lenient: malformed input yields whatever parsed cleanly.
pub fn parse_vdf(text: &str) -> BTreeMap<String, Vdf> {
    let tokens = vdf_tokens(text);
    let mut pos = 0;
    parse_vdf_map(&tokens, &mut pos)
}

#[derive(Debug, PartialEq)]
enum Tok {
    Str(String),
    Open,
    Close,
}

fn parse_vdf_map(tokens: &[Tok], pos: &mut usize) -> BTreeMap<String, Vdf> {
    let mut map = BTreeMap::new();
    while *pos < tokens.len() {
        match &tokens[*pos] {
            Tok::Close => {
                *pos += 1;
                break;
            }
            Tok::Open => *pos += 1,
            Tok::Str(key) => {
                *pos += 1;
                match tokens.get(*pos) {
                    Some(Tok::Open) => {
                        *pos += 1;
                        map.insert(key.clone(), Vdf::Map(parse_vdf_map(tokens, pos)));
                    }
                    Some(Tok::Str(v)) => {
                        *pos += 1;
                        map.insert(key.clone(), Vdf::Str(v.clone()));
                    }
                    _ => {}
                }
            }
        }
    }
    map
}

fn vdf_tokens(text: &str) -> Vec<Tok> {
    let b = text.as_bytes();
    let mut i = 0;
    let mut out = Vec::new();
    while i < b.len() {
        match b[i] {
            b'/' if b.get(i + 1) == Some(&b'/') => {
                while i < b.len() && b[i] != b'\n' {
                    i += 1;
                }
            }
            b'{' => {
                out.push(Tok::Open);
                i += 1;
            }
            b'}' => {
                out.push(Tok::Close);
                i += 1;
            }
            b'"' => {
                i += 1;
                let mut s = Vec::new();
                while i < b.len() && b[i] != b'"' {
                    if b[i] == b'\\' && i + 1 < b.len() {
                        i += 1;
                        s.push(match b[i] {
                            b'n' => b'\n',
                            b't' => b'\t',
                            c => c,
                        });
                    } else {
                        s.push(b[i]);
                    }
                    i += 1;
                }
                i += 1;
                out.push(Tok::Str(String::from_utf8_lossy(&s).into_owned()));
            }
            c if c.is_ascii_whitespace() => i += 1,
            _ => {
                let start = i;
                while i < b.len() && !b[i].is_ascii_whitespace() && !matches!(b[i], b'{' | b'}' | b'"') {
                    i += 1;
                }
                out.push(Tok::Str(String::from_utf8_lossy(&b[start..i]).into_owned()));
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    const ACF: &str = r#""AppState"
{
	"appid"		"1422450"
	"StateFlags"		"4"
	"SizeOnDisk"		"36514514979"
	"buildid"		"25379491"
	"InstalledDepots"
	{
		"1422451"
		{
			"manifest"		"886051970741897775"
			"size"		"977372005"
		}
		"1422456"
		{
			"manifest"		"9192361732058507254"
			"size"		"34877055548"
		}
	}
}
"#;

    #[test]
    fn appmanifest_fields() {
        let m = parse_appmanifest(ACF).unwrap();
        assert_eq!(m.build_id, "25379491");
        assert!(!m.updating());
        assert_eq!(m.size_on_disk, 36_514_514_979);
        assert_eq!(
            m.depots,
            vec![
                ("1422451".to_string(), "886051970741897775".to_string()),
                ("1422456".to_string(), "9192361732058507254".to_string())
            ]
        );
        let updating = parse_appmanifest(&ACF.replace("\"StateFlags\"\t\t\"4\"", "\"StateFlags\"\t\t\"1026\"")).unwrap();
        assert!(updating.updating());
    }

    fn varint(mut v: u64, out: &mut Vec<u8>) {
        loop {
            let b = (v & 0x7f) as u8;
            v >>= 7;
            if v == 0 {
                out.push(b);
                return;
            }
            out.push(b | 0x80);
        }
    }

    fn bytes_field(field: u32, data: &[u8], out: &mut Vec<u8>) {
        varint(u64::from(field << 3 | 2), out);
        varint(data.len() as u64, out);
        out.extend_from_slice(data);
    }

    fn varint_field(field: u32, v: u64, out: &mut Vec<u8>) {
        varint(u64::from(field << 3), out);
        varint(v, out);
    }

    fn manifest_bytes(entries: &[(&str, u64, u64, [u8; 20])], encrypted: bool) -> Vec<u8> {
        let mut payload = Vec::new();
        for (name, size, flags, sha) in entries {
            let mut m = Vec::new();
            bytes_field(1, name.as_bytes(), &mut m);
            varint_field(2, *size, &mut m);
            varint_field(3, *flags, &mut m);
            bytes_field(5, sha, &mut m);
            bytes_field(1, &m, &mut payload);
        }
        let mut meta = Vec::new();
        varint_field(1, 1422451, &mut meta);
        varint_field(4, u64::from(encrypted), &mut meta);
        let mut out = Vec::new();
        for (magic, sec) in [(MAGIC_PAYLOAD, &payload), (MAGIC_METADATA, &meta)] {
            out.extend_from_slice(&magic.to_le_bytes());
            out.extend_from_slice(&(sec.len() as u32).to_le_bytes());
            out.extend_from_slice(sec);
        }
        out.extend_from_slice(&MAGIC_END.to_le_bytes());
        out
    }

    #[test]
    fn depot_manifest_files_skip_dirs() {
        let bytes = manifest_bytes(
            &[
                ("game\\bin\\win64\\engine2.dll", 42, 0, [7; 20]),
                ("game\\citadel", 0, FLAG_DIRECTORY, [0; 20]),
            ],
            false,
        );
        let files = parse_depot_manifest(&bytes).unwrap();
        assert_eq!(files, vec![DepotFile { path: "game\\bin\\win64\\engine2.dll".into(), size: 42, sha1: [7; 20] }]);
    }

    #[test]
    fn encrypted_or_unsafe_manifests_are_refused() {
        assert!(parse_depot_manifest(&manifest_bytes(&[("game\\a", 1, 0, [1; 20])], true)).is_err());
        assert!(parse_depot_manifest(&manifest_bytes(&[("game\\..\\..\\evil", 1, 0, [1; 20])], false)).is_err());
        assert!(parse_depot_manifest(&manifest_bytes(&[("C:\\evil", 1, 0, [1; 20])], false)).is_err());
    }

    #[test]
    fn sha_hex_round_trip() {
        let sha = [0xab; 20];
        assert_eq!(parse_sha1_hex(&sha1_hex(&sha)), Some(sha));
        assert_eq!(parse_sha1_hex("zz"), None);
    }
}
