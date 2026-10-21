using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>A lane trooper (<c>npc_trooper</c>). Guardians and walkers are separate classes.</summary>
/// <remarks>
/// The game reuses troopers: hidden ones wait <see cref="CAI_BaseNPC.IsDormant"/> and come back as new spawns. A
/// stored trooper can return as a different spawn, on another team or lane.
/// </remarks>
[NativeClass("CNPC_Trooper")]
public sealed unsafe class CNPC_Trooper : CAI_CitadelNPC {
	internal CNPC_Trooper(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CNPC_Trooper"u8;

	private static readonly SchemaAccessor<int> _lane = new(Class, "m_iLane"u8);
	private static readonly SchemaAccessor<bool> _usingBossWeapon = new(Class, "m_bUsingBossWeapon"u8);

	/// <summary>The lane the trooper walks. 0 means any lane.</summary>
	public int Lane => _lane.Get(Handle);

	/// <summary>True while the trooper uses its anti-boss weapon against a guardian or walker.</summary>
	public bool UsingBossWeapon => _usingBossWeapon.Get(Handle);

	/// <summary>
	/// Spawns a trooper, as <c>citadel_spawn_trooper</c> does, without cheats. Returns null if it could not be
	/// spawned.
	/// </summary>
	/// <remarks>
	/// The trooper joins the lane nearest <paramref name="position"/> and walks it. A free dormant trooper of the same
	/// type is reused if there is one.
	/// </remarks>
	/// <param name="position">Where the trooper appears.</param>
	/// <param name="team">2 Amber, 3 Sapphire.</param>
	/// <param name="type">The kind of trooper.</param>
	/// <param name="angles">Pitch, yaw, roll. Null faces +X.</param>
	/// <param name="snapToGround">Drop it onto the ground below <paramref name="position"/>.</param>
	public static CNPC_Trooper? Create(Vector3 position, int team, TrooperType type = TrooperType.Normal, Vector3? angles = null, bool snapToGround = true) {
		float* pos = stackalloc float[3] { position.X, position.Y, position.Z };
		float* ang = stackalloc float[3];
		if (angles is { } a) {
			ang[0] = a.X;
			ang[1] = a.Y;
			ang[2] = a.Z;
		}
		void* trooper = NativeInterop.NpcCreateTrooper(pos, angles != null ? ang : null, team, (int)type, Random.Shared.Next(4), snapToGround ? (byte)1 : (byte)0);
		return trooper != null ? new CNPC_Trooper((nint)trooper) : null;
	}
}
