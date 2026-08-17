namespace DeadworksManaged.Api.UI;

/// <summary>
/// Per-recipient queues, token-bucket rate limiter, and drain loop. Receives
/// enqueue calls from <see cref="UIPanel"/> and emits closed-caption frames at
/// a bounded rate. The host runtime drives <see cref="Tick"/> once per game
/// frame; plugins don't call this directly.
/// </summary>
internal static class UIChannel {
	internal enum OrderedKind { Clear, Raw, Build, Destroy, Precache, Show, LoadXml, Append, Erase }

	internal readonly struct OrderedOp {
		internal readonly string PanelId;
		internal readonly OrderedKind Kind;
		// For Raw: the raw text. For Build/Precache: the compressed tree.
		// For LoadXml: the xml path. For Append: the compressed subtree.
		// For Erase: the target id. Otherwise null.
		internal readonly string? Payload;
		// Auxiliary string slot. Used by Append to carry the parentId alongside
		// the compressed subtree in Payload.
		internal readonly string? Aux;
		internal OrderedOp(string panelId, OrderedKind kind, string? payload, string? aux = null) {
			PanelId = panelId; Kind = kind; Payload = payload; Aux = aux;
		}
	}

	/// <summary>One caption frame plus the net-channel buffer it should ride.</summary>
	private readonly record struct Frame(string Text, bool Reliable);

	/// <summary>
	/// What it takes to rebuild one panel on one client from nothing.
	///
	/// The client discards its entire UI whenever server frames stop arriving
	/// for a few seconds, and it cannot rebuild alone — layouts only exist
	/// server-side. Rather than making every plugin remember to handle that
	/// (forget it once and that player's UI is dead until reconnect, silently),
	/// the channel keeps the last structural op per panel and replays it.
	/// </summary>
	private sealed class PanelSnapshot {
		internal string? Layout;    // Precache/Build payload
		internal bool    Shown;     // Show was issued (Build implies it)
		internal string? XmlPath;   // LoadXml path, mutually exclusive with Layout
		internal bool    UsedDeltas; // Append/Erase seen — see ReplayInto
	}

	private sealed class Slot {
		internal readonly Dictionary<(string panel, string key), string> Reliable = new();
		internal readonly Dictionary<(string panel, string key), string> Unreliable = new();
		internal readonly Queue<OrderedOp> Ordered = new();
		internal readonly Queue<Frame> OutFrames = new();
		internal double Tokens;
		internal double ByteTokens;

		// Retained so a torn-down client can be rebuilt without plugin help.
		// Structure per panel, plus the latest value of every field ever set —
		// Reliable/Unreliable are drained on flush, so they can't serve as the
		// record of what the client is supposed to be showing.
		internal readonly Dictionary<string, PanelSnapshot> Panels = new();
		internal readonly Dictionary<(string panel, string key), string> Shadow = new();

		// Set when a resync has queued structural ops whose field values must
		// follow them. Not merged into Reliable up front: BuildOrderedFrames
		// flushes a panel's pending Sets *ahead* of each ordered op, so doing so
		// would put the values on the wire before the layout that consumes them.
		internal bool ShadowReplayPending;

		// Handshake state. Captions emitted between full-connect and the
		// client's HUD/caption pipeline coming up vanish silently, so nothing
		// real is sent until the bootstrap proves the pipeline is live by
		// answering a hello with `dw_ui ~|ack|<token>`. Until then all enqueued
		// traffic just buffers here.
		internal bool Acked;

		// Panel ids the client's own mod tree provides a layout for, announced
		// on the ack. Empty for a stock client, or for one whose addon predates
		// the field — treat "not listed" as "unknown", never as "absent".
		internal readonly HashSet<string> ClientPanels = new(StringComparer.Ordinal);

		// What became of each runtime addon we asked this client to load.
		// Without it a plugin cannot tell "the player has my addon" from "the
		// player has nothing", because a failed BLoadLayout only ever logged
		// client-side.
		internal readonly Dictionary<string, AddonState> Addons = new(StringComparer.Ordinal);

