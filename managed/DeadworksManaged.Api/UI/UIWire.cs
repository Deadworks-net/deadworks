using System.Text;

namespace DeadworksManaged.Api.UI;

/// <summary>
/// Wire-format encoders for the UI message protocol.
///
/// Two layers stack on top of each other:
///   1. Logical message: <code>panel_id \x1f op \x1f arg0 \x1f arg1 ...</code>
///   2. Subtitle frame:  <code>u~ &lt;id&gt; &lt;flag&gt; &lt;payload&gt;</code>  (≤ 50 chars total)
///
/// One logical message is chunked into 1+ subtitle frames sharing a wire-id
/// character. Flags: '*' = single-chunk, '+' = first, '=' = continuation, '!' = last.
/// </summary>
internal static class UIWire {
	internal const char Sep = '\x1f';
	internal const string SubtitlePrefix = "u~";
	internal const int SubtitleMaxLength = 50;
	internal const int FrameHeaderLength = 4; // "u~Xy"
	internal const int FramePayloadCapacity = SubtitleMaxLength - FrameHeaderLength; // 46

	internal enum Op : byte {
		Set      = (byte)'s',
		Clear    = (byte)'c',
		Raw      = (byte)'r',
		Build    = (byte)'b',
		Destroy  = (byte)'d',
		Precache = (byte)'p',
		Show     = (byte)'o',
		LoadXml   = (byte)'x',
		Append    = (byte)'a',
		Erase     = (byte)'e',
		Heartbeat = (byte)'h',
	}

	/// <summary>
	/// Encodes a Set op: <c>panel \x1f s \x1f lz77(k \x1f v \x1f k \x1f v ...)</c>.
	/// The k/v payload is LZ77-compressed because the caption transport
	/// silently collapses runs of repeated characters (consecutive spaces,
	/// duplicate separators). LZ77 backref-encodes those runs so the values
	/// survive the wire intact — the same mechanism that keeps Build payloads
	/// lossless. Token compression is intentionally skipped: Set values are
	/// arbitrary user data and could legitimately contain strings that match
	/// CSS-property tokens.
	/// </summary>
	internal static string EncodeSet(string panelId, IReadOnlyList<KeyValuePair<string, string>> fields) {
		var payload = new StringBuilder(fields.Count * 16);
		for (int i = 0; i < fields.Count; i++) {
			if (i > 0) payload.Append(Sep);
			payload.Append(fields[i].Key).Append(Sep).Append(fields[i].Value);
		}
		return panelId + Sep + (char)Op.Set + Sep + UILz77.Compress(payload.ToString());
	}

	/// <summary>Encodes a Clear op: panel \x1f c</summary>
	internal static string EncodeClear(string panelId) {
		return panelId + Sep + (char)Op.Clear;
	}

	/// <summary>Encodes a Raw op: panel \x1f r \x1f text</summary>
	internal static string EncodeRaw(string panelId, string text) {
		return panelId + Sep + (char)Op.Raw + Sep + text;
	}

	/// <summary>Encodes a Build op: panel \x1f b \x1f json</summary>
	internal static string EncodeBuild(string panelId, string json) {
		return panelId + Sep + (char)Op.Build + Sep + json;
	}

	/// <summary>Encodes a Destroy op: panel \x1f d</summary>
	internal static string EncodeDestroy(string panelId) {
		return panelId + Sep + (char)Op.Destroy;
	}

	/// <summary>
	/// Encodes a Precache op: panel \x1f p \x1f compressed-tree.
	/// Same payload shape as Build, but the bootstrap stores the tree without
	/// instantiating a host. Subsequent Show ops render it instantly.
	/// </summary>
	internal static string EncodePrecache(string panelId, string compressed) {
		return panelId + Sep + (char)Op.Precache + Sep + compressed;
	}

	/// <summary>Encodes a Show op: panel \x1f o (single frame, no payload).</summary>
	internal static string EncodeShow(string panelId) {
		return panelId + Sep + (char)Op.Show;
	}

	/// <summary>
	/// Encodes a LoadXml op: panel \x1f x \x1f path. Bootstrap will BLoadLayout
	/// the given path under a fresh host panel keyed by panelId. Used for
	/// addons that ship their own .xml/.js/.vcss in the mod tree.
	/// </summary>
	internal static string EncodeLoadXml(string panelId, string xmlPath) {
		return panelId + Sep + (char)Op.LoadXml + Sep + xmlPath;
	}

	/// <summary>
	/// Encodes an Append op: <c>panel \x1f a \x1f parentId \x1f compressedSubtree</c>.
	/// parentId is held outside the LZ77 envelope so the bootstrap can peel it
	/// off with a single <c>indexOf(\x1f)</c> before decompressing the rest.
	/// The compressed subtree uses the same lz77+tokens+field-stream format as
	/// Build/Precache, so it can be parsed by the existing <c>parseTree</c>.
	/// </summary>
	internal static string EncodeAppend(string panelId, string parentId, string compressedSubtree) {
		return panelId + Sep + (char)Op.Append + Sep + parentId + Sep + compressedSubtree;
	}

	/// <summary>
	/// Encodes an Erase op: <c>panel \x1f e \x1f targetId</c>. Bootstrap removes
	/// the matching child via <c>DeleteAsync</c> and also clears any auto-bind
	/// state under that id.
	/// </summary>
	internal static string EncodeErase(string panelId, string targetId) {
		return panelId + Sep + (char)Op.Erase + Sep + targetId;
	}

	/// <summary>
	/// Encodes a Heartbeat op: <c>~ \x1f h \x1f token \x1f seq</c>. The reserved
	/// panel id <c>~</c> never collides with a real id (it's the empty-field
	/// placeholder). <paramref name="token"/> is the stable per-session id the
	/// client watches for change (reconnect detection); <paramref name="seq"/>
	/// increments every send so each heartbeat's caption text is unique and
	/// can't be swallowed by the client's burst-dedup / dwLastText layers.
	/// </summary>
	internal static string EncodeHeartbeat(string token, long seq) {
		return "~" + Sep + (char)Op.Heartbeat + Sep + token + Sep + seq;
	}

	/// <summary>
	/// Splits a logical message into 1+ subtitle frames using a single-char wire id.
	/// Single-chunk uses '*' flag; multi-chunk uses '+' / '=' / '!'.
	/// </summary>
	internal static List<string> Chunk(string message, char wireId) {
		var frames = new List<string>();
		if (message.Length <= FramePayloadCapacity) {
			frames.Add($"{SubtitlePrefix}{wireId}*{message}");
			return frames;
		}

		int offset = 0;
		bool first = true;
		while (offset < message.Length) {
			int remaining = message.Length - offset;
			int take = Math.Min(FramePayloadCapacity, remaining);
			char flag;
			if (first)                                   flag = '+';
			else if (offset + take >= message.Length)    flag = '!';
			else                                          flag = '=';
			frames.Add($"{SubtitlePrefix}{wireId}{flag}{message.AsSpan(offset, take)}");
			offset += take;
			first = false;
		}
		return frames;
	}
}
