use std::net::UdpSocket;
use std::time::{Duration, Instant};

// A2S_INFO request: header (0xFFFFFFFF) + 'T' + "Source Engine Query\0"
const A2S_INFO: &[u8] = b"\xFF\xFF\xFF\xFF\x54Source Engine Query\x00";

#[tauri::command]
pub async fn ping_server(addr: String) -> i32 {
    tokio::task::spawn_blocking(move || {
        let addr_with_port = if addr.contains(':') {
            addr.clone()
        } else {
            format!("{}:27015", addr)
        };

        let socket = match UdpSocket::bind("0.0.0.0:0") {
            Ok(s) => s,
            Err(_) => return -1,
        };
        socket.set_read_timeout(Some(Duration::from_secs(3))).ok();

        if socket.send_to(A2S_INFO, &addr_with_port).is_err() {
            return -1;
        }

        let start = Instant::now();
        let mut buf = [0u8; 1400];

        // Server may respond with S2C_CHALLENGE — resend with the challenge token appended
        match socket.recv_from(&mut buf) {
            Ok((len, _)) if len >= 5 && buf[4] == 0x41 => {
                let elapsed = start.elapsed();
                // 0x41 = challenge response, resend with challenge
                let mut retry = Vec::with_capacity(A2S_INFO.len() + 4);
                retry.extend_from_slice(A2S_INFO);
                retry.extend_from_slice(&buf[5..9]);
                if socket.send_to(&retry, &addr_with_port).is_err() {
                    return -1;
                }
                match socket.recv_from(&mut buf) {
                    Ok(_) => elapsed.as_millis() as i32,
                    Err(_) => -1,
                }
            }
            Ok(_) => start.elapsed().as_millis() as i32,
            Err(_) => -1,
        }
    })
    .await
    .unwrap_or(-1)
}

/// What a server says about itself in A2S_INFO: enough to name it to the player before a join.
#[derive(Debug, PartialEq, serde::Serialize)]
pub struct ServerInfo {
    pub name: String,
    pub map: String,
    pub players: u8,
    pub max_players: u8,
}

const S2A_INFO: u8 = 0x49; // 'I'
const S2C_CHALLENGE: u8 = 0x41; // 'A'

/// A zero-terminated string at `at`, and the index after its terminator.
fn cstring(packet: &[u8], at: usize) -> Option<(String, usize)> {
    let end = at + packet.get(at..)?.iter().position(|&b| b == 0)?;
    // A name is shown to the player, so nothing that is not printable gets through.
    let text: String = String::from_utf8_lossy(&packet[at..end]).chars().filter(|c| !c.is_control()).take(255).collect();
    Some((text, end + 1))
}

/// Header, 'I', protocol byte, then name, map, folder and game as strings, the app id (2 bytes),
/// and one byte each for players, slots and bots. Bots are not players.
fn parse_info(packet: &[u8]) -> Option<ServerInfo> {
    if packet.len() < 6 || packet[..4] != [0xFF; 4] || packet[4] != S2A_INFO {
        return None;
    }
    let (name, at) = cstring(packet, 6)?;
    let (map, at) = cstring(packet, at)?;
    let (_folder, at) = cstring(packet, at)?;
    let (_game, at) = cstring(packet, at)?;
    let counts = packet.get(at + 2..at + 5)?;
    Some(ServerInfo { name, map, players: counts[0].saturating_sub(counts[2]), max_players: counts[1] })
}

fn query_info(addr: &str, wait: Duration) -> Option<ServerInfo> {
    let socket = UdpSocket::bind("0.0.0.0:0").ok()?;
    socket.connect(addr).ok()?;
    socket.set_read_timeout(Some(wait)).ok()?;
    let mut request = A2S_INFO.to_vec();
    let mut buf = [0u8; 1400];
    // Once as asked, once more with the challenge Steam answers a stranger with, and one resend
    // in case either was lost.
    for _ in 0..3 {
        socket.send(&request).ok()?;
        let Ok(len) = socket.recv(&mut buf) else { continue };
        if len >= 9 && buf[4] == S2C_CHALLENGE {
            request = A2S_INFO.to_vec();
            request.extend_from_slice(&buf[5..9]);
            continue;
        }
        return parse_info(&buf[..len]);
    }
    None
}

