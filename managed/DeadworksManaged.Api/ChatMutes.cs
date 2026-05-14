namespace DeadworksManaged.Api;

/// <summary>Shared chat mute state used by the host chat dispatcher and plugins.</summary>
public static class ChatMutes
{
	private const string DefaultSource = "default";
	private static readonly Lock Lock = new();
	private static readonly Dictionary<string, Dictionary<ulong, ChatMuteInfo>> MutesBySource = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Adds or replaces a chat mute from the default source.</summary>
	public static void SetMuted(ChatMuteInfo mute) => SetMuted(DefaultSource, mute);

	/// <summary>Adds or replaces a chat mute from the given source.</summary>
	public static void SetMuted(string source, ChatMuteInfo mute)
	{
		lock (Lock)
		{
			GetOrCreateSource(source)[mute.SteamId64] = mute;
		}
	}

	/// <summary>Removes all chat mutes for the given SteamID64, regardless of source.</summary>
	public static bool Remove(ulong steamId64)
	{
		lock (Lock)
		{
			var removed = false;
			foreach (var sourceMutes in MutesBySource.Values)
				removed |= sourceMutes.Remove(steamId64);
			return removed;
		}
	}

	/// <summary>Removes a chat mute for the given SteamID64 from one source.</summary>
	public static bool Remove(string source, ulong steamId64)
	{
		lock (Lock)
		{
			return MutesBySource.TryGetValue(source, out var sourceMutes) && sourceMutes.Remove(steamId64);
		}
	}

	/// <summary>Returns whether the given SteamID64 currently has an active chat mute.</summary>
	public static bool IsMuted(ulong steamId64) => TryGetMute(steamId64, out _);

	/// <summary>Gets the active chat mute for the given SteamID64, pruning expired entries.</summary>
	public static bool TryGetMute(ulong steamId64, out ChatMuteInfo mute)
	{
		lock (Lock)
		{
			foreach (var sourceMutes in MutesBySource.Values)
			{
				if (!sourceMutes.TryGetValue(steamId64, out var existing))
					continue;

				if (existing.ExpiresAtUtc != null && existing.ExpiresAtUtc <= DateTimeOffset.UtcNow)
				{
					sourceMutes.Remove(steamId64);
					continue;
				}

				mute = existing;
				return true;
			}

			mute = null!;
			return false;
		}
	}

	/// <summary>Replaces all default-source chat mutes.</summary>
	public static void ReplaceAll(IEnumerable<ChatMuteInfo> mutes) => ReplaceAll(DefaultSource, mutes);

	/// <summary>Replaces all chat mutes owned by the given source.</summary>
	public static void ReplaceAll(string source, IEnumerable<ChatMuteInfo> mutes)
	{
		lock (Lock)
		{
			var sourceMutes = GetOrCreateSource(source);
			sourceMutes.Clear();
			foreach (var mute in mutes)
			{
				if (mute.ExpiresAtUtc == null || mute.ExpiresAtUtc > DateTimeOffset.UtcNow)
					sourceMutes[mute.SteamId64] = mute;
			}
		}
	}

	/// <summary>Clears every chat mute from every source.</summary>
	public static void Clear()
	{
		lock (Lock)
		{
			MutesBySource.Clear();
		}
	}

	/// <summary>Clears every chat mute owned by the given source.</summary>
	public static void Clear(string source)
	{
		lock (Lock)
		{
			MutesBySource.Remove(source);
		}
	}

	private static Dictionary<ulong, ChatMuteInfo> GetOrCreateSource(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
			source = DefaultSource;

		if (!MutesBySource.TryGetValue(source, out var sourceMutes))
		{
			sourceMutes = new Dictionary<ulong, ChatMuteInfo>();
			MutesBySource[source] = sourceMutes;
		}

		return sourceMutes;
	}
}
