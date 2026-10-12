//! The HTTP client content is downloaded with, and where it may be pointed.
//!
//! A server names its own download host, so the URL is whatever a stranger chose. Left unchecked
//! it could aim the launcher at the player's own machine or network - including the loopback
//! bridge the in-game browser drives, where a plain GET starts an install. So unless the game
//! server is itself on the player's network, nothing here will fetch from a local address: not
//! as the URL given, not as a redirect, and not as what a public name turns out to resolve to.
//! That last one is settled before anything connects (see `PublicOnly`), so a name pointing at a
//! local address never receives the request at all.

use std::net::{IpAddr, Ipv4Addr, SocketAddr, ToSocketAddrs};
use std::sync::{Arc, OnceLock};
use std::time::Duration;

/// Sent with every content download. The in-game bridge refuses requests that carry it, so a
/// download that is redirected at the bridge cannot drive it.
pub const USER_AGENT: &str = concat!("deadworks-launcher/", env!("CARGO_PKG_VERSION"));

const MAX_REDIRECTS: usize = 10;
/// A host that stops answering ends the download instead of holding the join open for good.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);
const READ_TIMEOUT: Duration = Duration::from_secs(60);

/// Loopback, private, link-local and the other ranges that are never a public host.
pub fn is_local_ip(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(v4) => {
            let o = v4.octets();
            v4.is_loopback()
                || v4.is_private()
                || v4.is_link_local()
                // "This network", 0.0.0.0/8: several stacks deliver it to the local machine.
                || o[0] == 0
                // Carrier-grade NAT, 100.64.0.0/10.
                || (o[0] == 100 && (o[1] & 0xC0) == 64)
                // IETF protocol assignments 192.0.0.0/24 and benchmarking 198.18.0.0/15.
                || (o[0] == 192 && o[1] == 0 && o[2] == 0)
                || (o[0] == 198 && (o[1] & 0xFE) == 18)
                // Multicast, reserved and broadcast: 224.0.0.0 and up.
                || o[0] >= 224
        }
        IpAddr::V6(v6) => {
            let s = v6.segments();
            let embedded = |hi: u16, lo: u16| is_local_ip(IpAddr::V4(Ipv4Addr::new((hi >> 8) as u8, hi as u8, (lo >> 8) as u8, lo as u8)));
            v6.is_loopback()
                || v6.is_unspecified()
                || v6.is_multicast()
                // Unique local fc00::/7, link-local fe80::/10 and the retired site-local fec0::/10.
                || (s[0] & 0xFE00) == 0xFC00
                || (s[0] & 0xFFC0) == 0xFE80
                || (s[0] & 0xFFC0) == 0xFEC0
                // Addresses that carry an IPv4 one: mapped, the old "compatible" form, NAT64
                // 64:ff9b::/96 and 6to4 2002::/16. Each is as local as the address inside it.
                || v6.to_ipv4_mapped().is_some_and(|v4| is_local_ip(IpAddr::V4(v4)))
                || (s[..6] == [0, 0, 0, 0, 0, 0] && embedded(s[6], s[7]))
                || (s[..6] == [0x64, 0xFF9B, 0, 0, 0, 0] && embedded(s[6], s[7]))
                || (s[0] == 0x2002 && embedded(s[1], s[2]))
        }
    }
}

/// Whether `host` (a name or an address, as it appears in a URL or an `ip:port`) is this machine
/// or the player's own network.
pub fn is_local_host(host: &str) -> bool {
    let host = host.trim_start_matches('[').trim_end_matches(']');
    if let Ok(ip) = host.parse::<IpAddr>() {
        return is_local_ip(ip);
    }
    let name = host.trim_end_matches('.').to_ascii_lowercase();
    name == "localhost" || name.ends_with(".localhost") || name.ends_with(".local")
}

/// Whether a game server at `addr` (`ip:port`) is on the player's own machine or network. Only
/// such a server may name a local download host.
pub fn server_is_local(addr: &str) -> bool {
    is_local_host(addr.rsplit_once(':').map_or(addr, |(host, _)| host))
}

