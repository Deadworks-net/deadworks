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
}
