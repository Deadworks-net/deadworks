namespace DeadworksManaged.Api.UI;

/// <summary>
/// Entry point for the UI message manager. Use <see cref="Panel"/> to obtain a
/// handle for a logical UI panel; the panel id binds to the panorama-side panel
/// script registered with the same id via <c>DW.registerPanel({ id: "..." })</c>.
/// </summary>
public static class UI {
	private static readonly Dictionary<string, UIPanel> _panels = new();

	public static UIPanel Panel(string id) {
		if (!_panels.TryGetValue(id, out var p)) {
			p = new UIPanel(id);
			_panels[id] = p;
		}
		return p;
	}

	/// <summary>Per-recipient subtitle send rate. Defaults to 5/s.</summary>
	public static int RatePerSecond {
		get => UIChannel.RatePerSecond;
		set => UIChannel.RatePerSecond = Math.Max(1, value);
	}

	/// <summary>Per-recipient burst capacity (token-bucket size). Defaults to 5.</summary>
	public static int BurstSize {
		get => UIChannel.BurstSize;
		set => UIChannel.BurstSize = Math.Max(1, value);
	}

	internal static void Tick() => UIChannel.Tick();

	// ─── Code-driven layout factories ──────────────────────────────────────
	// Construct a UI tree purely in C# and ship it to the bootstrap with
	// `UIPanel.BuildLayout(...)`. The bootstrap walks the tree and creates
	// the DOM via $.CreatePanel, so plugin authors don't need to write a
	// paired .xml/.js panel script for simple overlays.

	/// <summary>Plain Panel container (no flow direction).</summary>
	public static UIContainer Container(string? id = null) => new() { Id = id };

	/// <summary>Panel container that flows children top-to-bottom.</summary>
	public static UIContainer Vertical(string? id = null) => new() { Id = id, Flow = FlowDirection.Down };

	/// <summary>Panel container that flows children left-to-right.</summary>
	public static UIContainer Horizontal(string? id = null) => new() { Id = id, Flow = FlowDirection.Right };

	/// <summary>Label element. The id is auto-bound to <see cref="UIPanel.Set"/> updates with the same key.</summary>
	public static UILabel Label(string id, string text = "") => new() { Id = id, Text = text };

	/// <summary>
	/// Button element. When clicked, the bootstrap dispatches
	/// <c>dw_ui &lt;panelId&gt;|&lt;clickEvent&gt;|&lt;args...&gt;</c>, which is routed
	/// to handlers registered via <see cref="UIPanel.On"/>.
	/// </summary>
	public static UIButton Button(string id, string text, string? clickEvent = null, params string[] args)
		=> new() { Id = id, Text = text, ClickEvent = clickEvent, ClickArgs = args };

	/// <summary>
	/// Image element. <paramref name="src"/> is a Panorama path — typically
	/// <c>file://{images}/&lt;addon&gt;/&lt;file&gt;.vtex</c> referring to the
	/// compiled texture (the source <c>.png</c> sits next to it in the mod
	/// tree). The bootstrap calls <c>SetImage</c> on the resulting panel.
	/// </summary>
	public static UIImage Image(string id, string src) => new() { Id = id, Src = src };
}

/// <summary>
/// Handle for a logical UI panel. Methods enqueue updates onto each recipient's
/// rate-limited queue; the actual subtitles are emitted from the host runtime's
/// per-frame tick. Field updates are coalesced latest-wins per <c>(panel, key)</c>.
/// </summary>
public sealed class UIPanel {
	public string Id { get; }
	internal UIPanel(string id) { Id = id; }

	private readonly Dictionary<string, Action<UIEvent>> _eventHandlers = new();

	/// <summary>Reliable field update — eventually delivered, latest value wins.</summary>
	public void Set(RecipientFilter to, string key, object value)
		=> UIChannel.EnqueueSet(to, Id, key, value?.ToString() ?? "", unreliable: false);

	/// <summary>Best-effort field update — dropped under bandwidth pressure, latest value wins.</summary>
	public void SetUnreliable(RecipientFilter to, string key, object value)
		=> UIChannel.EnqueueSet(to, Id, key, value?.ToString() ?? "", unreliable: true);

	/// <summary>Tells the panel script to clear its state.</summary>
	public void Clear(RecipientFilter to)
		=> UIChannel.EnqueueClear(to, Id);

	/// <summary>Sends an opaque text payload to the panel script's onRaw handler.</summary>
	public void SendRaw(RecipientFilter to, string text)
		=> UIChannel.EnqueueRaw(to, Id, text);

