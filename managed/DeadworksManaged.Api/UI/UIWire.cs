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

	/// <summary>
	/// Hard ceiling imposed by the client, not by us. The caption processor
	/// (client.dll <c>sub_1816C64E0</c>) copies caption text into a fixed
	/// <c>char[4096]</c> stack buffer, and the copy loop advances its write
	/// pointer with no bounds compare — a segment at or above 4096 bytes
	/// overwrites the client's stack. Never raise this.
	/// </summary>
	internal const int CaptionHardLimit = 4000;

	/// <summary>
	/// Wire length of one caption frame. Nothing in the caption pipeline
	/// truncates: the item text reaches the panel as a whole <c>CUtlString</c>
	/// and is handed to <c>SetDialogVariable("subtitle_text")</c> intact. The
	/// old value of 50 matched the <c>width: 400px</c> visual line-wrap in
	/// <c>citadel_hud_subtitles.css</c>, which only affects rendering — the
	/// Label's <c>.text</c> still returns the full string.
	///
	/// Bigger frames are also *cheaper* on the wire: every frame costs a
	/// usermessage plus protobuf framing, and frames are sent BUF_RELIABLE
	/// (see NativeSendNetMessage), so fewer/fatter frames means strictly less
	/// reliable-channel pressure for the same payload.
	/// </summary>
	internal static int SubtitleMaxLength = 3800;

	internal const int FrameHeaderLength = 4; // "u~Xy"
	internal static int FramePayloadCapacity => SubtitleMaxLength - FrameHeaderLength;

	// ─── Wire-safe alphabet ────────────────────────────────────────────────
	//
	// Three classes of byte do not survive the closed-caption transport, and
	// the compressor emits all of them by accident:
	//
	//   '<' '>' '&'  The subtitle Label is html="true" over the loc token
	//                "<i>{s:subtitle_text}</i>", so the HTML text layer
	//                re-parses the substituted value. Unknown tags are dropped
	//                and '&' is read as an entity introducer — both are lossy
	//                on readback.
	//   whitespace   HTML layout collapses every run to a single space, and the
	//                caption processor strips leading whitespace outright
	//                (its skip loop covers 09,0A,0B,0C,0D,20).
	//
	// UILz77 encodes backref digits as (33 + n) for n in 0..93, which spans
	// '!'..'~' and therefore hits '&' (38), '<' (60) and '>' (62) regularly.
	// That produced intermittent, payload-dependent corruption that scaled with
	// message length.
	//
	// Escaping here rather than reshaping the compressor's alphabet keeps one
	// mirrored decode on the client and also covers literal payload bytes
	// (panel ids, keys, plugin-supplied values) that can contain the same
	// characters. ESC is 0x1D, which sits in the same control-byte family as
	// the 0x1E/0x1F separators the protocol already round-trips successfully;
	// the caption processor passes it through its default switch branch.
	// NOTE: these must be , never "\x1d". C#'s \x escape is
	// variable-length and greedily eats up to four hex digits, so "\x1dA" is
	// the single character U+01DA — not ESC followed by 'A'. That silently
	// broke '<', '>', '&', ESC, ' ' and '\t' (whose tags are all hex digits)
	// while leaving '\n'..'\r' (tags G..J) working. \u is always exactly four
	// digits and cannot absorb the tag.
	private const char Esc = '';

	private static string EscapeFor(char c) => c switch {
		'<'    => "A",
		'>'    => "B",
		'&'    => "C",
		Esc    => "D",
		' '    => "E",
		'\t'   => "F",
		'\n'   => "G",
		'\v'   => "H",
		'\f'   => "I",
		'\r'   => "J",
		_      => "",
	};

	private static bool NeedsEscape(char c) =>
		c == '<' || c == '>' || c == '&' || c == Esc ||
		c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';

	/// <summary>
	/// Rewrites a logical message so the wire form contains no character the
	/// caption transport mangles. Mirrored by <c>unescapeWire</c> in
	/// dw_bootstrap.js. Applied to the whole message before chunking, so the
	/// \x1f field separators and the LZ77 envelope both pass through unharmed
	/// (neither 0x1E nor 0x1F is in the escaped set).
	/// </summary>
	internal static string EscapeWire(string message) {
		int extra = 0;
		foreach (char c in message) if (NeedsEscape(c)) extra++;
		if (extra == 0) return message;

		var sb = new StringBuilder(message.Length + extra);
		foreach (char c in message) {
			if (NeedsEscape(c)) sb.Append(EscapeFor(c));
			else sb.Append(c);
		}
		return sb.ToString();
	}

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
		Style     = (byte)'y',
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

	/// <summary>
	/// Encodes a Style op:
	/// <c>panel \x1f y \x1f lz77(tokens(node \x1f prop \x1f value \x1f ...))</c>.
	///
	/// Token-compressed, unlike <see cref="EncodeSet"/>. Set carries arbitrary
	/// user data that could legitimately contain a <c>^X</c> sequence and be
	/// rewritten by the detokenizer; a style payload is CSS property names and
	/// CSS values, where a caret does not occur. The saving is large precisely
	/// because the table is CSS properties — <c>background-color</c> is one byte
	/// plus the escape.
	/// </summary>
	internal static string EncodeStyle(string panelId, IReadOnlyList<(string Node, string Prop, string Value)> entries) {
		var payload = new StringBuilder(entries.Count * 24);
		for (int i = 0; i < entries.Count; i++) {
			if (i > 0) payload.Append(Sep);
			payload.Append(entries[i].Node).Append(Sep)
			       .Append(entries[i].Prop).Append(Sep)
			       .Append(entries[i].Value);
		}
		return panelId + Sep + (char)Op.Style + Sep
		     + UILz77.Compress(UIStyleCompressor.Compress(payload.ToString()));
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
	/// Encodes a Heartbeat op:
	/// <c>~ \x1f h \x1f token \x1f seq \x1f version [\x1f a]</c>. The
	/// reserved panel id <c>~</c> never collides with a real id (it's the
	/// empty-field placeholder). <paramref name="token"/> is the stable
	/// per-session id the client watches for change (reconnect detection);
	/// <paramref name="seq"/> increments every send so each heartbeat's caption
	/// text is unique and can't be swallowed by the client's burst-dedup /
	/// dwLastText layers. <paramref name="requestAck"/> appends a trailing
	/// <c>a</c> field: the pre-handshake hello, asking the bootstrap to reply
	/// <c>dw_ui ~|ack|token</c> to prove its caption pipeline is live.
	/// </summary>
	internal static string EncodeHeartbeat(string token, long seq, bool requestAck = false) {
		// Wire: ~ \x1f h \x1f token \x1f seq \x1f version \x1f linger [\x1f a]
		//
		// The framework version and the caption linger ride here rather than
		// being separate messages: the heartbeat already reaches every client,
		// repeats idempotently, and re-delivers on reconnect, so the client
		// never has to hardcode either or ask for them. Costs a handful of
		// bytes per second.
		//
		// Linger matters because it is half of a caption's lifetime, and
		// lifetime is what sets the channel's frame rate. Sending it means the
		// server's pacing maths and the client's cc_linger_time can never drift
		// apart, and tuning throughput is one knob on one side.
		//
		// The ack marker stays last but is found by scanning rather than by
		// index, so future fields can be appended without breaking a client
		// that predates them.
		var msg = "~" + Sep + (char)Op.Heartbeat + Sep + token + Sep + seq + Sep + Deadworks.Version
		        + Sep + UIChannel.CaptionLingerSeconds.ToString("0.###",
		                    System.Globalization.CultureInfo.InvariantCulture);
		return requestAck ? msg + Sep + 'a' : msg;
	}

	/// <summary>
	/// Escapes a logical message into wire form, then splits it into 1+ subtitle
	/// frames using a single-char wire id. Single-chunk uses '*' flag;
	/// multi-chunk uses '+' / '=' / '!'.
	///
	/// Escaping happens before splitting so a frame boundary can never land
	/// inside an escape pair — and because ESC only ever appears as the first
	/// byte of a two-byte pair (a literal ESC is written as ESC 'D'), a chunk
	/// whose last character is ESC is unambiguously mid-pair and gives that
	/// byte back to the next frame.
	/// </summary>
	internal static List<string> Chunk(string logicalMessage, char wireId) {
		string message = EscapeWire(logicalMessage);
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
			if (take < remaining && message[offset + take - 1] == Esc) take--;
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