/// Whether `url` points at this machine or the player's network. False for anything unparsable.
pub fn url_host_is_local(url: &str) -> bool {
    reqwest::Url::parse(url).is_ok_and(|u| url_is_local(&u))
}

fn url_is_local(url: &reqwest::Url) -> bool {
    url.host_str().is_none_or(is_local_host)
}

/// Name resolution for downloads that must not reach a local address: every local answer is
/// dropped before anything connects. Checking the address after the fact would be too late for a
/// GET, which has done its work by the time the reply arrives.
struct PublicOnly;

impl reqwest::dns::Resolve for PublicOnly {
    fn resolve(&self, name: reqwest::dns::Name) -> reqwest::dns::Resolving {
        let host = name.as_str().to_string();
        Box::pin(async move {
            let found = tokio::task::spawn_blocking(move || (host.as_str(), 0).to_socket_addrs().map(|a| a.collect::<Vec<_>>()))
                .await
                .map_err(|e| Box::new(e) as Box<dyn std::error::Error + Send + Sync>)??;
            let public: Vec<SocketAddr> = found.into_iter().filter(|a| !is_local_ip(a.ip())).collect();
            if public.is_empty() {
                return Err("the download host resolves to a local address, which this server may not name".into());
            }
            Ok(Box::new(public.into_iter()) as reqwest::dns::Addrs)
        })
    }
}

/// An error with the reasons under it; reqwest's own text stops at "error sending request".
fn explain(error: &dyn std::error::Error) -> String {
    let mut text = error.to_string();
    let mut source = error.source();
    while let Some(inner) = source {
        let inner_text = inner.to_string();
        if !text.contains(&inner_text) {
            text = format!("{text}: {inner_text}");
        }
        source = inner.source();
    }
    text
}

fn build(allow_local: bool) -> reqwest::Client {
    let policy = if allow_local {
        reqwest::redirect::Policy::limited(MAX_REDIRECTS)
    } else {
        reqwest::redirect::Policy::custom(|attempt| {
            if attempt.previous().len() >= MAX_REDIRECTS {
                return attempt.error("too many redirects");
            }
            if !matches!(attempt.url().scheme(), "http" | "https") || url_is_local(attempt.url()) {
                return attempt.error("redirected to a local address");
            }
            attempt.follow()
        })
    };
    let builder = reqwest::Client::builder()
        .user_agent(USER_AGENT)
        .redirect(policy)
        .connect_timeout(CONNECT_TIMEOUT)
        .read_timeout(READ_TIMEOUT);
    let builder = if allow_local { builder } else { builder.dns_resolver(Arc::new(PublicOnly)) };
    builder.build().expect("the content download client could not be built")
}

fn client(allow_local: bool) -> &'static reqwest::Client {
    static PUBLIC: OnceLock<reqwest::Client> = OnceLock::new();
    static LOCAL: OnceLock<reqwest::Client> = OnceLock::new();
    if allow_local {
        LOCAL.get_or_init(|| build(true))
    } else {
        PUBLIC.get_or_init(|| build(false))
    }
}

