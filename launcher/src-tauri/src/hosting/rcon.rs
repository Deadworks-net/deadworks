//! Source RCON, used to talk to servers running under Wine.
//!
//! On Windows commands are typed into the server's own console. Under Wine the server's stdin is
//! never a console (the engine complains about that once per tick and ignores it), so the Linux
//! path follows the Docker image instead: `-usercon` plus an `rcon_password`, and each command is
//! one short RCON session on the game port. The reply carries the command's output, which the
//! caller feeds back into the console log.

use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::time::{Duration, Instant};

const AUTH: i32 = 3;
const AUTH_RESPONSE: i32 = 2;
const EXEC: i32 = 2;
const RESPONSE_VALUE: i32 = 0;
/// Longest a reply may take to start, and the quiet gap that ends a multi-packet reply.
const FIRST_REPLY: Duration = Duration::from_secs(3);
const QUIET: Duration = Duration::from_millis(250);
const MAX_PACKET: usize = 1 << 20;

fn packet(id: i32, kind: i32, body: &str) -> Vec<u8> {
    let len = 4 + 4 + body.len() + 2;
    let mut out = Vec::with_capacity(4 + len);
    out.extend_from_slice(&(len as i32).to_le_bytes());
    out.extend_from_slice(&id.to_le_bytes());
    out.extend_from_slice(&kind.to_le_bytes());
    out.extend_from_slice(body.as_bytes());
    out.extend_from_slice(&[0, 0]);
    out
}

/// One packet, or None when nothing arrived before the socket's read timeout.
fn read_packet(stream: &mut TcpStream) -> std::io::Result<Option<(i32, i32, String)>> {
    let mut len = [0u8; 4];
    match stream.read_exact(&mut len) {
        Ok(()) => {}
        Err(e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => return Ok(None),
        Err(e) => return Err(e),
    }
    let len = i32::from_le_bytes(len) as usize;
    if !(10..=MAX_PACKET).contains(&len) {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "bad RCON packet size"));
    }
    let mut buf = vec![0u8; len];
    stream.read_exact(&mut buf)?;
    let id = i32::from_le_bytes(buf[0..4].try_into().unwrap());
    let kind = i32::from_le_bytes(buf[4..8].try_into().unwrap());
    let body = String::from_utf8_lossy(&buf[8..len - 2]).into_owned();
    Ok(Some((id, kind, body)))
}

/// Run one command and return what it printed.
pub fn exec(addr: SocketAddr, password: &str, command: &str) -> Result<String, String> {
    let io = |e: std::io::Error| format!("Couldn't reach the server console: {e}");
    let mut stream = TcpStream::connect_timeout(&addr, Duration::from_secs(2)).map_err(io)?;
    stream.set_nodelay(true).ok();
    stream.set_read_timeout(Some(FIRST_REPLY)).map_err(io)?;
    stream.set_write_timeout(Some(Duration::from_secs(2))).map_err(io)?;

    stream.write_all(&packet(1, AUTH, password)).map_err(io)?;
    loop {
        match read_packet(&mut stream).map_err(io)? {
            None => return Err("The server console didn't answer.".into()),
            // Servers send an empty RESPONSE_VALUE before the real answer.
            Some((_, kind, _)) if kind != AUTH_RESPONSE => continue,
            Some((-1, _, _)) => return Err("The server refused the console password.".into()),
            Some(_) => break,
        }
    }

    stream.write_all(&packet(2, EXEC, command)).map_err(io)?;
    let mut out = String::new();
    let started = Instant::now();
    let mut got_any = false;
    loop {
        // After the first packet, a short silence means the reply is complete.
        stream.set_read_timeout(Some(if got_any { QUIET } else { FIRST_REPLY })).map_err(io)?;
        match read_packet(&mut stream) {
            Ok(Some((_, RESPONSE_VALUE, body))) => {
                got_any = true;
                out.push_str(&body);
            }
            Ok(Some(_)) => {}
            Ok(None) => break,
            // `quit` closes the connection instead of answering.
            Err(_) => break,
        }
        if started.elapsed() > Duration::from_secs(10) {
            break;
        }
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;

    /// A minimal RCON server: checks the password, answers one command in two packets.
    fn fake_server(password: &'static str) -> SocketAddr {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        std::thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                let mut s = stream;
                s.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
                let Ok(Some((id, kind, body))) = read_packet(&mut s) else { continue };
                assert_eq!(kind, AUTH);
                s.write_all(&packet(id, RESPONSE_VALUE, "")).unwrap();
                s.write_all(&packet(if body == password { id } else { -1 }, AUTH_RESPONSE, "")).unwrap();
                if body != password {
                    continue;
                }
                let Ok(Some((id, _, cmd))) = read_packet(&mut s) else { continue };
                s.write_all(&packet(id, RESPONSE_VALUE, &format!("ran {cmd}\n"))).unwrap();
                s.write_all(&packet(id, RESPONSE_VALUE, "second part\n")).unwrap();
            }
        });
        addr
    }

    #[test]
    fn runs_a_command_and_joins_a_split_reply() {
        let addr = fake_server("secret");
        assert_eq!(exec(addr, "secret", "status").unwrap(), "ran status\nsecond part\n");
    }

    #[test]
    fn a_wrong_password_is_reported_as_such() {
        let addr = fake_server("secret");
        assert_eq!(exec(addr, "nope", "status").unwrap_err(), "The server refused the console password.");
    }
}
