using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>Base player controller entity. Manages the link between a player slot and their pawn.</summary>
[NativeClass("CBasePlayerController")]
public unsafe class CBasePlayerController : CBaseEntity {
	internal CBasePlayerController(nint handle) : base(handle) { }

	/// <summary>The 0-based player slot index for this controller (<see cref="CBaseEntity.EntityIndex"/> minus 1).</summary>
	public int Slot => EntityIndex - 1;

	/// <summary>A <see cref="RecipientFilter"/> targeting just this player.</summary>
	public RecipientFilter Recipients => RecipientFilter.Single(Slot);

	/// <summary>Plays a soundevent to just this player at their listener position. Returns the soundevent GUID.</summary>
	public uint PlaySound(string name, float volume = 1f, float pitch = 1f)
		=> Sounds.Sounds.Play(name, Recipients, volume, pitch);

	/// <summary>Kicks this player from the server with an optional reason.</summary>
	public bool Kick(string reason = "Kicked by admin") => Server.KickPlayer(Slot, reason);

	private static readonly SchemaAccessor<byte> _playerName = new("CBasePlayerController"u8, "m_iszPlayerName"u8);

	/// <summary>The player's display name (char[128] inline buffer).</summary>
	public string PlayerName {
		get => Marshal.PtrToStringUTF8(_playerName.GetAddress(Handle)) ?? "";
		set {
			nint addr = _playerName.GetAddress(Handle);
			Span<byte> utf8 = Utf8.Encode(value, stackalloc byte[Utf8.Size(value)]);
			int len = Math.Min(utf8.Length, 127);
			fixed (byte* src = utf8) {
				Buffer.MemoryCopy(src, (void*)addr, 128, len);
			}
			((byte*)addr)[len] = 0;
			NativeInterop.NotifyStateChanged((void*)Handle, _playerName.Offset, _playerName.ChainOffset, 0);
		}
	}

    private static readonly SchemaAccessor<ulong> _playerSteamId = new("CBasePlayerController"u8, "m_steamID"u8);

    /// <summary>The player's SteamID64</summary>
    public ulong PlayerSteamId  => _playerSteamId.Get(Handle);

	private static readonly SchemaAccessor<uint> _hPawn = new("CBasePlayerController"u8, "m_hPawn"u8);

	/// <summary>The controller's current pawn (hero, observer, or any other), or null if none.</summary>
	public CBasePlayerPawn? Pawn {
		get {
			uint handle = _hPawn.Get(Handle);
			if (handle == 0xFFFFFFFF) return null;
			void* ptr = NativeInterop.GetEntityFromHandle(handle);
			return ptr != null ? new CBasePlayerPawn((nint)ptr) : null;
		}
	}

	/// <summary>Assigns a new pawn to this controller, optionally transferring team and movement state.</summary>
	public void SetPawn(CBasePlayerPawn? pawn, bool retainOldPawnTeam = false, bool copyMovementState = false, bool allowTeamMismatch = false, bool preserveMovementState = false) {
		NativeInterop.SetPawn((void*)Handle, pawn != null ? (void*)pawn.Handle : null,
			retainOldPawnTeam ? (byte)1 : (byte)0,
			copyMovementState ? (byte)1 : (byte)0,
			allowTeamMismatch ? (byte)1 : (byte)0,
			preserveMovementState ? (byte)1 : (byte)0);
	}
}