/// Starts a content download. `allow_local` is whether the source may be on the player's own
/// machine or network; see the module comment for when it is.
pub async fn get(url: &str, allow_local: bool) -> Result<reqwest::Response, String> {
    let parsed = reqwest::Url::parse(url).map_err(|e| format!("invalid download URL: {e}"))?;
    if !matches!(parsed.scheme(), "http" | "https") {
        return Err("only http and https downloads are supported".into());
    }
    if !allow_local && url_is_local(&parsed) {
        return Err("the download host is a local address, which this server may not name".into());
    }
    client(allow_local).get(parsed).send().await.map_err(|e| explain(&e))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn local_addresses_are_recognised_in_every_spelling() {
        for local in [
            "127.0.0.1", "127.8.9.10", "10.0.0.5", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.1.1",
            "0.0.0.0", "100.64.0.1", "100.127.255.255", "::1", "[::1]", "fc00::1", "fd12:3456::1", "fe80::1",
            "::ffff:127.0.0.1", "::ffff:192.168.0.1", "localhost", "LOCALHOST", "localhost.", "a.localhost", "printer.local",
            "0.0.0.1", "192.0.0.8", "198.18.0.1", "198.19.255.255", "224.0.0.1", "240.0.0.1", "255.255.255.255",
            "::7f00:1", "64:ff9b::7f00:1", "64:ff9b::c0a8:101", "2002:7f00:1::1", "2002:c0a8:101::1", "fec0::1", "ff02::1",
        ] {
            assert!(is_local_host(local), "{local}");
        }
        for public in [
            "8.8.8.8", "172.32.0.1", "100.128.0.1", "192.169.0.1", "2606:4700::1111", "dl.example.com",
            "198.17.0.1", "198.20.0.1", "192.0.1.1", "223.255.255.255", "64:ff9b::808:808", "2002:808:808::1",
            "localhost.example.com", "notlocalhost", "api.deadworks.net",
        ] {
            assert!(!is_local_host(public), "{public}");
        }
    }

    #[test]
    fn only_a_server_on_the_players_network_may_name_a_local_host() {
        assert!(server_is_local("127.0.0.1:27067"));
        assert!(server_is_local("192.168.1.20:27015"));
        assert!(!server_is_local("170.23.85.75:12496"));
        assert!(!server_is_local("play.example.com:27015"));
    }

    #[test]
    fn a_public_server_cannot_send_the_launcher_to_a_local_address() {
        tauri::async_runtime::block_on(async {
        for url in [
            "http://127.0.0.1:47800/dwl/prep.png?ip=1.2.3.4&port=27015&any=1",
            "http://192.168.1.1/admin",
            "http://localhost:8080/x",
            "http://[::1]/x",
        ] {
            let err = get(url, false).await.unwrap_err();
            assert!(err.contains("local address"), "{url}: {err}");
        }
        assert!(get("ftp://dl.example.com/x", false).await.unwrap_err().contains("http"));
        assert!(get("file:///C:/Windows/win.ini", true).await.unwrap_err().contains("http"));
        });
    }

    /// Needs the internet, so it is not part of the normal run:
    /// `cargo test --lib redirects -- --ignored`.
    #[test]
    #[ignore]
    fn redirects_are_followed_to_public_hosts_and_refused_to_local_ones() {
        tauri::async_runtime::block_on(async {
            let to = |target: &str| format!("https://httpbin.org/redirect-to?url={target}");
            let ok = get(&to("https%3A%2F%2Fexample.com%2F"), false).await.unwrap();
            assert_eq!(ok.url().host_str(), Some("example.com"));

            for target in ["http%3A%2F%2F127.0.0.1%3A47800%2Fdwl%2Fprep.png", "http%3A%2F%2F192.168.1.1%2F", "http%3A%2F%2Flocalhost%2F"] {
                let err = get(&to(target), false).await.unwrap_err();
                assert!(err.contains("redirect"), "{target}: {err}");
            }

            // A public name for 127.0.0.1: refused, and the local listener never hears of it -
            // not directly, and not at the end of a redirect.
            let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
            listener.set_nonblocking(true).unwrap();
            let port = listener.local_addr().unwrap().port();
            for name in ["localtest.me", "lvh.me"] {
                let err = get(&format!("http://{name}:{port}/router/apply?reboot=1"), false).await.unwrap_err();
                assert!(err.contains("local address"), "{name}: {err}");
                let err = get(&to(&format!("http%3A%2F%2F{name}%3A{port}%2Fx")), false).await.unwrap_err();
                assert!(err.contains("local address"), "{name} by redirect: {err}");
            }
            assert!(listener.accept().is_err(), "a request reached the local listener");
        });
    }
}
