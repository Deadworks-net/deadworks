using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// A lane trooper (<c>npc_trooper</c>): the creeps that spawn in waves and walk down a lane. Guardians and
/// walkers are separate classes.
/// </summary>
/// <remarks>
/// The game recycles troopers. Before the first wave it spawns hidden ones and keeps them dormant (see
/// <see cref="CAI_BaseNPC.IsDormant"/>), and new spawns reuse them, so a trooper you stored earlier can come back
/// as a different spawn, possibly on another team or lane.
/// </remarks>
[NativeClass("CNPC_Trooper")]
public sealed unsafe class CNPC_Trooper : CAI_CitadelNPC {
	internal CNPC_Trooper(nint handle) : base(handle) { }
	private static ReadOnlySpan<byte> Class => "CNPC_Trooper"u8;

	private static readonly SchemaAccessor<int> _lane = new(Class, "m_iLane"u8);
	private static readonly SchemaAccessor<bool> _usingBossWeapon = new(Class, "m_bUsingBossWeapon"u8);

	/// <summary>
	/// The lane the trooper walks. 0 means any lane.
	/// </summary>
	public int Lane => _lane.Get(Handle);

	/// <summary>True while the trooper uses its anti-boss weapon, because it's fighting a guardian or walker.</summary>
	public bool UsingBossWeapon => _usingBossWeapon.Get(Handle);

	/// <summary>
	/// Spawns a trooper the way <c>citadel_spawn_trooper</c> does, with no cheats needed. Returns it, or null if it
	/// could not be spawned.
	/// </summary>
	/// <remarks>
	/// The trooper joins the lane nearest to <paramref name="position"/> and walks it like a wave trooper. A dormant
	/// pooled trooper of the same type is reused when one is free, so the returned entity can be one that existed
	/// before.
	/// </remarks>
	/// <param name="position">Where the trooper appears.</param>
	/// <param name="team">The team number (2 Amber, 3 Sapphire).</param>
	/// <param name="type">Which kind of trooper.</param>
	/// <param name="angles">The way it faces (pitch, yaw, roll). Null faces along +X.</param>
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