		// Round-robin counter for generating wire-ids for chunked messages.
		// Wire id only needs to disambiguate concurrent chunked streams to the
		// SAME recipient, which is rare; cycling 'a'..'z' gives plenty.
		internal int WireIdCursor;
	}

	private static readonly Slot[] _slots = NewSlots();
	private static long _lastTickTimestamp = DateTime.UtcNow.Ticks;

	private static Slot[] NewSlots() {
		var arr = new Slot[Players.MaxSlot + 1];
		for (int i = 0; i < arr.Length; i++) arr[i] = new Slot();
		return arr;
	}

	// ─── Session liveness / disconnect detection ───────────────────────────
	// One token per server process (regenerated on full restart). The client
	// tears its UI down if heartbeats stop (disconnect) or if the token changes
	// (it reconnected to a different process). Stable across plugin hot reloads
	// because this assembly is shared and loaded once.
	internal static readonly string SessionToken = NewSessionToken();
	internal static int HeartbeatIntervalMs = 1000;
	private static long _lastHeartbeatTicks;
	private static long _heartbeatSeq;

	private static string NewSessionToken() {
		// Short, collision-unlikely: base36 of a random 63-bit value (~12 chars).
		Span<byte> b = stackalloc byte[8];
		Random.Shared.NextBytes(b);
		long v = BitConverter.ToInt64(b) & long.MaxValue;
		return ToBase36(v);
	}

	// Convert.ToString(long, toBase) only supports bases 2/8/10/16 — base 36
	// throws "Invalid Base", so encode by hand. Digits 0-9a-z are wire-safe
	// (no separator/flag chars). long.MaxValue needs 13 base-36 digits.
	private static string ToBase36(long value) {
		const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
		if (value == 0) return "0";
		Span<char> buf = stackalloc char[13];
		int i = buf.Length;
		while (value > 0) {
			buf[--i] = digits[(int)(value % 36)];
			value /= 36;
		}
		return new string(buf.Slice(i));
	}

	// Public knobs (forwarded from UI.RatePerSecond / UI.BurstSize / UI.BytesPerSecond).
	//
	// Two budgets, because the binding constraint is bytes, not messages.
	// Frames are emitted through NativeSendNetMessage, whose CRecipientFilter
	// defaults to BUF_RELIABLE — the reliable channel does not throttle when
	// the server outruns the client's acks, it disconnects with
	// NETWORK_DISCONNECT_RELIABLEOVERFLOW. The message budget bounds per-frame
	// protobuf/netmessage overhead; the byte budget bounds reliable-channel
	// pressure, which is what actually risks a kick.
	//
	// Frames are now up to UIWire.SubtitleMaxLength instead of 50, so the same
	// payload costs far fewer messages and fewer headers than it used to.
	// Tick() runs once per game frame, so a high message cap means a small
	// update is on the wire the same frame it is enqueued — latency, not
	// throughput, is what a low message cap costs. Bytes are bounded separately
	// by BytesPerSecond, so this is deliberately loose; the real ceiling is the
	// client's 6-caption display pool (see SafeCaptionRate).
	internal static int RatePerSecond = 60;
	internal static int BurstSize = 60;

	/// <summary>
	/// Captions the client will render at once. <c>sub_181953220</c> stops
	/// collecting at 6, and only collected items get a Panorama panel — so a
	/// 7th concurrent caption is not delayed, it is <b>lost</b>.
	/// </summary>
	private const int CaptionSlots = 6;

	/// <summary>
	/// Fraction of the pool to actually use. At exactly <c>6 / lifetime</c> the
	/// pool sits permanently full and any frame-time jitter drops a caption, so
	/// aim for roughly four of the six slots in steady state.
	/// </summary>
	private const double CaptionPoolUtilisation = 0.7;

