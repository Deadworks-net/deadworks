//! `search_api`: the XML documentation files that ship next to the Deadworks assemblies, searched
//! by words. They come from the release the launcher hosts, so they match what plugins compile
//! against.

use std::path::Path;

use regex::Regex;
use serde_json::Value;

pub struct Member {
    /// "api" or "game".
    assembly: &'static str,
    kind: &'static str,
    /// Without the namespace prefix, with simplified parameter types.
    name: String,
    /// The last segment of the name, before any parameters: what a search hits first.
    short: String,
    summary: String,
}

/// Summaries of the members protoc generates for every message field, which bury everything else.
const PROTOBUF_BOILERPLATE: &[&str] = &["Gets whether the \"", "Clears the value of the \"", "Field number for the \"", "Container for nested types"];

const FILES: &[(&str, &str)] = &[("DeadworksManaged.Api.xml", "api"), ("DeadworksManaged.Game.xml", "game")];

pub fn load(dir: &Path) -> Vec<Member> {
    let member = Regex::new(r#"(?s)<member name="([A-Z]):([^"]+)">(.*?)</member>"#).unwrap();
    let summary = Regex::new(r"(?s)<summary>(.*?)</summary>").unwrap();
    let mut out = Vec::new();
    for (file, assembly) in FILES {
        let Ok(text) = std::fs::read_to_string(dir.join(file)) else { continue };
        for m in member.captures_iter(&text) {
            let kind = match &m[1] {
                "T" => "type",
                "M" => "method",
                "P" => "property",
                "F" => "field",
                "E" => "event",
                _ => continue,
            };
            let name = simplify(&m[2]);
            let head = name.split('(').next().unwrap_or(&name);
            let short = head.rsplit('.').next().unwrap_or(head).to_string();
            let summary = summary.captures(&m[3]).map(|s| plain(&s[1])).unwrap_or_default();
            out.push(Member { assembly, kind, short, name, summary });
        }
    }
    out
}

pub fn search(docs: &[Member], args: &Value) -> String {
    let query = args["query"].as_str().unwrap_or_default().to_lowercase();
    let terms: Vec<&str> = query.split_whitespace().collect();
    if terms.is_empty() {
        return "Give some words to search for.".into();
    }
    if docs.is_empty() {
        return "No documentation files in the SDK folder.".into();
    }
    let kind = args["kind"].as_str();
    let assembly = args["assembly"].as_str();
    let limit = args["limit"].as_u64().unwrap_or(25).clamp(1, 200) as usize;

    let mut hits: Vec<(u32, &Member)> = docs
        .iter()
        .filter(|m| kind.is_none_or(|k| k == m.kind) && assembly.is_none_or(|a| a == m.assembly))
        .filter(|m| !PROTOBUF_BOILERPLATE.iter().any(|p| m.summary.starts_with(p)))
        .filter_map(|m| {
            let (short, name, summary) = (m.short.to_lowercase(), m.name.to_lowercase(), m.summary.to_lowercase());
            let mut score = 0;
            for t in &terms {
                score += match () {
                    _ if short == *t => 8,
                    _ if short.contains(t) => 4,
                    _ if name.contains(t) => 2,
                    _ if summary.contains(t) => 1,
                    _ => return None,
                };
            }
            // Types first among equals: they are where to start reading.
            Some((score * 2 + u32::from(m.kind == "type"), m))
        })
        .collect();
    let total = hits.len();
    hits.sort_by(|a, b| b.0.cmp(&a.0).then(a.1.name.len().cmp(&b.1.name.len())));
    let mut out = String::new();
    for (_, m) in hits.iter().take(limit) {
        out.push_str(&format!("[{} {}] {}", m.assembly, m.kind, m.name));
        if !m.summary.is_empty() {
            out.push_str(" - ");
            out.push_str(&m.summary);
        }
        out.push('\n');
    }
    match total {
        0 => "Nothing matches every word.".into(),
        n if n > limit => format!("{out}({} more; add words or a kind to narrow)", n - limit),
        _ => out,
    }
}

/// `DeadworksManaged.Api.CBaseEntity.Hurt(System.Single,DeadworksManaged.Api.CBaseEntity)` →
/// `CBaseEntity.Hurt(Single, CBaseEntity)`.
fn simplify(name: &str) -> String {
    let mut s = name.to_string();
    for prefix in ["DeadworksManaged.Api.", "DeadworksManaged.Game.", "System.Collections.Generic.", "System.Numerics.", "System."] {
        s = s.replace(prefix, "");
    }
    s.replace(',', ", ")
}

/// Doc-comment XML as one line of text: `<see cref="T:X.Y"/>` becomes `Y`, other tags go.
fn plain(xml: &str) -> String {
    let see = Regex::new(r#"<see(?:also)? (?:cref|langword|href)="(?:[A-Z]:)?([^"]+)"\s*/>"#).unwrap();
    let tags = Regex::new(r"<[^>]+>").unwrap();
    let text = see.replace_all(xml, |c: &regex::Captures| simplify(&c[1]).rsplit('.').next().unwrap_or_default().to_string());
    let text = tags.replace_all(&text, "");
    let text = text.replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&").replace("&quot;", "\"");
    text.split_whitespace().collect::<Vec<_>>().join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn members_are_found_by_name_before_summary() {
        let dir = std::env::temp_dir().join("dw-docs-test");
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(
            dir.join("DeadworksManaged.Api.xml"),
            r#"<doc><members>
<member name="M:DeadworksManaged.Api.CBaseEntity.Hurt(System.Single,DeadworksManaged.Api.CBaseEntity)"><summary>Deals damage, like <see cref="M:DeadworksManaged.Api.CBaseEntity.TakeDamage(X)"/>.</summary></member>
<member name="T:DeadworksManaged.Api.TakeDamageEvent"><summary>A <c>hurt</c> about to happen.</summary></member>
</members></doc>"#,
        )
        .unwrap();
        let docs = load(&dir);
        assert_eq!(docs.len(), 2);
        let out = search(&docs, &serde_json::json!({ "query": "hurt" }));
        let first = out.lines().next().unwrap();
        assert_eq!(first, "[api method] CBaseEntity.Hurt(Single, CBaseEntity) - Deals damage, like TakeDamage(X).");
        assert!(out.contains("[api type] TakeDamageEvent - A hurt about to happen."));
    }
}