	/// <summary>
	/// Replace the panel's DOM tree on each recipient. The bootstrap creates
	/// a host panel under the HUD root if needed, wipes any existing children,
	/// and walks the tree calling <c>$.CreatePanel</c> for each node. Subsequent
	/// <see cref="Set"/> calls on the same panel auto-bind to Labels by id.
	/// </summary>
	public void BuildLayout(RecipientFilter to, UINode root)
		=> UIChannel.EnqueueBuild(to, Id, EncodeBuildPayload(root));

	/// <summary>Destroy the panel's host on each recipient and forget its registration.</summary>
	public void DestroyLayout(RecipientFilter to)
		=> UIChannel.EnqueueDestroy(to, Id);

	/// <summary>
	/// Ship the layout to each recipient and let the bootstrap parse + cache
	/// it without instantiating a host. After this completes, a much smaller
	/// <see cref="Show"/> call (a single chunk, no payload) renders the panel
	/// instantly. Useful for HUDs whose structure stays fixed and only field
	/// values change at runtime — precache once, set fields freely.
	/// </summary>
	public void Precache(RecipientFilter to, UINode root)
		=> UIChannel.EnqueuePrecache(to, Id, EncodeBuildPayload(root));

	/// <summary>
	/// Render a previously-precached panel on each recipient. No-op (and
	/// logs on the client) if the panel was never precached. Pre-existing
	/// state from <see cref="Set"/> calls is auto-applied to matching Labels
	/// after the host is created.
	/// </summary>
	public void Show(RecipientFilter to)
		=> UIChannel.EnqueueShow(to, Id);

	/// <summary>
	/// Tell the bootstrap to <c>BLoadLayout</c> an XML file from the client's
	/// mod tree under a fresh host panel. The XML's own <c>&lt;scripts&gt;</c>
	/// block runs in an isolated domain, so the addon must be self-contained
	/// (no <c>DW</c> access from inside). Use <see cref="DestroyLayout"/> to
	/// tear it down.
	/// </summary>
	public void LoadXml(RecipientFilter to, string xmlPath)
		=> UIChannel.EnqueueLoadXml(to, Id, xmlPath);

	/// <summary>
	/// Append a subtree under an existing node in the panel's host. Far
	/// cheaper than <see cref="BuildLayout"/> when only a small piece of the
	/// tree is changing — typical row addition fits in 1–2 wire chunks.
	/// <paramref name="parentId"/> must match a node id inside the existing
	/// tree (resolved client-side via <c>FindChildTraverse</c>).
	/// </summary>
	public void AppendChild(RecipientFilter to, string parentId, UINode child)
		=> UIChannel.EnqueueAppend(to, Id, parentId, EncodeBuildPayload(child));

	/// <summary>
	/// Remove a child node by id from the panel's host. Single-chunk wire op.
	/// </summary>
	public void RemoveChild(RecipientFilter to, string targetId)
		=> UIChannel.EnqueueErase(to, Id, targetId);

	internal static string EncodeBuildPayload(UINode root)
		=> UILz77.Compress(UIStyleCompressor.Compress(UITreeEncoder.Encode(root)));

	/// <summary>Begin building a multi-op message that ships in one logical send.</summary>
	public UIUpdate Build() => new UIUpdate(this);

	/// <summary>
	/// Register a handler for an event the panel fires client-side via
	/// <c>DW.send(panelId, eventName, ...args)</c>. Latest registration wins.
	/// </summary>
	public UIPanel On(string eventName, Action<UIEvent> handler) {
		_eventHandlers[eventName] = handler;
		return this;
	}

	internal bool TryDispatch(UIEvent ev) {
		if (!_eventHandlers.TryGetValue(ev.EventName, out var fn)) return false;
		try { fn(ev); }
		catch (Exception ex) { Console.WriteLine($"[UI] handler '{Id}/{ev.EventName}' threw: {ex}"); }
		return true;
	}
}

/// <summary>An event dispatched from the panorama panel back to the server.</summary>
public sealed class UIEvent {
	public CCitadelPlayerController Caller { get; }
	public string PanelId   { get; }
	public string EventName { get; }
	public string[] Args    { get; }

	internal UIEvent(CCitadelPlayerController caller, string panelId, string eventName, string[] args) {
		Caller = caller; PanelId = panelId; EventName = eventName; Args = args;
	}

	public string ArgAt(int index, string fallback = "")
		=> (index >= 0 && index < Args.Length) ? Args[index] : fallback;
}
