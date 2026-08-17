namespace DeadworksManaged.Api.UI;

/// <summary>
/// Token-based compressor for the entire encoded BuildLayout payload (not
/// just style fields). Replaces a fixed table of common substrings — CSS
/// property names, common CSS values, and a handful of HUD-specific UI
/// patterns — with two-byte codes of the form <c>^X</c>, where <c>X</c>
/// is one ASCII letter or digit.
///
/// The escape marker <c>^</c> is exceedingly rare in real style/text/id
/// content, so the round-trip is lossless in practice. Authors who put
/// <c>^X</c> sequences (caret followed by alphanumeric) into label text or
/// IDs may see them rewritten on the client.
///
/// Applied once to the whole field-separated payload after
/// <see cref="UITreeEncoder.Encode"/>. The encoder builds style strings
/// without whitespace from <c>(name, value)</c> pairs, so no minification
/// is needed here.
/// </summary>
internal static class UIStyleCompressor {
	// Order matters — longest tokens first so "border-radius" matches before
	// "border", "margin-bottom" before "margin", etc.
	//
	// 62 entries — uses all 26 uppercase + 26 lowercase + 10 digit codes.
	private static readonly (string Token, string Code)[] _table = {
		// 38 — game-specific font stacks (huge savings per occurrence)
		("'Retail Demo', 'Noto Sans', sans-serif",  "^m"),
		// 34
		("Consolas, 'Courier New', monospace",      "^n"),
		// 16
		("background-color", "^O"),
		("horizontal-align", "^A"),
		("file://{images}/", "^9"),
		// 14
		("vertical-align",   "^B"),
		("padding-bottom",   "^N"),
		("letter-spacing",   "^o"),
		// 13
		("padding-right",    "^L"),
		("border-radius",    "^R"),
		("margin-bottom",    "^I"),
		("flow-children",    "^D"),
		// 12
		("padding-left",     "^K"),
		("margin-right",     "^G"),
		("fit-children",     "^p"),
		// 11
		("padding-top",      "^M"),
		("font-family",      "^T"),
		("font-weight",      "^U"),
		("margin-left",      "^F"),
		("white-space",      "^q"),
		("transparent",      "^r"),
		// 10
		("text-align",       "^C"),
		("margin-top",       "^H"),
		("visibility",       "^X"),
		("box-shadow",       "^s"),
		("min-height",       "^t"),
		("max-height",       "^u"),
		("transition",       "^v"),
		// 9
		("1px solid",        "^i"),
		("font-size",        "^S"),
		("menuClick",        "^j"),
		("min-width",        "^w"),
		("max-width",        "^x"),
		("transform",        "^y"),
		// 8
		("overflow",         "^Z"),
		("absolute",         "^z"),
		("relative",         "^0"),
		("preserve",         "^1"),
		// 7
		("padding",          "^J"),
		("display",          "^Y"),
		("Button ",          "^k"),
		("inherit",          "^2"),
		("stretch",          "^3"),
		// 6
		("border",           "^Q"),
		("center",           "^a"),
		("margin",           "^E"),
		("bottom",           "^e"),
		("height",           "^W"),
		("dashed",           "^g"),
		("nowrap",           "^4"),
		("normal",           "^5"),
		// 5
		("Label",            "^l"),
		("color",            "^P"),
		("solid",            "^f"),
		("right",            "^c"),
		("width",            "^V"),
		// 4
		("100%",             "^h"),
		("left",             "^b"),
		("auto",             "^6"),
		("bold",             "^7"),
		("none",             "^8"),
		// 3
		("top",              "^d"),
	};

	internal static string Compress(string? input) {
		if (string.IsNullOrEmpty(input)) return "";
		var s = input!;
		foreach (var (token, code) in _table) {
			if (s.Contains(token)) s = s.Replace(token, code);
		}
		return s;
	}
}
