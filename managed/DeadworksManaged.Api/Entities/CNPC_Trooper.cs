using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>Lane trooper (<c>npc_trooper</c>). Guardians and walkers are separate classes.</summary>
/// <remarks>
/// The game pools troopers: hidden ones are <see cref="CAI_BaseNPC.IsDormant"/> and come back as new spawns, possibly
/// on another team or lane. A stored reference can become a different trooper.
/// </remarks>
[NativeClass("CNPC_Trooper")]
public sealed unsafe class CNPC_Trooper : CAI_CitadelNPC {
	internal CNPC_Trooper(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CNPC_Trooper"u8;

	private static readonly SchemaAccessor<int> _lane = new(Class, "m_iLane"u8);
	private static readonly SchemaAccessor<bool> _usingBossWeapon = new(Class, "m_bUsingBossWeapon"u8);

	/// <summary>Lane the trooper walks, or 0 for none.</summary>
	public int Lane => _lane.Get(Handle);

	/// <summary>True while the trooper fires its anti-boss weapon at a guardian or walker.</summary>
	public bool UsingBossWeapon => _usingBossWeapon.Get(Handle);

	/// <summary>Spawns a lane trooper, like <c>citadel_spawn_trooper</c>. Returns null on failure.</summary>
	/// <remarks>
	/// The trooper joins the lane nearest <paramref name="position"/>. A free pooled trooper of the same type is
	/// reused.
	/// </remarks>
	/// <param name="position">Spawn position.</param>
	/// <param name="team">2 Amber, 3 Sapphire.</param>
	/// <param name="type">Trooper type.</param>
	/// <param name="angles">Pitch, yaw, roll. Null faces +X.</param>
	/// <param name="snapToGround">Drop it to the ground below <paramref name="position"/>.</param>
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
