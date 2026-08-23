namespace DeadworksManaged.Api.UI;

/// <summary>
/// Direction of child layout in a <see cref="UIContainer"/>. Maps to the
/// Panorama <c>flow-children</c> CSS property.
/// </summary>
public enum FlowDirection { None, Down, Right }

/// <summary>
/// Base type for nodes in a code-driven UI tree. Built via the factories on
/// <see cref="UI"/> (<c>UI.Vertical</c>, <c>UI.Label</c>, etc.) and shipped to
/// the client by <see cref="UIPanel.BuildLayout"/>.
/// </summary>
public abstract class UINode {
	public string? Id { get; set; }
	/// <summary>
	/// Styles applied to this node, in declaration order. Each entry is a
	/// <c>(property, value)</c> pair (e.g. <c>("horizontal-align", "left")</c>).
	/// Use <see cref="UINodeExtensions.WithStyle"/> or
	/// <see cref="UINodeExtensions.WithStyles"/> to add entries fluently.
	/// Property names should be CSS kebab-case so the wire-format token
	/// compressor can replace common ones with single-byte codes.
	/// </summary>
	public List<(string Name, string Value)> Styles { get; } = new();

	/// <summary>
	/// Styles applied while the pointer is over this node, and reverted when it
	/// leaves. Panorama's <c>:hover</c> needs a stylesheet, which a server-built
	/// tree has no way to ship — the client applies these directly instead, so
	/// each plugin picks its own colours with nothing shared or pre-registered.
	///
	/// Declaring any of these makes the node hit-testable, since a node that
	/// ignores the pointer can never see it arrive.
	/// </summary>
	public List<(string Name, string Value)> HoverStyles { get; } = new();

	/// <summary>
	/// Styles applied while the pointer is held down on this node, on top of
	/// any hover styles, and reverted on release.
	///
	/// Hover and press are the only states the client has to own: the server
	/// rebuilds the tree whenever game state changes, so anything it knows —
	/// selected, disabled, whose turn it is — is already expressible by sending
	/// different styles. It just cannot see the pointer.
	/// </summary>
	public List<(string Name, string Value)> PressStyles { get; } = new();

	/// <summary>
	/// Client-side transitions declared on this node, added with
	/// <see cref="UINodeExtensions.WithTransition"/>.
	///
	/// Kept apart from <see cref="Styles"/> because Panorama expresses several
	/// transitions as parallel comma-separated lists across three properties.
	/// Appending them as ordinary styles would make a second call silently
	/// override the first rather than adding to it; collected here they compose,
	/// and the encoder emits the matched lists.
	/// </summary>
	public List<(string Property, string Duration, string Timing)> Transitions { get; } = new();

	public List<UINode> Children { get; } = new();
}

public sealed class UIContainer : UINode {
	public FlowDirection Flow { get; set; } = FlowDirection.None;
}

public sealed class UILabel : UINode {
	public string Text = "";
}

public sealed class UIButton : UINode {
	public string Text = "";
	public string? ClickEvent;
	public string[] ClickArgs = Array.Empty<string>();

	private UILabel? _textLabel;

	/// <summary>
	/// The Label carrying this button's text, created on first use.
	///
	/// A Button with text and no children has its Label created client-side,
	/// where a plugin cannot reach it to apply styles. Asking for the label here
	/// moves the text into a real child node instead, which then styles like any
	/// other. Idempotent, so repeated calls keep returning the same Label.
	/// </summary>
	public UILabel TextLabel() {
		if (_textLabel is not null) return _textLabel;
		_textLabel = new UILabel { Id = (Id ?? "button") + "Label", Text = Text };
		Text = "";
		Children.Insert(0, _textLabel);
		return _textLabel;
	}

	/// <summary>
	/// Name the event this button fires, and any arguments to send with it.
	/// Handle it with <c>UI.Panel(id).On(eventName, ...)</c>.
	/// </summary>
	public UIButton OnClick(string eventName, params string[] args) {
		ClickEvent = eventName;
		ClickArgs = args;
		return this;
	}
}

public sealed class UIImage : UINode {
	/// <summary>
	/// Source path. Typically <c>file://{images}/&lt;addon&gt;/&lt;file&gt;.vtex</c>
	/// for mod-supplied images (the runtime resolves to the compiled
	/// <c>.vtex_c</c>; the source <c>.png</c> sits alongside in the mod tree).
	/// </summary>
	public string Src = "";
}

public static class UINodeExtensions {
	public static T WithId<T>(this T n, string id) where T : UINode {
		n.Id = id;
		return n;
	}

	/// <summary>
	/// Append a single style entry. Chain multiple calls for several
	/// properties.
	/// </summary>
	public static T WithStyle<T>(this T n, string name, string value) where T : UINode {
		n.Styles.Add((name, value));
		return n;
	}

	/// <summary>
	/// Append several style entries at once. Accepts either inline tuples or
	/// a pre-built array (e.g. a <c>private static readonly (string, string)[]</c>
	/// constant shared across nodes).
	/// </summary>
	public static T WithStyles<T>(this T n, params (string Name, string Value)[] entries) where T : UINode {
		n.Styles.AddRange(entries);
		return n;
	}

	/// <summary>
	/// Append a style that applies only while the pointer is over the node.
	/// Reverted on leave: to the value the node's normal styles declare for that
	/// property, or to the stylesheet default if they declare none.
	/// </summary>
	public static T WithHoverStyle<T>(this T n, string name, string value) where T : UINode {
		n.HoverStyles.Add((name, value));
		return n;
	}

	/// <summary>Append several hover style entries at once.</summary>
	public static T WithHoverStyles<T>(this T n, params (string Name, string Value)[] entries) where T : UINode {
		n.HoverStyles.AddRange(entries);
		return n;
	}

	/// <summary>
	/// Append a style that applies while the pointer is held down on the node,
	/// layered over any hover styles and reverted on release.
	/// </summary>
	public static T WithPressStyle<T>(this T n, string name, string value) where T : UINode {
		n.PressStyles.Add((name, value));
		return n;
	}

	/// <summary>Append several press style entries at once.</summary>
	public static T WithPressStyles<T>(this T n, params (string Name, string Value)[] entries) where T : UINode {
		n.PressStyles.AddRange(entries);
		return n;
	}

	/// <summary>
	/// Declare a client-side transition: when <paramref name="property"/> next
	/// changes on this node, the client interpolates to the new value over
	/// <paramref name="duration"/> instead of snapping.
	///
	/// Call it once per property; several calls compose into the parallel lists
	/// Panorama expects. The change itself comes from
	/// <see cref="UIPanel.SetStyle"/>, because a transition needs the property to
	/// move on a node that already exists. Rebuilding the tree creates the node
	/// holding the final value, with nothing to animate from.
	/// </summary>
	public static T WithTransition<T>(this T n, string property, string duration,
		string timing = "linear") where T : UINode {
		n.Transitions.Add((property, duration, timing));
		return n;
	}

	/// <summary>
	/// Style the text inside a Button. Without this the text Label is created on
	/// the client and cannot be reached from a plugin.
	/// </summary>
	public static UIButton WithTextStyle(this UIButton b, string name, string value) {
		b.TextLabel().Styles.Add((name, value));
		return b;
	}

	/// <summary>Apply several styles to a Button's text Label at once.</summary>
	public static UIButton WithTextStyles(this UIButton b, params (string Name, string Value)[] entries) {
		b.TextLabel().Styles.AddRange(entries);
		return b;
	}

	public static T Add<T>(this T n, params UINode[] children) where T : UINode {
		n.Children.AddRange(children);
		return n;
	}
}