/// Asks the server at `addr` (an `ip:port`) who it is. An error when nothing answers, which is
/// also what a server started with -nomaster looks like.
#[tauri::command]
pub async fn server_info(addr: String) -> Result<ServerInfo, String> {
    if !crate::deep_link::is_valid_ip_port(&addr) {
        return Err(format!("{addr} is not an ip:port address"));
    }
    let target = addr.clone();
    tokio::task::spawn_blocking(move || query_info(&target, Duration::from_millis(1200)))
        .await
        .ok()
        .flatten()
        .ok_or_else(|| format!("No server answered at {addr}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn info_packet(name: &str, map: &str, players: u8, max: u8, bots: u8) -> Vec<u8> {
        let mut p = vec![0xFF, 0xFF, 0xFF, 0xFF, S2A_INFO, 17];
        for text in [name, map, "citadel", "Deadlock"] {
            p.extend_from_slice(text.as_bytes());
            p.push(0);
        }
        p.extend_from_slice(&[0x02, 0x00, players, max, bots, b'd', b'w', 0, 1]);
        p
    }

    #[test]
    fn reads_what_a_server_says_about_itself() {
        let info = parse_info(&info_packet("Ware \u{1F3AE} night", "dl_midtown", 9, 24, 2)).unwrap();
        assert_eq!(info, ServerInfo { name: "Ware \u{1F3AE} night".into(), map: "dl_midtown".into(), players: 7, max_players: 24 });
    }

    #[test]
    fn a_name_cannot_carry_control_characters_or_run_on() {
        let long = format!("line one\nline two\u{7}{}", "x".repeat(600));
        let info = parse_info(&info_packet(&long, "m", 0, 12, 0)).unwrap();
        assert!(info.name.starts_with("line oneline two") && info.name.chars().count() == 255, "{}", info.name.len());
    }

    #[test]
    fn rejects_replies_that_are_not_an_info_reply() {
        let good = info_packet("n", "m", 1, 2, 0);
        assert!(parse_info(&good[..good.len() - 6]).is_none()); // cut off before the counts
        assert!(parse_info(&[0xFF, 0xFF, 0xFF, 0xFF, 0x45, 0, 0]).is_none()); // a rules reply
        assert!(parse_info(b"HTTP/1.1 200 OK").is_none());
        assert!(parse_info(&[]).is_none());
        let mut unterminated = vec![0xFF, 0xFF, 0xFF, 0xFF, S2A_INFO, 17];
        unterminated.extend_from_slice(b"no terminator");
        assert!(parse_info(&unterminated).is_none());
    }

    #[test]
    fn answers_the_challenge_and_reads_the_reply() {
        let server = UdpSocket::bind("127.0.0.1:0").unwrap();
        let addr = server.local_addr().unwrap().to_string();
        let responder = std::thread::spawn(move || {
            let mut buf = [0u8; 64];
            let (n, from) = server.recv_from(&mut buf).unwrap();
            assert_eq!(&buf[..n], A2S_INFO);
            server.send_to(&[0xFF, 0xFF, 0xFF, 0xFF, S2C_CHALLENGE, 9, 8, 7, 6], from).unwrap();
            let (n, from) = server.recv_from(&mut buf).unwrap();
            assert_eq!(&buf[n - 4..n], &[9, 8, 7, 6]);
            server.send_to(&info_packet("Challenged", "dl_harbor", 3, 12, 0), from).unwrap();
        });
        let info = query_info(&addr, Duration::from_millis(800)).unwrap();
        responder.join().unwrap();
        assert_eq!(info.name, "Challenged");
    }

    #[test]
    fn a_silent_address_is_not_a_server() {
        let silent = UdpSocket::bind("127.0.0.1:0").unwrap();
        assert!(query_info(&silent.local_addr().unwrap().to_string(), Duration::from_millis(100)).is_none());
    }
}
