//! Just enough of the Source server query protocol to read a server's rules (A2S_RULES), which is
//! where a Deadworks server advertises the content it runs. Steam answers these on the game port.

use std::collections::HashMap;
use std::net::{ToSocketAddrs, UdpSocket};
use std::time::Duration;

const SINGLE_PACKET: [u8; 4] = [0xFF, 0xFF, 0xFF, 0xFF];
const SPLIT_PACKET: [u8; 4] = [0xFE, 0xFF, 0xFF, 0xFF];
const A2S_RULES: u8 = b'V';
const S2C_CHALLENGE: u8 = b'A';
const S2A_RULES: u8 = b'E';
/// How many times a request is sent before the server counts as not answering. The whole wait
/// is split between them.
const SENDS: u32 = 3;

#[derive(Debug, PartialEq)]
enum Reply {
    Challenge([u8; 4]),
    Rules(HashMap<String, String>),
}

/// Asks `addr` for its rules; a bare host gets the default port, as `ping_server` does.
/// Blocking: call it from `spawn_blocking`.
pub fn query_rules(addr: &str, wait: Duration, require_challenge: bool) -> Result<HashMap<String, String>, String> {
    let addr = if addr.contains(':') {
        addr.to_string()
    } else {
        format!("{}:27015", addr)
    };
    let target = addr
        .to_socket_addrs()
        .map_err(|e| format!("Could not resolve {}: {}", addr, e))?
        .next()
        .ok_or_else(|| format!("Could not resolve {}", addr))?;
    let socket = UdpSocket::bind(if target.is_ipv4() { "0.0.0.0:0" } else { "[::]:0" })
        .map_err(|e| format!("UDP bind failed: {}", e))?;
    socket
        .set_read_timeout(Some(wait / SENDS))
        .map_err(|e| format!("UDP setup failed: {}", e))?;
    socket
        .connect(target)
        .map_err(|e| format!("UDP connect to {} failed: {}", addr, e))?;

    // A server may answer the first request with a challenge instead; echo it back once. UDP
    // loses packets, and a query that got no answer is indistinguishable from a server that
    // advertises nothing, so each request is sent again before giving up.
    let mut challenge = [0xFF; 4];
    let mut challenged = false;
    let mut sends_left = SENDS;
    let mut buf = [0u8; 2048];
    loop {
        let mut request = SINGLE_PACKET.to_vec();
        request.push(A2S_RULES);
        request.extend_from_slice(&challenge);
        socket
            .send(&request)
            .map_err(|e| format!("A2S_RULES send to {} failed: {}", addr, e))?;
        let n = match socket.recv(&mut buf) {
            Ok(n) => n,
            Err(e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => {
                sends_left -= 1;
                if sends_left == 0 {
                    return Err(format!("No A2S_RULES reply from {}: {}", addr, e));
                }
                continue;
            }
            Err(e) => return Err(format!("No A2S_RULES reply from {}: {}", addr, e)),
        };
        match parse_reply(&buf[..n])? {
            // Steam answers a stranger with a challenge first. Rules that arrive without one
            // came from someone who did not have to see our request to send them: a forged
            // source address is all it takes. Thrown away, and the question asked again.
            Reply::Rules(_) if require_challenge && !challenged => {
                sends_left -= 1;
                if sends_left == 0 {
                    return Err(format!("{} answered A2S_RULES without the challenge Steam requires", addr));
                }
            }
            Reply::Rules(rules) => return Ok(rules),
            Reply::Challenge(_) if challenged => {
                return Err(format!("{} kept answering A2S_RULES with a new challenge", addr));
            }
            Reply::Challenge(c) => {
                challenge = c;
                challenged = true;
            }
        }
    }
}

fn parse_reply(packet: &[u8]) -> Result<Reply, String> {
    if packet.starts_with(&SPLIT_PACKET) {
        // A Deadworks server keeps its rules well inside one datagram.
        return Err("A2S_RULES reply was split across packets, which is not supported".into());
    }
    let body = packet
        .strip_prefix(&SINGLE_PACKET[..])
        .ok_or("Not an A2S reply")?;
    match body.split_first() {
        Some((&kind, rest)) if kind == S2C_CHALLENGE && rest.len() >= 4 => {
            Ok(Reply::Challenge([rest[0], rest[1], rest[2], rest[3]]))
        }
        Some((&kind, rest)) if kind == S2A_RULES => parse_rules(rest).map(Reply::Rules),
        Some((&kind, _)) => Err(format!("Unexpected A2S reply type 0x{:02X}", kind)),
        None => Err("Empty A2S reply".into()),
    }
}

fn parse_rules(body: &[u8]) -> Result<HashMap<String, String>, String> {
    if body.len() < 2 {
        return Err("Truncated A2S_RULES reply".into());
    }
    let count = u16::from_le_bytes([body[0], body[1]]);
    let mut rest = &body[2..];
    let mut rules = HashMap::with_capacity(count as usize);
    for _ in 0..count {
        let key = take_cstr(&mut rest)?;
        let value = take_cstr(&mut rest)?;
        rules.insert(key, value);
    }
    Ok(rules)
}

fn take_cstr(rest: &mut &[u8]) -> Result<String, String> {
    let end = rest
        .iter()
        .position(|&b| b == 0)
        .ok_or("Truncated A2S_RULES reply")?;
    let s = String::from_utf8_lossy(&rest[..end]).into_owned();
    *rest = &rest[end + 1..];
    Ok(s)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rules_packet(pairs: &[(&str, &str)]) -> Vec<u8> {
        let mut p = SINGLE_PACKET.to_vec();
        p.push(S2A_RULES);
        p.extend_from_slice(&(pairs.len() as u16).to_le_bytes());
        for (k, v) in pairs {
            p.extend_from_slice(k.as_bytes());
            p.push(0);
            p.extend_from_slice(v.as_bytes());
            p.push(0);
        }
        p
    }

    #[test]
    fn parses_a_rules_reply() {
        let packet = rules_packet(&[("dw_ver", "1"), ("dw_addons", "turbo:9330149c74886724")]);
        let Reply::Rules(rules) = parse_reply(&packet).unwrap() else {
            panic!("expected rules")
        };
        assert_eq!(rules["dw_ver"], "1");
        assert_eq!(rules["dw_addons"], "turbo:9330149c74886724");
    }

    #[test]
    fn parses_a_challenge() {
        let packet = [0xFF, 0xFF, 0xFF, 0xFF, b'A', 1, 2, 3, 4];
        assert_eq!(parse_reply(&packet).unwrap(), Reply::Challenge([1, 2, 3, 4]));
    }

    #[test]
    fn rejects_truncated_and_foreign_replies() {
        let mut packet = rules_packet(&[("dw_ver", "1")]);
        packet.pop(); // drop the value's terminator
        assert!(parse_reply(&packet).is_err());
        assert!(parse_reply(&[0xFE, 0xFF, 0xFF, 0xFF, 0, 0]).is_err());
        assert!(parse_reply(b"HTTP/1.1 200 OK").is_err());
        assert!(parse_reply(&[0xFF, 0xFF, 0xFF, 0xFF, b'I']).is_err());
    }

    #[test]
    fn answers_a_challenge_then_reads_the_rules() {
        let server = UdpSocket::bind("127.0.0.1:0").unwrap();
        let addr = server.local_addr().unwrap().to_string();
        let responder = std::thread::spawn(move || {
            let mut buf = [0u8; 64];
            let (n, from) = server.recv_from(&mut buf).unwrap();
            assert_eq!(&buf[..n], &[0xFF, 0xFF, 0xFF, 0xFF, b'V', 0xFF, 0xFF, 0xFF, 0xFF]);
            server.send_to(&[0xFF, 0xFF, 0xFF, 0xFF, b'A', 9, 8, 7, 6], from).unwrap();

            let (n, from) = server.recv_from(&mut buf).unwrap();
            assert_eq!(&buf[..n], &[0xFF, 0xFF, 0xFF, 0xFF, b'V', 9, 8, 7, 6]);
            server.send_to(&rules_packet(&[("dw_ver", "1")]), from).unwrap();
        });

        let rules = query_rules(&addr, Duration::from_secs(2), true).unwrap();
        responder.join().unwrap();
        assert_eq!(rules["dw_ver"], "1");
    }

    #[test]
    fn rules_without_a_challenge_are_not_believed_from_a_stranger() {
        let server = UdpSocket::bind("127.0.0.1:0").unwrap();
        let addr = server.local_addr().unwrap().to_string();
        let responder = std::thread::spawn(move || {
            let mut buf = [0u8; 64];
            // Answers every request with rules straight away, as a forged reply would.
            server.set_read_timeout(Some(Duration::from_millis(1500))).unwrap();
            while let Ok((_, from)) = server.recv_from(&mut buf) {
                server.send_to(&rules_packet(&[("dw_ver", "1"), ("dw_fastdl", "http://evil.example")]), from).unwrap();
            }
        });

        assert!(query_rules(&addr, Duration::from_millis(600), true).unwrap_err().contains("challenge"));
        assert_eq!(query_rules(&addr, Duration::from_millis(600), false).unwrap()["dw_ver"], "1");
        responder.join().unwrap();
    }

    #[test]
    fn asks_again_when_a_request_is_lost() {
        let server = UdpSocket::bind("127.0.0.1:0").unwrap();
        let addr = server.local_addr().unwrap().to_string();
        let responder = std::thread::spawn(move || {
            let mut buf = [0u8; 64];
            // The first request is dropped on the floor; the second is answered.
            server.recv_from(&mut buf).unwrap();
            let (_, from) = server.recv_from(&mut buf).unwrap();
            server.send_to(&rules_packet(&[("dw_ver", "1")]), from).unwrap();
        });

        let rules = query_rules(&addr, Duration::from_millis(900), false).unwrap();
        responder.join().unwrap();
        assert_eq!(rules["dw_ver"], "1");
    }

    #[test]
    fn times_out_against_a_silent_server() {
        let silent = UdpSocket::bind("127.0.0.1:0").unwrap();
        let addr = silent.local_addr().unwrap().to_string();
        assert!(query_rules(&addr, Duration::from_millis(200), true).is_err());
    }
}
