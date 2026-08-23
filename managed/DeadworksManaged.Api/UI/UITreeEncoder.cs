using System.Text;
using System.Linq;

namespace DeadworksManaged.Api.UI;

/// <summary>
/// Encodes a <see cref="UINode"/> tree as a flat sequence of fields separated
/// by <c>\x1f</c>. JSON was tried first but the closed-caption transport
/// mangles some of its structural characters; this format uses only
/// <c>\x1f</c> as a separator (already proven to round-trip via the
/// existing Set/Clear/Raw ops).
///
/// **Empty fields are encoded as the single character <c>~</c>** so the wire
/// payload never contains two consecutive <c>\x1f</c> bytes — there's evidence
/// the caption layer collapses runs of separators, which would silently shift
/// fields out of alignment downstream. The bootstrap interprets a lone
/// <c>~</c> field as empty when reading back.
///
/// The payload begins with a deduplicated style table so a repeated style
/// (e.g. the row style shared by every scoreboard row) is shipped once, not
/// once per node. Only styles used by 2+ nodes go in the table — a one-off
/// style stays inline because the table-plus-reference (~4 chars) would be
/// pure overhead vs. just emitting the CSS directly. Each node's style field
/// is one of: <c>~</c> (empty), <c>@N</c> (table reference), or an inline
/// CSS string. Layout on the wire:
///
/// <code>
///   styleCount \x1f style0 \x1f style1 \x1f ... \x1f &lt;node fields...&gt;
/// </code>
///
/// Per node, eight fields in fixed order, then the node's children laid out
/// recursively in the same shape:
/// <list type="number">
///   <item><c>type</c> — single char: <c>p</c> plain, <c>v</c> vertical,
///         <c>h</c> horizontal, <c>L</c> label, <c>B</c> button,
///         <c>I</c> image.</item>
///   <item><c>id</c> — element id (or <c>~</c>).</item>
///   <item><c>style</c> — <c>~</c> for empty or <c>@N</c> referencing the
///         style table.</item>
///   <item><c>text</c> — Label/Button text, or Image src path (or <c>~</c>).</item>
///   <item><c>event</c> — Button click spec, <c>event[,arg1,arg2,...]</c>
///         (or <c>~</c>).</item>
///   <item><c>hover</c> — styles applied while the pointer is over the node,
///         same encoding as <c>style</c> (or <c>~</c>).</item>
///   <item><c>press</c> — styles applied while the pointer is held down,
///         same encoding again (or <c>~</c>).</item>
///   <item><c>child_count</c> — decimal integer.</item>
/// </list>
///
/// The node arity is fixed, so the encoder and the client bootstrap have to
/// ship together.
/// </summary>
internal static class UITreeEncoder {
	private const char Sep = '\x1f';
	private const string EmptyPlaceholder = "~";

	internal static string Encode(UINode root) {
		// Pass 1: walk the tree and count occurrences of each distinct style.
		var counts = new Dictionary<string, int>();
		CountStyles(root, counts);

		// Build the table from styles used 2+ times. Single-use styles stay
		// inline because table-plus-reference (~4 chars) costs more than the
		// inline CSS by itself.
		var styleTable = new List<string>();
		var styleIndex = new Dictionary<string, int>();
		foreach (var kv in counts) {
			if (kv.Value < 2) continue;
			styleIndex[kv.Key] = styleTable.Count;
			styleTable.Add(kv.Key);
		}

		// Pass 2: emit nodes, referencing the table for shared styles.
		var nodes = new StringBuilder();
		bool first = true;
		AppendNode(nodes, root, ref first, styleIndex);

		var sb = new StringBuilder();
		sb.Append(styleTable.Count);
		foreach (var s in styleTable) sb.Append(Sep).Append(s);
		if (nodes.Length > 0) sb.Append(Sep).Append(nodes);
		return sb.ToString();
	}

