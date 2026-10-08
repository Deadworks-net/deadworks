namespace DeadworksManaged.Api;

/// <summary>
/// A lane's zipline rope (<c>citadel_zipline_path</c>). A map's ropes tell you which lanes it has:
/// <code>
/// var lanes = Entities.ByClass&lt;CCitadelZiplinePath&gt;()
///     .Select(rope => rope.Lane)
///     .Where(lane => lane != LaneColor.Invalid)
///     .ToHashSet();
/// </code>
/// Map entities don't exist yet during <see cref="IDeadworksPlugin.OnStartupServer"/>.
/// </summary>
[NativeClass("CCitadelZiplinePath")]
public sealed unsafe class CCitadelZiplinePath : CBaseEntity {
	internal CCitadelZiplinePath(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<int> _laneNumber = new("CCitadelZiplinePath"u8, "m_iLaneNumber"u8);
	/// <summary>The rope's lane, or <see cref="LaneColor.Invalid"/> if it isn't part of one.</summary>
	public LaneColor Lane => (LaneColor)_laneNumber.Get(Handle);
}
