//! Content verification: SHA-256 of installed files (cached, so a 1 GB map is read once per
//! version) and decompression (zstd or bzip2) that hashes what it writes and refuses to run away.

use std::collections::HashMap;
use std::fs::File;
use std::io::{BufRead, BufReader, BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::time::UNIX_EPOCH;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

pub(crate) fn sha256_file(path: &Path) -> Result<String, String> {
    sha256_file_with(path, |_| {})
}

/// [`sha256_file`], reporting bytes read as it goes. Reports are throttled: a 1 GB file would
/// otherwise fire a progress event a thousand times over.
pub(crate) fn sha256_file_with(path: &Path, mut on_read: impl FnMut(u64)) -> Result<String, String> {
    const REPORT_EVERY: u64 = 16 * 1024 * 1024;

    let file = File::open(path)
        .map_err(|e| format!("Failed to open {} for hashing: {}", path.display(), e))?;
    let mut reader = BufReader::new(file);
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 256 * 1024];
    let mut read_total = 0u64;
    let mut next_report = REPORT_EVERY;
    loop {
        let n = reader
            .read(&mut buf)
            .map_err(|e| format!("Read error while hashing: {}", e))?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        read_total += n as u64;
        if read_total >= next_report {
            on_read(read_total);
            next_report = read_total + REPORT_EVERY;
        }
    }
    on_read(read_total);
    Ok(format!("{:x}", hasher.finalize()))
}

#[derive(Clone, Serialize, Deserialize)]
struct CachedHash {
    size: u64,
    mtime_ns: u64,
    sha256: String,
}

/// SHA-256 of installed content, keyed by path and invalidated by size or modified time, and kept
/// in citadel/deadworks_cache/hashes.json. A stale entry can only make a check fail and trigger a
/// re-download, never accept the wrong file.
pub(crate) struct HashCache {
    path: PathBuf,
    files: HashMap<String, CachedHash>,
    dirty: bool,
}

impl HashCache {
    pub fn load(game_dir: &Path) -> Self {
        Self::load_from(
            game_dir
                .join("citadel")
                .join("deadworks_cache")
                .join("hashes.json"),
        )
    }

    fn load_from(path: PathBuf) -> Self {
        let files = std::fs::read(&path)
            .ok()
            .and_then(|bytes| serde_json::from_slice(&bytes).ok())
            .unwrap_or_default();
        Self { path, files, dirty: false }
    }

    /// Full hex SHA-256 of `file`, or None if it does not exist. Only reads the file when it has
    /// changed since it was last hashed, and `on_read` reports progress while it does - a cache
    /// hit reports nothing, because nothing is read.
    pub fn sha256(
        &mut self,
        file: &Path,
        on_read: impl FnMut(u64),
    ) -> Result<Option<String>, String> {
        let Some(before) = stamp(file)? else {
            return Ok(None);
        };
        let key = file.to_string_lossy().into_owned();
        if let Some(hit) = self.files.get(&key) {
            if (hit.size, hit.mtime_ns) == before {
                return Ok(Some(hit.sha256.clone()));
            }
        }

        let sha = sha256_file_with(file, on_read)?;
        // Only remember a hash that provably matches the file as it now stands.
        if stamp(file)? == Some(before) {
            self.insert(key, before, &sha);
        }
        Ok(Some(sha))
    }

    /// Records the hash of a file that was just written, so the next check need not read it.
    pub fn record(&mut self, file: &Path, sha256: &str) {
        if let Ok(Some(stamp)) = stamp(file) {
            self.insert(file.to_string_lossy().into_owned(), stamp, sha256);
        }
    }

    fn insert(&mut self, key: String, (size, mtime_ns): (u64, u64), sha256: &str) {
        self.files.insert(key, CachedHash { size, mtime_ns, sha256: sha256.to_string() });
        self.dirty = true;
    }

    /// Writes the cache if anything changed. Failures are only logged: the cache is an
    /// optimisation, and losing it just means hashing again.
    pub fn save(&mut self) {
        if !self.dirty {
            return;
        }
        let result = (|| -> std::io::Result<()> {
            if let Some(dir) = self.path.parent() {
                std::fs::create_dir_all(dir)?;
            }
            let temp = self.path.with_extension("json.part");
            std::fs::write(&temp, serde_json::to_vec_pretty(&self.files)?)?;
            std::fs::rename(&temp, &self.path)
        })();
        match result {
            Ok(()) => self.dirty = false,
            Err(e) => eprintln!("[content] Could not save {}: {}", self.path.display(), e),
        }
    }
}