	private static void CountStyles(UINode node, Dictionary<string, int> counts) {
		Count(BaseStyleCss(node), counts);
		Count(BuildStyleCss(node.HoverStyles), counts);
		Count(BuildStyleCss(node.PressStyles), counts);
		foreach (var child in node.Children) CountStyles(child, counts);
	}

	private static void Count(string css, Dictionary<string, int> counts) {
		if (string.IsNullOrEmpty(css)) return;
		counts[css] = counts.TryGetValue(css, out var c) ? c + 1 : 1;
	}

	private static void AppendNode(StringBuilder sb, UINode node, ref bool first,
		Dictionary<string, int> styleIndex) {
		AppendField(sb, TypeCode(node), ref first);
		AppendField(sb, OrPlaceholder(node.Id), ref first);

		AppendField(sb, StyleField(BaseStyleCss(node), styleIndex), ref first);

		string text = "";
		string evt = "";
		switch (node) {
			case UILabel lbl:
				text = lbl.Text ?? "";
				break;
			case UIButton btn:
				text = btn.Text ?? "";
				if (!string.IsNullOrEmpty(btn.ClickEvent)) {
					evt = btn.ClickArgs.Length == 0
						? btn.ClickEvent!
						: btn.ClickEvent + "," + string.Join(",", btn.ClickArgs);
				}
				break;
			case UIImage img:
				// Image has no text or event of its own; reuse the text slot
				// for the src path so every node keeps the same arity.
				text = img.Src ?? "";
				break;
		}
		AppendField(sb, OrPlaceholder(text), ref first);
		AppendField(sb, OrPlaceholder(evt), ref first);
		AppendField(sb, StyleField(BuildStyleCss(node.HoverStyles), styleIndex), ref first);
		AppendField(sb, StyleField(BuildStyleCss(node.PressStyles), styleIndex), ref first);
		AppendField(sb, node.Children.Count.ToString(), ref first);

		foreach (var child in node.Children) {
			AppendNode(sb, child, ref first, styleIndex);
		}
	}

	private static string OrPlaceholder(string? s)
		=> string.IsNullOrEmpty(s) ? EmptyPlaceholder : s!;

	private static string StyleField(string css, Dictionary<string, int> styleIndex) {
		if (string.IsNullOrEmpty(css)) return EmptyPlaceholder;
		return styleIndex.TryGetValue(css, out var idx) ? "@" + idx : css;
	}

	/// <summary>
	/// A node's own styles plus any transitions it declared, which Panorama
	/// expresses as three parallel comma-separated lists.
	/// </summary>
	private static string BaseStyleCss(UINode node) {
		if (node.Transitions.Count == 0) return BuildStyleCss(node.Styles);

		var all = new List<(string Name, string Value)>(node.Styles) {
			("transition-property", string.Join(",", node.Transitions.Select(t => t.Property))),
			("transition-duration", string.Join(",", node.Transitions.Select(t => t.Duration))),
			("transition-timing-function", string.Join(",", node.Transitions.Select(t => t.Timing))),
		};
		return BuildStyleCss(all);
	}

	private static string BuildStyleCss(List<(string Name, string Value)> styles) {
		if (styles.Count == 0) return "";
		var sb = new StringBuilder();
		for (int i = 0; i < styles.Count; i++) {
			if (i > 0) sb.Append(';');
			var (name, value) = styles[i];
			sb.Append(name).Append(':').Append(value);
		}
		return sb.ToString();
	}

	private static void AppendField(StringBuilder sb, string value, ref bool first) {
		if (!first) sb.Append(Sep);
		first = false;
		sb.Append(value);
	}

	private static string TypeCode(UINode n) => n switch {
		UIContainer c when c.Flow == FlowDirection.Down  => "v",
		UIContainer c when c.Flow == FlowDirection.Right => "h",
		UIContainer                                       => "p",
		UILabel                                           => "L",
		UIButton                                          => "B",
		UIImage                                           => "I",
		_                                                 => "?",
	};
}