	/// <summary>
	/// How long a caption lingers on top of its own length. The client computes
	/// a caption's expiry as <c>cc_linger_time + len</c>, so our
	/// <c>&lt;len:N&gt;</c> frames occupy a slot for <c>N + linger</c>, not N —
	/// and the pool recycles that much slower.
	///
	/// This value is sent to the client on every heartbeat and the addon asserts
	/// it as <c>cc_linger_time</c>, so the two halves can't drift. Raising it
	/// costs throughput and buys margin against a slow Panorama frame dropping a
	/// caption unseen.
	/// </summary>
	internal static float CaptionLingerSeconds = 0.1f;

	/// <summary>
	/// Frames per second the client can actually absorb. Exceeding this loses
	/// data silently, which is worse than being slow, so Tick clamps to it.
	///
	/// Divides by lifetime, not length: a slot is held for the caption's length
	/// <i>plus</i> the linger. Ignoring the linger overstated this by several
	/// times at the stock value, which is why the channel felt slower than its
	/// numbers said.
	/// </summary>
	internal static double SafeCaptionRate()
		=> CaptionSlots * CaptionPoolUtilisation
		   / Math.Max(0.02f, CaptionLengthSeconds + CaptionLingerSeconds);
	internal static int BytesPerSecond = 16384;
	internal static int ByteBurst = 16384;

	private static PanelSnapshot Snapshot(Slot s, string panelId) {
		if (!s.Panels.TryGetValue(panelId, out var snap)) {
			snap = new PanelSnapshot();
			s.Panels[panelId] = snap;
		}
		return snap;
	}

	private static void ForgetPanel(Slot s, string panelId) {
		s.Panels.Remove(panelId);
		DropShadow(s, panelId);
	}

	private static void DropShadow(Slot s, string panelId) {
		if (s.Shadow.Count == 0) return;
		List<(string panel, string key)>? doomed = null;
		foreach (var k in s.Shadow.Keys) {
			if (k.panel == panelId) (doomed ??= new()).Add(k);
		}
		if (doomed is null) return;
		foreach (var k in doomed) s.Shadow.Remove(k);
	}

