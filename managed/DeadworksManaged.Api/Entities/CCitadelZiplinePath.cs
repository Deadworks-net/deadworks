namespace DeadworksManaged.Api;

/// <summary>
/// A zipline rope placed in the map (citadel_zipline_path). Each lane has its own, so a map's ropes tell you which lanes
/// it has:
/// <code>
/// var lanes = Entities.ByClass&lt;CCitadelZiplinePath&gt;()
///     .Select(rope => rope.Lane)
///     .Where(lane => lane != LaneColor.Invalid)
///     .ToHashSet();
/// </code>
/// dl_midtown has Yellow, Blue and Purple, dl_harbor Yellow and Blue. Map entities don't exist yet during
/// <see cref="IDeadworksPlugin.OnStartupServer"/>, so look them up later.
/// </summary>
[NativeClass("CCitadelZiplinePath")]
public sealed unsafe class CCitadelZiplinePath : CBaseEntity {
	internal CCitadelZiplinePath(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<int> _laneNumber = new("CCitadelZiplinePath"u8, "m_iLaneNumber"u8);
	/// <summary>
	/// The lane this rope runs along, or <see cref="LaneColor.Invalid"/> for a rope that isn't part of a lane.
	/// </summary>
	public LaneColor Lane => (LaneColor)_laneNumber.Get(Handle);
}
