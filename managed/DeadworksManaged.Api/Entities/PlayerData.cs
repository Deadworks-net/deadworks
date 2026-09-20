using System.Collections;

namespace DeadworksManaged.Api;

/// <summary>
/// Per-player state keyed by player slot. Entries are removed automatically when the player
/// disconnects and cleared when the server restarts, so a plugin never has to mirror the
/// connect/disconnect lifecycle itself or worry about a slot being reused by a new player.
/// </summary>
/// <remarks>
/// <para>
/// Prefer this over <see cref="EntityData{T}"/> for anything that should survive a hero swap,
/// respawn or team change: those replace the pawn entity, and can replace the controller, while the
/// slot stays the same for as long as the player is connected.
/// </para>
/// <para>
/// Not thread-safe. Access it from the game thread only, the same as any entity API.
/// </para>
/// </remarks>
/// <typeparam name="T">The state type. Use a class with mutable fields for anything non-trivial.</typeparam>
public sealed class PlayerData<T> : IPlayerData, IEnumerable<KeyValuePair<int, T>> {
	private readonly Dictionary<int, T> _data = new();

	public PlayerData() {
		PlayerDataRegistry.Register(this);
	}

	/// <summary>Number of slots that currently have an entry.</summary>
	public int Count => _data.Count;

	/// <summary>Gets the entry for <paramref name="slot"/>, or <see langword="default"/> if none. Setting <see langword="null"/> removes it.</summary>
	public T? this[int slot] {
		get => _data.TryGetValue(slot, out var value) ? value : default;
		set {
			if (value is null)
				_data.Remove(slot);
			else
				_data[slot] = value;
		}
	}

	/// <summary>Gets or sets the entry for the player that owns <paramref name="controller"/>.</summary>
	public T? this[CBasePlayerController controller] {
		get => this[controller.Slot];
		set => this[controller.Slot] = value;
	}

	/// <summary>Gets or sets the entry for the player that owns <paramref name="pawn"/>. Getting returns <see langword="default"/> if the pawn has no controller.</summary>
	public T? this[CBasePlayerPawn pawn] {
		get {
			var controller = pawn.Controller;
			return controller != null ? this[controller.Slot] : default;
		}
		set {
			var controller = pawn.Controller;
			if (controller != null) this[controller.Slot] = value;
		}
	}

	public bool TryGet(int slot, out T value) => _data.TryGetValue(slot, out value!);

	public bool TryGet(CBasePlayerController controller, out T value) => TryGet(controller.Slot, out value);

	/// <summary>Returns the existing entry for <paramref name="slot"/>, or stores and returns <paramref name="defaultValue"/>.</summary>
	public T GetOrAdd(int slot, T defaultValue) {
		if (!_data.TryGetValue(slot, out var value)) {
			value = defaultValue;
			_data[slot] = value;
		}
		return value;
	}

	/// <summary>Returns the existing entry for <paramref name="slot"/>, or stores and returns the result of <paramref name="factory"/>.</summary>
	public T GetOrAdd(int slot, Func<T> factory) {
		if (!_data.TryGetValue(slot, out var value)) {
			value = factory();
			_data[slot] = value;
		}
		return value;
	}

	/// <inheritdoc cref="GetOrAdd(int, T)"/>
	public T GetOrAdd(CBasePlayerController controller, T defaultValue) => GetOrAdd(controller.Slot, defaultValue);

	/// <inheritdoc cref="GetOrAdd(int, Func{T})"/>
	public T GetOrAdd(CBasePlayerController controller, Func<T> factory) => GetOrAdd(controller.Slot, factory);

	public bool Has(int slot) => _data.ContainsKey(slot);

	public bool Has(CBasePlayerController controller) => Has(controller.Slot);

	public bool Remove(int slot) => _data.Remove(slot);

	public bool Remove(CBasePlayerController controller) => Remove(controller.Slot);

	/// <summary>Removes every entry.</summary>
	public void Clear() => _data.Clear();

	/// <summary>The stored values, in no particular order. Do not add or remove entries while iterating.</summary>
	public IEnumerable<T> Values => _data.Values;

	/// <summary>The slots that currently have an entry.</summary>
	public IEnumerable<int> Slots => _data.Keys;

	void IPlayerData.OnDisconnect(int slot) => _data.Remove(slot);

	void IPlayerData.OnReset() => _data.Clear();

	/// <summary>Enumerates stored entries as (slot, value) pairs. Do not add or remove entries while iterating.</summary>
	public IEnumerator<KeyValuePair<int, T>> GetEnumerator() => _data.GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Internal interface for slot-keyed player data stores, allowing cleanup on disconnect and server reset.</summary>
public interface IPlayerData {
	void OnDisconnect(int slot);
	void OnReset();
}

/// <summary>
/// Global registry of all active <see cref="PlayerData{T}"/> stores. Notifies them when a player
/// disconnects and when the server restarts so stale entries never outlive the player they belong to.
/// </summary>
public static class PlayerDataRegistry {
	private static readonly List<WeakReference<IPlayerData>> _stores = new();

	internal static void Register(IPlayerData store) {
		lock (_stores) _stores.Add(new WeakReference<IPlayerData>(store));
	}

	/// <summary>Called by the host after plugin OnClientDisconnect handlers have run, so those handlers can still read the player's data.</summary>
	internal static void OnDisconnect(int slot) => ForEach(store => store.OnDisconnect(slot));

	/// <summary>Called by the host when the server restarts and all slots are reset.</summary>
	internal static void OnReset() => ForEach(store => store.OnReset());

	private static void ForEach(Action<IPlayerData> action) {
		lock (_stores) {
			for (int i = _stores.Count - 1; i >= 0; i--) {
				if (_stores[i].TryGetTarget(out var store))
					action(store);
				else
					_stores.RemoveAt(i);
			}
		}
	}
}
