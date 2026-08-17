namespace DeadworksManaged.Api.UI;

/// <summary>
/// What happened to a runtime addon on one client — see <see cref="UI.Addon"/>.
///
/// <see cref="Loaded"/> and <see cref="Ready"/> are deliberately separate: a
/// correctly mounted VPK whose script throws reports Loaded and never reaches
/// Ready, and that is exactly the case worth being able to tell apart.
/// </summary>
public enum AddonState {
	/// <summary>Nothing reported. The client may not have the addon, or may not have answered yet.</summary>
	Unknown = 0,
	/// <summary>The client tried to load the layout and could not — usually the VPK isn't mounted.</summary>
	Failed,
	/// <summary>The layout loaded. Its script may or may not have come up.</summary>
	Loaded,
	/// <summary>The addon's script registered and is receiving updates.</summary>
	Ready,
}

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

	/// <summary>Per-recipient subtitle send rate, in frames/s. Defaults to 24.</summary>
	public static int RatePerSecond {
		get => UIChannel.RatePerSecond;
		set => UIChannel.RatePerSecond = Math.Max(1, value);
	}

	/// <summary>Per-recipient burst capacity (token-bucket size). Defaults to 24.</summary>
	public static int BurstSize {
		get => UIChannel.BurstSize;
		set => UIChannel.BurstSize = Math.Max(1, value);
	}

	/// <summary>
	/// Per-recipient wire budget in bytes/s. Defaults to 16 KB/s.
	///
	/// This is the budget that matters: frames go out on the <b>reliable</b>
	/// net channel, which disconnects the client on overflow rather than
	/// throttling. Raise it only with real players on a real server, watching
	/// for choke and drops.
	/// </summary>
	public static int BytesPerSecond {
		get => UIChannel.BytesPerSecond;
		set {
			UIChannel.BytesPerSecond = Math.Max(256, value);
			UIChannel.ByteBurst = Math.Max(UIChannel.BytesPerSecond, MaxFrameLength);
		}
	}

	/// <summary>
	/// Frames per second this channel can actually deliver, derived from
	/// <see cref="CaptionLengthSeconds"/> and the client's six-caption display
	/// pool. Read-only.
	///
	/// Pace recurring updates against this. Offering frames faster than the
	/// channel drains does not increase throughput — it just grows the queue and
	/// adds latency, because a frame is only as useful as the moment it arrives.
	/// Your achievable byte rate is roughly
	/// <c>MaxFramesPerSecond × bytes-per-update</c>; to go faster, send more per
	/// frame rather than more frames.
	/// </summary>
	public static double MaxFramesPerSecond => UIChannel.SafeCaptionRate();

	/// <summary>
	/// How long each caption frame lives on the client, in seconds. Defaults to
	/// 0.2.
	///
	/// This governs <b>throughput</b>, not bandwidth. The client renders at most
	/// six captions at a time and silently discards any beyond that, so the
	/// channel tops out at <c>6 / CaptionLengthSeconds</c> frames per second —
	/// about 5/s at the game's default linger, which is why the transport felt
	/// slow regardless of rate or frame size. The send rate is clamped to stay
	/// inside this automatically.
	///
	/// Lower means more throughput but less time for the client to build its
	/// panel and for the mod to read it. Do not go near a frame time.
	/// </summary>
	public static float CaptionLengthSeconds {
		get => UIChannel.CaptionLengthSeconds;
		set => UIChannel.CaptionLengthSeconds = Math.Clamp(value, 0.05f, 5f);
	}

	/// <summary>
	/// The client's <c>cc_linger_time</c>, which is added to every caption's own
	/// length to get its expiry. Defaults to 0.3, matching what the addon
	/// asserts on the client.
	///
	/// This is not a knob you set to change client behaviour — the addon owns
	/// that. It exists so <see cref="MaxFramesPerSecond"/> tells the truth. If
	/// you ship an addon with a different <c>CC_LINGER</c>, set the same value
	/// here or the channel will pace itself against a pool that recycles slower
	/// than it thinks.
	/// </summary>
	public static float CaptionLingerSeconds {
		get => UIChannel.CaptionLingerSeconds;
		set => UIChannel.CaptionLingerSeconds = Math.Clamp(value, 0f, 5f);
	}

	/// <summary>
	/// Bytes of wire payload per caption frame. Defaults to 3800.
	///
	/// Hard-capped just under 4000: the client copies caption text into a fixed
	/// <c>char[4096]</c> stack buffer with no bounds check, so larger frames
	/// corrupt its stack. Larger frames are otherwise strictly better — each
	/// frame costs one usermessage, so fewer/fatter frames means less
	/// reliable-channel pressure for the same payload.
	/// </summary>
	public static int MaxFrameLength {
		get => UIWire.SubtitleMaxLength;
		set => UIWire.SubtitleMaxLength = Math.Clamp(value, 64, UIWire.CaptionHardLimit);
	}

	/// <summary>
	/// A client discarded its UI and needs its layouts re-sent. The argument is
	/// the player slot.
	///
	/// The client tears everything down if server frames stop arriving for a few
	/// seconds — a hitch is enough — and it cannot rebuild on its own, because
	/// layouts live on the server. Any plugin that ships a panel should re-send
	/// it here, otherwise that player's UI stays blank until they reconnect.
	/// Re-sending is idempotent: precache/show simply replaces whatever the
	/// client had.
	/// </summary>
	public static event Action<int>? ClientResync;

	/// <summary>
	/// Panel ids the client at <paramref name="slot"/> supplies its own layout
	/// for, reported on the handshake. Empty until that client acks.
	///
	/// Sending a layout for one of these replaces the client's own tree, which
	/// is almost never what you want — check here before shipping a panel whose
	/// id an addon might also claim.
	///
	/// Empty means <i>unknown</i>, not <i>none</i>: a stock client and one whose
	/// addon predates the field both report nothing. Use it to avoid stepping on
	/// a panel you know about, not to prove one isn't there.
	/// </summary>
	public static IReadOnlyCollection<string> ClientPanels(int slot)
		=> UIChannel.ClientPanels(slot);

	/// <summary>
	/// Whether that client registered <paramref name="panelId"/> itself. See
	/// <see cref="ClientPanels"/> — false can also mean "hasn't said".
	/// </summary>
	public static bool ClientOwnsPanel(int slot, string panelId) {
		foreach (var id in UIChannel.ClientPanels(slot))
			if (string.Equals(id, panelId, StringComparison.Ordinal)) return true;
		return false;
	}

	/// <summary>
	/// What became of the runtime addon you sent this client with
	/// <see cref="UIPanel.LoadXml"/>.
	///
	/// A client whose VPK isn't mounted reports <see cref="AddonState.Failed"/>,
	/// which is your cue to fall back to a code-driven panel rather than stream
	/// updates at something that isn't there.
	/// </summary>
	public static AddonState Addon(int slot, string panelId)
		=> UIChannel.AddonStatus(slot, panelId);

	/// <summary>Shorthand for <c>Addon(...) == AddonState.Ready</c>.</summary>
	public static bool AddonReady(int slot, string panelId)
		=> UIChannel.AddonStatus(slot, panelId) == AddonState.Ready;

	/// <summary>
	/// A client reported a change in one of its runtime addons: (slot, panelId, state).
	/// Fires once per transition, so it is safe to drive fallback logic from.
	/// </summary>
	public static event Action<int, string, AddonState>? AddonStatusChanged;

	internal static void RaiseAddonStatus(int slot, string panelId, AddonState state) {
		try { AddonStatusChanged?.Invoke(slot, panelId, state); }
		catch (Exception ex) { Console.WriteLine($"[UI] AddonStatusChanged handler threw: {ex}"); }
	}

	internal static void RaiseClientResync(int slot) {
		try { ClientResync?.Invoke(slot); }
		catch (Exception ex) { Console.WriteLine($"[UI] ClientResync handler threw: {ex}"); }
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
	/// Re-send whatever layout each recipient was last given for this panel,
	/// tearing the addon down and rebuilding it. The path comes from what the
	/// channel already retained, so you don't have to pass it again.
	///
	/// Exists for the edit loop: change an addon's .xml or .js and reload it
	/// without restarting the client. Returns how many recipients had a layout
	/// to reload.
	/// </summary>
	public int Reload(RecipientFilter to)
		=> UIChannel.EnqueueReload(to, Id);

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