/// (size, modified time in ns) of `file`, or None if it does not exist.
fn stamp(file: &Path) -> Result<Option<(u64, u64)>, String> {
    let meta = match std::fs::metadata(file) {
        Ok(meta) => meta,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(format!("Could not read {}: {}", file.display(), e)),
    };
    let mtime_ns = meta
        .modified()
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0);
    Ok(Some((meta.len(), mtime_ns)))
}

/// What a download has to turn out to be, as its server advertised it.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct Expected {
    /// Prefix the SHA-256 of the decompressed file must start with.
    pub sha256: String,
    /// Its exact decompressed size, when the server gave one.
    pub size: Option<u64>,
    /// Whether a mismatch fails the install. Cleared once the player has been shown the
    /// mismatch and chosen to join regardless: a fastDL host serving a different build than
    /// the server runs is the operator's mistake, not an attack - the operator picks both the
    /// hash and the host - and the wrong textures beat no content at all.
    pub enforce: bool,
}

const ZSTD_MAGIC: [u8; 4] = [0x28, 0xB5, 0x2F, 0xFD];
/// A zstd skippable frame, which is how a file written by pzstd starts: 0x184D2A50 to 0x184D2A5F,
/// little-endian.
const ZSTD_SKIPPABLE_MAGIC: [u8; 3] = [0x2A, 0x4D, 0x18];
const BZIP2_MAGIC: [u8; 3] = *b"BZh";

/// The largest zstd window a download may ask for, as a power of two: 128 MiB. That is zstd's
/// own default limit and what `--long=27` and `--ultra -22` produce. The window is memory the
/// decoder has to hold, so the file does not get to choose it freely.
const ZSTD_WINDOW_LOG_MAX: u32 = 27;

/// A decoder for the downloaded file at `packed`. Which kind is told from the file's first
/// bytes, not from what its host called it.
///
/// Both read every stream in the file, not just the first: parallel compressors (pbzip2, pzstd,
/// `zstd -T`) write several, and a single-stream decoder would silently stop early.
fn open_packed(packed: &Path, name: &str) -> Result<Box<dyn Read>, String> {
    let file = File::open(packed).map_err(|e| format!("Failed to open compressed temp: {}", e))?;
    let mut input = BufReader::new(file);
    let head = input
        .fill_buf()
        .map_err(|e| format!("Failed to read compressed temp: {}", e))?;
    let zstd_frame = head.starts_with(&ZSTD_MAGIC)
        || (head.len() >= 4 && head[0] & 0xF0 == 0x50 && head[1..4] == ZSTD_SKIPPABLE_MAGIC);
    if zstd_frame {
        let mut decoder = zstd::stream::read::Decoder::with_buffer(input)
            .map_err(|e| format!("zstd decompression failed for {}: {}", name, e))?;
        decoder
            .window_log_max(ZSTD_WINDOW_LOG_MAX)
            .map_err(|e| format!("zstd decompression failed for {}: {}", name, e))?;
        Ok(Box::new(decoder))
    } else if head.starts_with(&BZIP2_MAGIC) {
        Ok(Box::new(bzip2::read::MultiBzDecoder::new(input)))
    } else {
        Err(format!(
            "decompression failed for {}: the download is neither a zstd nor a bzip2 file",
            name
        ))
    }
}

