//! Network-mode specifics: the per-server gameinfo.gi and local addresses.
//!
//! Steam Datagram Relay needs two gameinfo.gi edits on the *server's* copy
//! only (verified: clients connect with `connect [A:1:...]` untouched):
//! `CreateListenSocketP2P 2` in `NetworkSystem` and
//! `"net_p2p_listen_dedicated" "1"` in `ConVars`. The stock file already has
//! the `rate` block the community guide also mentions.

use std::net::{IpAddr, UdpSocket};

use crate::gameinfo::{first_token, indent_of, line_of_offset, locate_block, newline_for};

use super::types::NetworkMode;

/// The server's gameinfo.gi for `mode`, rendered from Steam's original.
pub fn render_gameinfo(vanilla: &str, mode: NetworkMode) -> Result<String, String> {
    match mode {
        NetworkMode::Sdr => {
            let s = set_entry(vanilla, "NetworkSystem", "CreateListenSocketP2P", "CreateListenSocketP2P\t2")?;
            set_entry(&s, "ConVars", "net_p2p_listen_dedicated", "\"net_p2p_listen_dedicated\"\t\"1\"")
        }
        NetworkMode::PortForward | NetworkMode::Lan => Ok(vanilla.to_string()),
    }
}

/// Make `line` the first entry of `block`, replacing any existing `key` entry.
fn set_entry(content: &str, block: &str, key: &str, line: &str) -> Result<String, String> {
    let located =
        locate_block(content, &[block]).ok_or_else(|| format!("gameinfo.gi has no {block} block; is the game install intact?"))?;
    let src: Vec<&str> = content.split_inclusive('\n').collect();
    let open = line_of_offset(&src, located.open);
    let close = line_of_offset(&src, located.close);
    if close <= open {
        return Err(format!("gameinfo.gi's {block} block is on one line; refusing to edit"));
    }
    let nl = newline_for(src[open], content);
    let indent = format!("{}\t", indent_of(src[open]).unwrap_or_default());
    let mut out: Vec<String> = Vec::with_capacity(src.len() + 1);
    for (i, l) in src.iter().enumerate() {
        if i > open && i < close && first_token(l).is_some_and(|k| k.eq_ignore_ascii_case(key)) {
            continue;
        }
        out.push((*l).to_string());
        if i == open {
            if !out.last().is_some_and(|s| s.ends_with('\n')) {
                out.last_mut().unwrap().push_str(nl);
            }
            out.push(format!("{indent}{line}{nl}"));
        }
    }
    Ok(out.concat())
}

/// This PC's address on the local network (the interface the default route
/// uses). No packet is sent; connecting a UDP socket only picks a route.
pub fn primary_lan_ip() -> Option<IpAddr> {
    let sock = UdpSocket::bind("0.0.0.0:0").ok()?;
    sock.connect("192.0.2.1:9").ok()?;
    let ip = sock.local_addr().ok()?.ip();
    (!ip.is_unspecified() && !ip.is_loopback()).then_some(ip)
}

/// A port nothing on this PC is using for UDP or TCP right now.
pub fn port_free(port: u16) -> bool {
    !super::firewall::port_in_use(port)
}

#[cfg(test)]
mod tests {
    use super::*;

    const GI: &str = "\"GameInfo\"\r\n{\r\n\tNetworkSystem\r\n\t{\r\n\t\tBetaUniverse\r\n\t\t{\r\n\t\t\tFakeLag\t40\r\n\t\t}\r\n\t}\r\n\tConVars\r\n\t{\t \r\n\t\t\"rate\"\r\n\t\t{\r\n\t\t\t\"min\"\t\"98304\"\r\n\t\t}\r\n\t\t\"sv_minrate\"\t\"98304\"\r\n\t}\r\n}\r\n";

    #[test]
    fn sdr_adds_both_entries_once() {
        let out = render_gameinfo(GI, NetworkMode::Sdr).unwrap();
        assert!(out.contains("\tNetworkSystem\r\n\t{\r\n\t\tCreateListenSocketP2P\t2\r\n\t\tBetaUniverse"));
        assert!(out.contains("\tConVars\r\n\t{\t \r\n\t\t\"net_p2p_listen_dedicated\"\t\"1\"\r\n\t\t\"rate\""));
        assert_eq!(render_gameinfo(&out, NetworkMode::Sdr).unwrap(), out, "idempotent");
        assert!(!out.contains("\n\n") && out.matches('\n').count() == out.matches("\r\n").count());
    }

    #[test]
    fn other_modes_keep_steams_file() {
        assert_eq!(render_gameinfo(GI, NetworkMode::PortForward).unwrap(), GI);
        assert_eq!(render_gameinfo(GI, NetworkMode::Lan).unwrap(), GI);
    }

    #[test]
    fn existing_value_is_replaced_not_duplicated() {
        let odd = GI.replace("\t\tBetaUniverse", "\t\tCreateListenSocketP2P\t0\r\n\t\tBetaUniverse");
        let out = render_gameinfo(&odd, NetworkMode::Sdr).unwrap();
        assert_eq!(out.matches("CreateListenSocketP2P").count(), 1);
        assert!(out.contains("CreateListenSocketP2P\t2"));
    }
}
