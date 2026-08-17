using System.Text;

namespace DeadworksManaged.Api.UI;

/// <summary>
/// Tiny LZ77-style compressor used as a final pass over the encoded
/// BuildLayout payload. After the field encoder + style tokenizer have done
/// their thing, the wire stream still contains a lot of cross-field
/// repetition (the same compressed button style fragment appears once per
/// button, etc.). LZ77 catches those automatically without any per-tree
/// analysis.
///
/// Wire format:
/// <list type="bullet">
///   <item>Literal byte: passes through unchanged.</item>
///   <item>Backref: <c>\x1e &lt;o1&gt; &lt;o2&gt; &lt;len&gt;</c> — four
///     bytes total. <c>o1</c> and <c>o2</c> are base-94 digits in
///     <c>33..126</c> encoding the offset minus one (so offsets 1..8836 fit).
///     <c>len</c> is the same encoding, mapped to a match length of
///     <c>code + 4</c> (4..97).</item>
///   <item>Literal escape (rare — <c>\x1e</c> never appears in our encoded
///     trees in practice): doubled to <c>\x1e\x1e</c>.</item>
/// </list>
///
/// <c>\x1e</c> (Record Separator) is in the same ASCII control-char family
/// as <c>\x1f</c> (Unit Separator), which the existing protocol already
/// round-trips successfully through the closed-caption transport.
/// </summary>
internal static class UILz77 {
	private const char Escape = '\x1e';
	private const int MinMatch = 4;
	private const int BaseStart = 33;     // '!'
	private const int Base = 94;          // 33..126 inclusive
	private const int MaxOffset = Base * Base;          // 8836
	private const int MaxLength = MinMatch + Base - 1;  // 97

	internal static string Compress(string input) {
		if (string.IsNullOrEmpty(input)) return input ?? "";
		var sb = new StringBuilder(input.Length);
		int i = 0;
		while (i < input.Length) {
			int bestLen = 0, bestOffset = 0;
			int windowStart = i - MaxOffset; if (windowStart < 0) windowStart = 0;
			int maxL = input.Length - i; if (maxL > MaxLength) maxL = MaxLength;
			for (int j = windowStart; j < i; j++) {
				int len = 0;
				while (len < maxL && input[j + len] == input[i + len]) len++;
				if (len > bestLen) {
					bestLen = len;
					bestOffset = i - j;
					if (len == maxL) break;
				}
			}
			if (bestLen >= MinMatch) {
				int encoded = bestOffset - 1; // 0..MaxOffset-1
				sb.Append(Escape);
				sb.Append((char)(BaseStart + encoded / Base));
				sb.Append((char)(BaseStart + encoded % Base));
				sb.Append((char)(BaseStart + (bestLen - MinMatch)));
				i += bestLen;
			} else {
				char c = input[i];
				if (c == Escape) sb.Append(Escape); // doubled literal
				sb.Append(c);
				i++;
			}
		}
		return sb.ToString();
	}
}