/// Decompresses the zstd or bzip2 file `packed` into `out`, hashing what it writes, and returns the full hex
/// SHA-256. Fails once more than `max_bytes` come out (a decompression bomb), and - when
/// `expected` is given and enforced - if the result is not what the server advertised: an
/// advertised size is then both a tighter cap and an exact length to match. An `expected` that is
/// not enforced only logs the mismatch and keeps the file, leaving `max_bytes` as the one cap,
/// which is what CONNECT ANYWAY asks for. `on_chunk` sees the running total after every
/// chunk and can stop the work by returning an error. The caller removes `out` on failure.
pub(crate) fn decompress_verified(
    packed: &Path,
    out: &Path,
    name: &str,
    max_bytes: u64,
    expected: Option<&Expected>,
    mut on_chunk: impl FnMut(u64) -> Result<(), String>,
) -> Result<String, String> {
    // Only an enforced size doubles as a cap; a tolerated one must not turn "bigger than
    // advertised" into a failure through the back door.
    let advertised_size = expected
        .filter(|e| e.enforce)
        .and_then(|e| e.size)
        .filter(|&s| s <= max_bytes);
    let limit = advertised_size.unwrap_or(max_bytes);
    let mut decoder = open_packed(packed, name)?;
    let mut output = BufWriter::new(
        File::create(out).map_err(|e| format!("Failed to create {}: {}", out.display(), e))?,
    );
    let mut hasher = Sha256::new();
    let mut buf = vec![0u8; 256 * 1024];
    let mut written: u64 = 0;
    loop {
        let n = decoder
            .read(&mut buf)
            .map_err(|e| format!("decompression failed for {}: {}", name, e))?;
        if n == 0 {
            break;
        }
        written += n as u64;
        if written > limit {
            return Err(match advertised_size {
                Some(size) => format!(
                    "downloaded {} does not match the version the server runs (it is larger than the {} bytes advertised)",
                    name, size
                ),
                None => format!(
                    "decompressed payload for {} exceeds maximum size ({} bytes)",
                    name, max_bytes
                ),
            });
        }
        hasher.update(&buf[..n]);
        output
            .write_all(&buf[..n])
            .map_err(|e| format!("Write error: {}", e))?;
        on_chunk(written)?;
    }
    output.flush().map_err(|e| format!("Flush error: {}", e))?;

    let sha = format!("{:x}", hasher.finalize());
    if let Some(expected) = expected {
        let wanted = expected.sha256.to_ascii_lowercase();
        if expected.size.is_some_and(|size| size != written) || !sha.starts_with(&wanted) {
            let mismatch = format!(
                "downloaded {} does not match the version the server runs (got {} bytes with hash {}; expected {} with hash {})",
                name,
                written,
                &sha[..wanted.len().min(sha.len())],
                expected
                    .size
                    .map_or_else(|| "any size".to_string(), |size| format!("{} bytes", size)),
                wanted
            );
            if expected.enforce {
                return Err(mismatch);
            }
            eprintln!("[content] keeping it anyway at the player's request: {mismatch}");
        }
    }
    Ok(sha)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    /// A fresh directory under the system temp dir, removed when dropped.
    struct TempDir(PathBuf);
    impl TempDir {
        fn new() -> Self {
            static NEXT: AtomicU32 = AtomicU32::new(0);
            let dir = std::env::temp_dir().join(format!(
                "dw-launcher-test-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            std::fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }
        fn path(&self, name: &str) -> PathBuf {
            self.0.join(name)
        }
    }
    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn bz2(data: &[u8]) -> Vec<u8> {
        let mut encoder = bzip2::write::BzEncoder::new(Vec::new(), bzip2::Compression::default());
        encoder.write_all(data).unwrap();
        encoder.finish().unwrap()
    }

    fn zst(data: &[u8]) -> Vec<u8> {
        zstd::stream::encode_all(data, 3).unwrap()
    }

    /// zstd with a window of `1 << window_log` bytes, as `zstd --long=<window_log>` writes it.
    fn zst_long(data: &[u8], window_log: u32) -> Vec<u8> {
        use zstd::zstd_safe::CParameter;
        let mut encoder = zstd::stream::write::Encoder::new(Vec::new(), 3).unwrap();
        encoder.set_parameter(CParameter::EnableLongDistanceMatching(true)).unwrap();
        encoder.set_parameter(CParameter::WindowLog(window_log)).unwrap();
        encoder.write_all(data).unwrap();
        encoder.finish().unwrap()
    }

    fn sha(data: &[u8]) -> String {
        format!("{:x}", Sha256::digest(data))
    }

    fn unpack(dir: &TempDir, bz2_bytes: Vec<u8>, max: u64, expected: Option<&Expected>) -> Result<String, String> {
        std::fs::write(dir.path("in.bz2"), bz2_bytes).unwrap();
        decompress_verified(&dir.path("in.bz2"), &dir.path("out.vpk"), "test", max, expected, |_| Ok(()))
    }

    fn expect(sha256: &str, size: Option<u64>) -> Expected {
        Expected { sha256: sha256.to_string(), size, enforce: true }
    }

    /// The same advertisement, after the player chose to join regardless.
    fn tolerate(sha256: &str, size: Option<u64>) -> Expected {
        Expected { enforce: false, ..expect(sha256, size) }
    }

    #[test]
    fn hashes_known_input() {
        let dir = TempDir::new();
        std::fs::write(dir.path("abc"), b"abc").unwrap();
        assert_eq!(
            sha256_file(&dir.path("abc")).unwrap(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn cache_rehashes_a_changed_file_and_knows_missing_ones() {
        let dir = TempDir::new();
        let mut cache = HashCache::load_from(dir.path("hashes.json"));
        let file = dir.path("turbo.vpk");
        assert_eq!(cache.sha256(&file, |_| {}).unwrap(), None);

        std::fs::write(&file, [1, 2, 3]).unwrap();
        assert_eq!(cache.sha256(&file, |_| {}).unwrap(), Some(sha(&[1, 2, 3])));
        std::fs::write(&file, [4, 5, 6, 7]).unwrap();
        assert_eq!(cache.sha256(&file, |_| {}).unwrap(), Some(sha(&[4, 5, 6, 7])));
    }

    #[test]
    fn cache_is_reused_after_a_restart() {
        let dir = TempDir::new();
        let file = dir.path("map.vpk");
        std::fs::write(&file, [9, 9, 9]).unwrap();

        let mut cache = HashCache::load_from(dir.path("hashes.json"));
        cache.sha256(&file, |_| {}).unwrap();
        // Record a different hash for the same size and time, then reload: if the saved cache is
        // honored, the planted value comes back without the file being read.
        cache.record(&file, "planted");
        cache.save();

        let mut reloaded = HashCache::load_from(dir.path("hashes.json"));
        assert_eq!(reloaded.sha256(&file, |_| {}).unwrap().as_deref(), Some("planted"));
    }

    #[test]
    fn decompresses_and_verifies() {
        let dir = TempDir::new();
        let data = b"VPK payload".repeat(1000);
        let wanted = expect(&sha(&data)[..16], Some(data.len() as u64));
        let full = unpack(&dir, bz2(&data), 1 << 20, Some(&wanted)).unwrap();
        assert_eq!(full, sha(&data));
        assert_eq!(std::fs::read(dir.path("out.vpk")).unwrap(), data);
    }

    #[test]
    fn rejects_content_that_does_not_match_the_advertised_hash() {
        let dir = TempDir::new();
        let wanted = expect("0123456789abcdef", None);
        let err = unpack(&dir, bz2(b"not what the server runs"), 1 << 20, Some(&wanted)).unwrap_err();
        assert!(err.contains("does not match"), "{err}");
    }

    #[test]
    fn rejects_content_of_another_size_even_with_the_right_hash() {
        let dir = TempDir::new();
        let data = b"exactly this".to_vec();
        let short = expect(&sha(&data)[..16], Some(data.len() as u64 + 1));
        let err = unpack(&dir, bz2(&data), 1 << 20, Some(&short)).unwrap_err();
        assert!(err.contains("does not match"), "{err}");
    }

    #[test]
    fn stops_at_the_advertised_size_rather_than_the_general_cap() {
        let dir = TempDir::new();
        let wanted = expect("0123456789abcdef", Some(1024));
        let err = unpack(&dir, bz2(&vec![1u8; 1 << 20]), 1 << 30, Some(&wanted)).unwrap_err();
        assert!(err.contains("larger than the 1024 bytes advertised"), "{err}");
    }

    #[test]
    fn keeps_content_that_does_not_match_once_the_player_has_accepted_it() {
        let dir = TempDir::new();
        let data = b"the build the fastdl host actually serves".to_vec();
        let stale = tolerate("0123456789abcdef", Some(data.len() as u64));
        assert_eq!(unpack(&dir, bz2(&data), 1 << 20, Some(&stale)).unwrap(), sha(&data));
        assert_eq!(std::fs::read(dir.path("out.vpk")).unwrap(), data);
    }

    /// The advertised size is a cap only while the hash is enforced, or tolerating a mismatch
    /// would still reject every file that came out bigger than advertised.
    #[test]
    fn a_tolerated_size_does_not_cap_the_download() {
        let dir = TempDir::new();
        let stale = tolerate("0123456789abcdef", Some(1024));
        unpack(&dir, bz2(&vec![1u8; 1 << 20]), 1 << 30, Some(&stale)).unwrap();
        assert_eq!(std::fs::metadata(dir.path("out.vpk")).unwrap().len(), 1 << 20);
    }

    /// Tolerating a mismatch is not tolerating a bomb: the hard cap still applies.
    #[test]
    fn a_tolerated_mismatch_still_stops_at_the_hard_cap() {
        let dir = TempDir::new();
        let stale = tolerate("0123456789abcdef", Some(1024));
        let err = unpack(&dir, bz2(&vec![0u8; 4 << 20]), 64 << 10, Some(&stale)).unwrap_err();
        assert!(err.contains("exceeds maximum size"), "{err}");
    }

    #[test]
    fn stops_a_decompression_bomb() {
        let dir = TempDir::new();
        let err = unpack(&dir, bz2(&vec![0u8; 4 << 20]), 64 << 10, None).unwrap_err();
        assert!(err.contains("exceeds maximum size"), "{err}");
    }

    #[test]
    fn unpacks_zstd_checked_like_any_other_download() {
        let dir = TempDir::new();
        let data = b"the build the server runs".repeat(4096);
        let wanted = expect(&sha(&data)[..16], Some(data.len() as u64));
        assert_eq!(unpack(&dir, zst(&data), 1 << 20, Some(&wanted)).unwrap(), sha(&data));
        assert_eq!(std::fs::read(dir.path("out.vpk")).unwrap(), data);

        let err = unpack(&dir, zst(b"not what the server runs"), 1 << 20, Some(&wanted)).unwrap_err();
        assert!(err.contains("does not match the version the server runs"), "{err}");
    }

    #[test]
    fn stops_a_zstd_decompression_bomb() {
        let dir = TempDir::new();
        // 64 MiB of zeros is a few kilobytes of zstd.
        let bomb = zst(&vec![0u8; 64 << 20]);
        assert!(bomb.len() < 64 << 10, "{}", bomb.len());
        let err = unpack(&dir, bomb, 1 << 20, None).unwrap_err();
        assert!(err.contains("exceeds maximum size"), "{err}");
    }

    #[test]
    fn reads_every_frame_of_a_multiframe_zstd_file() {
        let dir = TempDir::new();
        // A skippable frame first, the way pzstd starts a file, then two frames of data.
        let mut multi = vec![0x50, 0x2A, 0x4D, 0x18, 4, 0, 0, 0, 1, 2, 3, 4];
        multi.extend(zst(b"first frame "));
        multi.extend(zst(b"second frame"));
        assert_eq!(unpack(&dir, multi, 1 << 20, None).unwrap(), sha(b"first frame second frame"));
    }

    #[test]
    fn a_zstd_window_is_allowed_up_to_128_mib_and_no_further() {
        let dir = TempDir::new();
        let data = vec![9u8; 1 << 20];
        // --long=27, the most the documentation tells an operator to use.
        assert_eq!(unpack(&dir, zst_long(&data, 27), 1 << 30, None).unwrap(), sha(&data));
        // --long=28 would have the decoder hold 256 MiB on the file's say-so.
        let err = unpack(&dir, zst_long(&data, 28), 1 << 30, None).unwrap_err();
        assert!(err.contains("decompression failed"), "{err}");
        assert_eq!(crate::addons::error_code(&err), 4);
    }

    #[test]
    fn a_download_that_is_neither_format_is_refused() {
        let dir = TempDir::new();
        for junk in [&b"<html>404 not found</html>"[..], &b"4\x12\xaaU raw vpk"[..], &b""[..]] {
            let err = unpack(&dir, junk.to_vec(), 1 << 20, None).unwrap_err();
            assert!(err.contains("neither a zstd nor a bzip2 file"), "{err}");
            assert_eq!(crate::addons::error_code(&err), 4);
        }
    }

    #[test]
    fn reads_every_stream_of_a_parallel_compressor() {
        let dir = TempDir::new();
        let mut multi = bz2(b"first stream ");
        multi.extend(bz2(b"second stream"));
        unpack(&dir, multi, 1 << 20, None).unwrap();
        assert_eq!(std::fs::read(dir.path("out.vpk")).unwrap(), b"first stream second stream");
    }

    #[test]
    fn the_chunk_callback_can_stop_the_work() {
        let dir = TempDir::new();
        std::fs::write(dir.path("in.bz2"), bz2(&vec![7u8; 1 << 20])).unwrap();
        let err = decompress_verified(&dir.path("in.bz2"), &dir.path("out.vpk"), "test", 1 << 30, None, |_| {
            Err("CANCELLED".into())
        })
        .unwrap_err();
        assert_eq!(err, "CANCELLED");
    }
}