	internal static void EnqueueSet(RecipientFilter to, string panelId, string key, string value, bool unreliable) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			(unreliable ? s.Unreliable : s.Reliable)[(panelId, key)] = value;
			// Latest value wins, and it outlives the flush so a rebuilt client
			// gets the current state rather than waiting for the next update.
			s.Shadow[(panelId, key)] = value;
		}
	}

	internal static void EnqueueClear(RecipientFilter to, string panelId) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Clear, null));
			DropShadow(s, panelId);
		}
	}

	internal static void EnqueueRaw(RecipientFilter to, string panelId, string text) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Raw, text));
		}
	}

	internal static void EnqueueBuild(RecipientFilter to, string panelId, string json) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Build, json));
			// Build is precache+show, and the client wipes prior field state.
			var snap = Snapshot(s, panelId);
			snap.Layout = json; snap.Shown = true; snap.XmlPath = null; snap.UsedDeltas = false;
			DropShadow(s, panelId);
		}
	}

	internal static void EnqueueDestroy(RecipientFilter to, string panelId) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Destroy, null));
			ForgetPanel(s, panelId);   // nothing to rebuild
		}
	}

	internal static void EnqueuePrecache(RecipientFilter to, string panelId, string compressed) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Precache, compressed));
			var snap = Snapshot(s, panelId);
			snap.Layout = compressed; snap.XmlPath = null; snap.UsedDeltas = false;
		}
	}

	internal static void EnqueueShow(RecipientFilter to, string panelId) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Show, null));
			Snapshot(s, panelId).Shown = true;
		}
	}

	internal static void EnqueueLoadXml(RecipientFilter to, string panelId, string xmlPath) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.LoadXml, xmlPath));
			var snap = Snapshot(s, panelId);
			snap.XmlPath = xmlPath; snap.Layout = null; snap.Shown = false; snap.UsedDeltas = false;
			// Whatever the client last told us is about to be out of date.
			s.Addons.Remove(panelId);
		}
	}

	/// <summary>
	/// Re-send the layout this client was last given for <paramref name="panelId"/>,
	/// which tears the addon down and rebuilds it. The path comes from the
	/// retained snapshot, so callers don't have to remember it.
	/// Returns how many recipients had something to reload.
	/// </summary>
	internal static int EnqueueReload(RecipientFilter to, string panelId) {
		int n = 0;
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			if (!s.Panels.TryGetValue(panelId, out var snap) || snap.XmlPath is null) continue;
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.LoadXml, snap.XmlPath));
			s.Addons.Remove(panelId);
			n++;
		}
		return n;
	}

	internal static void EnqueueAppend(RecipientFilter to, string panelId, string parentId, string compressedSubtree) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Append, compressedSubtree, parentId));
			Snapshot(s, panelId).UsedDeltas = true;
		}
	}

	internal static void EnqueueErase(RecipientFilter to, string panelId, string targetId) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			Snapshot(s, panelId).UsedDeltas = true;
			s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Erase, targetId));
		}
	}

	/// <summary>Refill tokens, build chunks from pending state, and emit at most one cycle of available chunks.</summary>
	internal static void Tick() {
		long now = DateTime.UtcNow.Ticks;
		double dt = (now - _lastTickTimestamp) / (double)TimeSpan.TicksPerSecond;
		if (dt < 0) dt = 0;
		_lastTickTimestamp = now;

		// Never outrun the client's 6-caption display pool — frames past it are
		// dropped, not queued.
		double rate = Math.Min(RatePerSecond, SafeCaptionRate());
		double refill = rate * dt;

		// The burst ceiling is the pool, not the rate. After a server hitch dt
		// is large and the bucket refills to its cap, so a rate-sized burst
		// would dump ~20 frames into a single tick — the client renders six
		// concurrently and silently discards the rest, taking whatever ordered
		// ops happened to be in that burst with them. Capping the burst at the
		// pool's usable depth means a backlog drains steadily instead of
		// overrunning the client the moment the server catches up.
		double cap = Math.Min(BurstSize, Math.Max(1.0, CaptionSlots * CaptionPoolUtilisation));
		double byteRefill = BytesPerSecond * dt;
		// Never let the byte burst fall below one maximum-size frame, or an
		// oversized frame at the head of the queue could stall the drain
		// forever.
		double byteCap = Math.Max(ByteBurst, UIWire.SubtitleMaxLength);

		// Liveness pulse: once per interval, queue a heartbeat to every connected
		// slot. It rides the same rate-limited queue as real traffic (cheap: one
		// tiny frame/sec), and any real op also proves liveness on the client, so
		// the standalone heartbeat only matters when the UI is otherwise idle.
		bool doHeartbeat = (now - _lastHeartbeatTicks) >= HeartbeatIntervalMs * TimeSpan.TicksPerMillisecond;
		string? heartbeat = null;
		string? hello = null;
		if (doHeartbeat) {
			_lastHeartbeatTicks = now;
			long seq = unchecked(_heartbeatSeq++);
			heartbeat = UIWire.EncodeHeartbeat(SessionToken, seq);
			hello = UIWire.EncodeHeartbeat(SessionToken, seq, requestAck: true);
		}

		for (int slot = 0; slot < _slots.Length; slot++) {
			var s = _slots[slot];
			s.Tokens = Math.Min(cap, s.Tokens + refill);
			s.ByteTokens = Math.Min(byteCap, s.ByteTokens + byteRefill);

			// Handshake gate: until this client acks (see Slot.Acked), emit
			// nothing but the hello — repeated idempotently at the heartbeat
			// cadence, bypassing the token bucket (it's one tiny frame per
			// interval). Enqueued ops keep buffering; the first tick after the
			// ack drains them in their original order with a full burst.
			if (!s.Acked) {
				if (hello != null && Players.IsConnected(slot)) {
					// Always reliable: the session can't start until this lands.
					foreach (var f in UIWire.Chunk(hello, NextWireId(s))) SendCaption(slot, f, reliable: true);
				}
				continue;
			}

			if (heartbeat != null && Players.IsConnected(slot)) EnqueueChunks(s, heartbeat);

			// 1) Emit anything already chunked
			DrainOutFrames(slot, s);
			if (s.Tokens < 1.0) continue;

			// 2) Build new frames from state — ordered first (preserves user-visible sequence)
			BuildOrderedFrames(s);

			// A resync's field values are merged only now: BuildOrderedFrames
			// has already turned the replayed layout into frames, so these
			// queue behind it. Merging earlier would send values for a panel the
			// client has not rebuilt yet, and they would be dropped.
			if (s.ShadowReplayPending && s.Ordered.Count == 0) {
				foreach (var (key, value) in s.Shadow) s.Reliable[key] = value;
				s.ShadowReplayPending = false;
			}

			BuildReliableFrames(s);

			// 3) Unreliable: only if there's headroom (no reliable backlog left)
			if (s.OutFrames.Count == 0 && s.Tokens >= 1.0 && s.Unreliable.Count > 0) {
				BuildUnreliableFrames(s);
			} else if (s.Unreliable.Count > 0 && s.OutFrames.Count > 0) {
				// Still reliable backlog — drop unreliable to keep latency low.
				s.Unreliable.Clear();
			}

			// 4) Emit whatever fits this tick
			DrainOutFrames(slot, s);
		}
	}

	private static void BuildOrderedFrames(Slot s) {
		while (s.Ordered.Count > 0) {
			var op = s.Ordered.Dequeue();

			// Flush any pending Set fields for this panel BEFORE the ordered op
			// so client sees: latest sets, then clear/raw — preserving order.
			FlushPanelSets(s, op.PanelId, fromReliable: true);
			FlushPanelSets(s, op.PanelId, fromReliable: false);

			string msg = op.Kind switch {
				OrderedKind.Clear    => UIWire.EncodeClear(op.PanelId),
				OrderedKind.Raw      => UIWire.EncodeRaw(op.PanelId, op.Payload ?? ""),
				OrderedKind.Build    => UIWire.EncodeBuild(op.PanelId, op.Payload ?? ""),
				OrderedKind.Destroy  => UIWire.EncodeDestroy(op.PanelId),
				OrderedKind.Precache => UIWire.EncodePrecache(op.PanelId, op.Payload ?? ""),
				OrderedKind.Show     => UIWire.EncodeShow(op.PanelId),
				OrderedKind.LoadXml  => UIWire.EncodeLoadXml(op.PanelId, op.Payload ?? ""),
				OrderedKind.Append   => UIWire.EncodeAppend(op.PanelId, op.Aux ?? "", op.Payload ?? ""),
				OrderedKind.Erase    => UIWire.EncodeErase(op.PanelId, op.Payload ?? ""),
				_                    => "",
			};
			if (msg.Length == 0) continue;
			EnqueueChunks(s, msg);
		}
	}

	private static void BuildReliableFrames(Slot s) {
		FlushAllPanelSets(s, fromReliable: true);
	}

	private static void BuildUnreliableFrames(Slot s) {
		FlushAllPanelSets(s, fromReliable: false);
	}

	private static void FlushAllPanelSets(Slot s, bool fromReliable) {
		var dict = fromReliable ? s.Reliable : s.Unreliable;
		if (dict.Count == 0) return;

		// Group by panel id
		var byPanel = new Dictionary<string, List<KeyValuePair<string, string>>>();
		foreach (var kv in dict) {
			if (!byPanel.TryGetValue(kv.Key.panel, out var list)) {
				list = new List<KeyValuePair<string, string>>();
				byPanel[kv.Key.panel] = list;
			}
			list.Add(new KeyValuePair<string, string>(kv.Key.key, kv.Value));
		}
		dict.Clear();

		foreach (var (panelId, fields) in byPanel) {
			var msg = UIWire.EncodeSet(panelId, fields);
			// Unreliable Sets are latest-wins by construction, so a dropped
			// frame just means the next update supersedes it.
			EnqueueChunks(s, msg, reliable: fromReliable);
		}
	}

	private static void FlushPanelSets(Slot s, string panelId, bool fromReliable) {
		var dict = fromReliable ? s.Reliable : s.Unreliable;
		if (dict.Count == 0) return;
		List<KeyValuePair<string, string>>? fields = null;
		var keysToRemove = new List<(string panel, string key)>();
		foreach (var kv in dict) {
			if (kv.Key.panel != panelId) continue;
			(fields ??= new List<KeyValuePair<string, string>>()).Add(new KeyValuePair<string, string>(kv.Key.key, kv.Value));
			keysToRemove.Add(kv.Key);
		}
		if (fields == null) return;
		foreach (var k in keysToRemove) dict.Remove(k);
		EnqueueChunks(s, UIWire.EncodeSet(panelId, fields));
	}

	/// <param name="reliable">
	/// Requested channel. A multi-frame message is forced back to reliable
	/// regardless: the client reassembles chunks by wire-id and flag, so a
	/// dropped fragment does not degrade the message, it destroys it (and
	/// strands the reassembly buffer until the id is reused). Only a message
	/// that fits in a single frame can safely be unreliable.
	/// </param>
	private static void EnqueueChunks(Slot s, string message, bool reliable = true) {
		char wireId = NextWireId(s);
		var frames = UIWire.Chunk(message, wireId);
		bool sendReliable = reliable || frames.Count > 1;
		foreach (var f in frames) s.OutFrames.Enqueue(new Frame(f, sendReliable));
	}

	private static char NextWireId(Slot s) {
		// Cycle through a-z. Same wire id reuse is fine because the receiver
		// resets its buffer for that id whenever a '+' or '*' flag arrives.
		int n = s.WireIdCursor++ % 26;
		return (char)('a' + n);
	}

	private static void DrainOutFrames(int slot, Slot s) {
		while (s.OutFrames.Count > 0 && s.Tokens >= 1.0) {
			Frame frame = s.OutFrames.Peek();
			int cost = frame.Text.Length + LengthPrefix().Length;  // SendCaption prepends the <len:> tag
			if (s.ByteTokens < cost) break;                        // byte budget exhausted this tick
			s.OutFrames.Dequeue();
			SendCaption(slot, frame.Text, frame.Reliable);
			s.Tokens -= 1.0;
			s.ByteTokens -= cost;
		}
	}

	/// <summary>
	/// Per-caption lifetime, in seconds, injected as <c>&lt;len:N&gt;</c> markup.
	///
	/// This is the single most important throughput knob, and it is not about
	/// bandwidth. <c>CCitadelHudSubtitles</c> (client.dll <c>sub_181953220</c>)
	/// walks the caption item list and stops collecting at <b>6</b> items:
	///
	///     if (v7 &gt;= 6) break;
	///
	/// Only those six ever get a Panorama panel, so items 7+ are invisible to
	/// the mod no matter how fast we send. Effective throughput is therefore
	/// 6 / lifetime. Left alone, lifetime is <c>cc_linger_time</c> (~1.2s), which
	/// caps the channel at ~5 captions/sec regardless of rate or frame size.
	///
	/// <c>&lt;len:N&gt;</c> is parsed out of the caption text by
	/// <c>sub_1816C64E0</c>, which sets the item's death time to
	/// <c>cc_linger_time + N</c> — linger is <b>added</b>, not replaced. Both
	/// halves are ours: this one rides in the caption text, and linger is sent
	/// on the heartbeat for the client to assert.
	///
	/// Lifetime is the whole story for throughput, and it also sets frame size:
	/// a plugin filling a byte budget puts <c>budget / framerate</c> in each
	/// frame, so a long lifetime means fewer, fatter captions. Those are more
	/// expensive for the client to lay out, since the subtitle Label is
	/// <c>html="true"</c>. Do not drop this near a Panorama frame time — an item
	/// that expires before the UI next runs is never rendered, and what is never
	/// rendered can never be read.
	/// </summary>
	internal static float CaptionLengthSeconds = 0.1f;

	private static string LengthPrefix() =>
		"<len:" + CaptionLengthSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ">";

	private static void SendCaption(int slot, string text, bool reliable) {
		// Prepended after chunking and after UIWire escaping, so these are the
		// only '<' and '>' in the frame — the payload's own were already
		// escaped away. The client strips the tag from the rendered text (the
		// subtitle Label is html="true" and this is not a known tag), and the
		// bootstrap locates the "u~" prefix by search rather than position, so
		// it does not matter whether any of it survives to .text.
		text = LengthPrefix() + text;

		// Defensive clamp. The client copies caption text into a fixed
		// char[4096] stack buffer with no bounds check (client.dll
		// sub_1816C64E0; copy loop at 0x1816C6B35), so an oversized frame
		// corrupts its stack rather than being truncated. UIWire caps frame
		// construction well below this, so tripping here means a knob was
		// mis-set — clamp rather than hand the client a malformed frame.
		if (text.Length > UIWire.CaptionHardLimit) text = text[..UIWire.CaptionHardLimit];

		var msg = new CUserMessageCloseCaptionPlaceholder {
			Duration = 0f,
			EntIndex = -1,
			FromPlayer = false,
			String = text,
		};
		NetMessages.Send(msg, RecipientFilter.Single(slot), reliable);
	}

	/// <summary>
	/// Handshake ack from the client bootstrap (routed via UIBootstrap from
	/// <c>dw_ui ~|ack|&lt;token&gt;|&lt;panel,ids&gt;</c>). The token guards against a
	/// stale ack aimed at a previous server session; repeats are harmless.
	///
	/// <paramref name="panelList"/> is the comma-separated set of panel ids the
	/// client registered locally — optional, and absent from older addons.
	/// </summary>
	internal static void Acknowledge(int slot, string token, string? panelList = null) {
		if (slot < 0 || slot >= _slots.Length) return;
		if (!string.Equals(token, SessionToken, StringComparison.Ordinal)) return;
		var s = _slots[slot];
		s.Acked = true;

		// Replace rather than merge: the ack is a full statement of what the
		// client has right now, and a reconnecting client may have fewer panels
		// than it did before.
		s.ClientPanels.Clear();
		if (string.IsNullOrEmpty(panelList)) return;
		foreach (var id in panelList.Split(',')) {
			var trimmed = id.Trim();
			if (trimmed.Length > 0) s.ClientPanels.Add(trimmed);
		}
	}

	/// <summary>Panel ids the client at <paramref name="slot"/> supplies its own layout for.</summary>
	internal static IReadOnlyCollection<string> ClientPanels(int slot)
		=> (slot < 0 || slot >= _slots.Length) ? Array.Empty<string>() : _slots[slot].ClientPanels;

	/// <summary>
	/// A runtime addon reported in (routed from <c>dw_ui ~|addon|&lt;panel&gt;|&lt;state&gt;</c>).
	///
	/// The bootstrap sends <c>ok</c>/<c>fail</c> for whether the layout resolved
	/// at all, and the addon's own script sends <c>ready</c> once it registers.
	/// Those are genuinely different: a mounted VPK with a broken script reports
	/// ok but never ready.
	/// </summary>
	internal static void ReportAddon(int slot, string panelId, string status) {
		if (slot < 0 || slot >= _slots.Length) return;
		if (string.IsNullOrEmpty(panelId)) return;

		var state = status switch {
			"ready" => AddonState.Ready,
			"ok"    => AddonState.Loaded,
			"fail"  => AddonState.Failed,
			_       => AddonState.Unknown,
		};
		if (state == AddonState.Unknown) return;

		var s = _slots[slot];
		s.Addons.TryGetValue(panelId, out var prev);
		if (prev == state) return;
		s.Addons[panelId] = state;
		UI.RaiseAddonStatus(slot, panelId, state);
	}

	internal static AddonState AddonStatus(int slot, string panelId) {
		if (slot < 0 || slot >= _slots.Length) return AddonState.Unknown;
		return _slots[slot].Addons.TryGetValue(panelId, out var v) ? v : AddonState.Unknown;
	}

	/// <summary>
	/// The client tore its UI down and has nothing left (routed via UIBootstrap
	/// from <c>dw_ui ~|resync|&lt;token&gt;</c>).
	///
	/// The client's heartbeat watchdog destroys every panel when frames stop
	/// arriving — a server hitch is enough. Without this the server still
	/// believed the client was acked, so it kept streaming Set ops at a client
	/// that had no panels to apply them to and never re-sent the layouts: the
	/// UI stayed dead until reconnect. Resetting to the pre-handshake state
	/// re-runs the hello, and <see cref="UI.ClientResync"/> lets plugins put
	/// their layouts back.
	/// </summary>
	internal static void RequestResync(int slot, string token) {
		if (slot < 0 || slot >= _slots.Length) return;
		// Same guard as the ack: ignore a resync aimed at a previous session.
		if (!string.Equals(token, SessionToken, StringComparison.Ordinal)) return;

		var s = _slots[slot];
		// Drop anything queued — it targets panels the client no longer has,
		// and replaying it would just reproduce the "no handler" spam.
		s.Reliable.Clear();
		s.Unreliable.Clear();
		s.Ordered.Clear();
		s.OutFrames.Clear();
		s.Tokens = 0;
		s.ByteTokens = 0;
		s.Acked = false;

		// Rebuild from what we already know we sent. Plugins do not have to
		// participate — anything that went out once can go out again, and
		// precache/show simply replaces whatever the client had.
		int panels = ReplayInto(s);

		// Escape hatch for the cases the replay can't cover (see ReplayInto).
		UI.RaiseClientResync(slot);

		Console.WriteLine($"[UI] slot {slot} resync — replayed {panels} panel(s), {s.Shadow.Count} field(s)");
	}

	/// <summary>
	/// Re-queues the retained structure and field values for one client.
	///
	/// Faithful for the precache/show, build and load-xml panels that make up
	/// almost everything. It is NOT faithful for panels built up with
	/// Append/Erase: those are deltas against a tree, and we keep only the base,
	/// so the panel returns to its last full layout without the incremental
	/// children. Such a panel is flagged and its owner should rebuild it from
	/// <see cref="UI.ClientResync"/> — hence the event still exists.
	/// </summary>
	private static int ReplayInto(Slot s) {
		foreach (var (panelId, snap) in s.Panels) {
			if (snap.XmlPath is not null) {
				s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.LoadXml, snap.XmlPath));
			} else if (snap.Layout is not null) {
				s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Precache, snap.Layout));
				if (snap.Shown) s.Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Show, null));
			}
			if (snap.UsedDeltas) {
				Console.WriteLine($"[UI] panel '{panelId}' uses Append/Erase — resync restores its base layout only");
			}
		}

		// Field values are deliberately NOT merged into Reliable here. They must
		// reach the client after the layout: Sets for a panel that does not yet
		// exist are discarded, and Build explicitly wipes prior field state on
		// the assumption that a fresh tree is authoritative. Tick merges them
		// once the structure has been turned into frames.
		s.ShadowReplayPending = s.Shadow.Count > 0;

		return s.Panels.Count;
	}

	internal static void OnPlayerDisconnect(int slot) {
		if (slot < 0 || slot >= _slots.Length) return;
		var s = _slots[slot];
		s.Reliable.Clear();
		s.Unreliable.Clear();
		s.Ordered.Clear();
		s.OutFrames.Clear();
		s.Panels.Clear();
		s.Shadow.Clear();
		s.ClientPanels.Clear();
		s.Addons.Clear();
		s.Tokens = 0;
		s.ByteTokens = 0;
		s.Acked = false;
	}
}
