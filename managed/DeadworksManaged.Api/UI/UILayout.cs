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

	public static T Add<T>(this T n, params UINode[] children) where T : UINode {
		n.Children.AddRange(children);
		return n;
	}
}
