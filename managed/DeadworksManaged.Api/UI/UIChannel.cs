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

	private sealed class Slot {
		internal readonly Dictionary<(string panel, string key), string> Reliable = new();
		internal readonly Dictionary<(string panel, string key), string> Unreliable = new();
		internal readonly Queue<OrderedOp> Ordered = new();
		internal readonly Queue<string> OutFrames = new();
		internal double Tokens;

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

	// Public knobs (forwarded from UI.RatePerSecond / UI.BurstSize).
	internal static int RatePerSecond = 5;
	internal static int BurstSize = 5;

	internal static void EnqueueSet(RecipientFilter to, string panelId, string key, string value, bool unreliable) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			var s = _slots[slot];
			(unreliable ? s.Unreliable : s.Reliable)[(panelId, key)] = value;
		}
	}

	internal static void EnqueueClear(RecipientFilter to, string panelId) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Clear, null));
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
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Build, json));
		}
	}

	internal static void EnqueueDestroy(RecipientFilter to, string panelId) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Destroy, null));
		}
	}

	internal static void EnqueuePrecache(RecipientFilter to, string panelId, string compressed) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Precache, compressed));
		}
	}

	internal static void EnqueueShow(RecipientFilter to, string panelId) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Show, null));
		}
	}

	internal static void EnqueueLoadXml(RecipientFilter to, string panelId, string xmlPath) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.LoadXml, xmlPath));
		}
	}

	internal static void EnqueueAppend(RecipientFilter to, string panelId, string parentId, string compressedSubtree) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Append, compressedSubtree, parentId));
		}
	}

	internal static void EnqueueErase(RecipientFilter to, string panelId, string targetId) {
		for (int slot = 0; slot < _slots.Length; slot++) {
			if (!to.HasRecipient(slot)) continue;
			_slots[slot].Ordered.Enqueue(new OrderedOp(panelId, OrderedKind.Erase, targetId));
		}
	}

	/// <summary>Refill tokens, build chunks from pending state, and emit at most one cycle of available chunks.</summary>
	internal static void Tick() {
		long now = DateTime.UtcNow.Ticks;
		double dt = (now - _lastTickTimestamp) / (double)TimeSpan.TicksPerSecond;
		if (dt < 0) dt = 0;
		_lastTickTimestamp = now;

		double refill = RatePerSecond * dt;
		double cap = BurstSize;

		// Liveness pulse: once per interval, queue a heartbeat to every connected
		// slot. It rides the same rate-limited queue as real traffic (cheap: one
		// tiny frame/sec), and any real op also proves liveness on the client, so
		// the standalone heartbeat only matters when the UI is otherwise idle.
		bool doHeartbeat = (now - _lastHeartbeatTicks) >= HeartbeatIntervalMs * TimeSpan.TicksPerMillisecond;
		string? heartbeat = null;
		if (doHeartbeat) {
			_lastHeartbeatTicks = now;
			heartbeat = UIWire.EncodeHeartbeat(SessionToken, unchecked(_heartbeatSeq++));
		}

		for (int slot = 0; slot < _slots.Length; slot++) {
			var s = _slots[slot];
			s.Tokens = Math.Min(cap, s.Tokens + refill);

			if (heartbeat != null && Players.IsConnected(slot)) EnqueueChunks(s, heartbeat);

			// 1) Emit anything already chunked
			DrainOutFrames(slot, s);
			if (s.Tokens < 1.0) continue;

			// 2) Build new frames from state — ordered first (preserves user-visible sequence)
			BuildOrderedFrames(s);
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
			EnqueueChunks(s, msg);
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

	private static void EnqueueChunks(Slot s, string message) {
		char wireId = NextWireId(s);
		var frames = UIWire.Chunk(message, wireId);
		foreach (var f in frames) s.OutFrames.Enqueue(f);
	}

	private static char NextWireId(Slot s) {
		// Cycle through a-z. Same wire id reuse is fine because the receiver
		// resets its buffer for that id whenever a '+' or '*' flag arrives.
		int n = s.WireIdCursor++ % 26;
		return (char)('a' + n);
	}

	private static void DrainOutFrames(int slot, Slot s) {
		while (s.OutFrames.Count > 0 && s.Tokens >= 1.0) {
			string frame = s.OutFrames.Dequeue();
			SendCaption(slot, frame);
			s.Tokens -= 1.0;
		}
	}

	private static void SendCaption(int slot, string text) {
		var msg = new CUserMessageCloseCaptionPlaceholder {
			Duration = 0f,
			EntIndex = -1,
			FromPlayer = false,
			String = text,
		};
		NetMessages.Send(msg, RecipientFilter.Single(slot));
	}

	internal static void OnPlayerDisconnect(int slot) {
		if (slot < 0 || slot >= _slots.Length) return;
		var s = _slots[slot];
		s.Reliable.Clear();
		s.Unreliable.Clear();
		s.Ordered.Clear();
		s.OutFrames.Clear();
		s.Tokens = 0;
	}
}
