using System.Drawing;
using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// A world-space line rendered by an <c>env_beam</c> entity. Create one with <see cref="Create"/>, or
/// several at once with <see cref="CreateBox"/> and <see cref="CreatePolyline"/>. Beams are the simplest
/// way to draw zone outlines, paths, and debug shapes that every client can see.
/// </summary>
[NativeClass("CBeam")]
public sealed unsafe class CBeam : CBaseEntity {
	internal CBeam(nint handle) : base(handle) { }

	private static readonly SchemaAccessor<Vector3> _vecEndPos = new("CBeam"u8, "m_vecEndPos"u8);
	private static readonly SchemaAccessor<float> _fWidth = new("CBeam"u8, "m_fWidth"u8);
	private static readonly SchemaAccessor<uint> _clrRender = new("CBaseModelEntity"u8, "m_clrRender"u8);

	/// <summary>World position of the beam's first end. This is the entity's origin.</summary>
	public Vector3 StartPosition {
		get => Position;
		set => Teleport(position: value);
	}

	/// <summary>World position of the beam's second end.</summary>
	public Vector3 EndPosition {
		get => _vecEndPos.Get(Handle);
		set => _vecEndPos.Set(Handle, value);
	}

	/// <summary>Beam thickness in world units.</summary>
	public float Width {
		get => _fWidth.Get(Handle);
		set => _fWidth.Set(Handle, value);
	}

	/// <summary>Beam colour and opacity.</summary>
	public Color Color {
		get {
			uint v = _clrRender.Get(Handle);
			return Color.FromArgb((byte)(v >> 24), (byte)v, (byte)(v >> 8), (byte)(v >> 16));
		}
		set => _clrRender.Set(Handle, (uint)(value.R | (value.G << 8) | (value.B << 16) | (value.A << 24)));
	}

	/// <summary>Moves both ends of the beam in one call.</summary>
	public void SetEndpoints(Vector3 start, Vector3 end) {
		StartPosition = start;
		EndPosition = end;
	}

	/// <summary>
	/// Spawns a beam from <paramref name="start"/> to <paramref name="end"/>.
	/// </summary>
	/// <param name="start">World position of the first end.</param>
	/// <param name="end">World position of the second end.</param>
	/// <param name="width">Thickness in world units.</param>
	/// <param name="color">Colour and opacity. Defaults to opaque white.</param>
	/// <returns>The spawned beam, or <see langword="null"/> if the entity could not be created.</returns>
	public static CBeam? Create(Vector3 start, Vector3 end, float width = 1f, Color? color = null) {
		var baseEntity = CreateByName("env_beam");
		if (baseEntity == null) return null;

		var c = color ?? Color.White;
		var beam = new CBeam(baseEntity.Handle);

		var ekv = new CEntityKeyValues();
		ekv.SetColor("rendercolor", c.R, c.G, c.B, c.A);
		ekv.SetInt("renderamt", c.A);

		beam.Teleport(position: start);
		beam.Spawn(ekv);
		beam.Width = width;
		beam.EndPosition = end;
		return beam;
	}

	/// <summary>
	/// Spawns the twelve edges of the axis-aligned box spanning <paramref name="mins"/> to <paramref name="maxs"/>.
	/// Useful for showing trigger volumes and zones. Beams that fail to spawn are skipped.
	/// </summary>
	public static List<CBeam> CreateBox(Vector3 mins, Vector3 maxs, float width = 1f, Color? color = null) {
		Vector3 lo = Vector3.Min(mins, maxs);
		Vector3 hi = Vector3.Max(mins, maxs);

		Span<Vector3> c = stackalloc Vector3[8];
		c[0] = new(lo.X, lo.Y, lo.Z); c[1] = new(hi.X, lo.Y, lo.Z);
		c[2] = new(hi.X, hi.Y, lo.Z); c[3] = new(lo.X, hi.Y, lo.Z);
		c[4] = new(lo.X, lo.Y, hi.Z); c[5] = new(hi.X, lo.Y, hi.Z);
		c[6] = new(hi.X, hi.Y, hi.Z); c[7] = new(lo.X, hi.Y, hi.Z);

		ReadOnlySpan<(int, int)> edges = [
			(0, 1), (1, 2), (2, 3), (3, 0), // bottom
			(4, 5), (5, 6), (6, 7), (7, 4), // top
			(0, 4), (1, 5), (2, 6), (3, 7), // verticals
		];

		var beams = new List<CBeam>(12);
		foreach (var (a, b) in edges) {
			var beam = Create(c[a], c[b], width, color);
			if (beam != null) beams.Add(beam);
		}
		return beams;
	}

	/// <summary>
	/// Spawns one beam between each consecutive pair of <paramref name="points"/>, forming a path.
	/// Pass <paramref name="closed"/> to also connect the last point back to the first.
	/// </summary>
	public static List<CBeam> CreatePolyline(IReadOnlyList<Vector3> points, float width = 1f, Color? color = null, bool closed = false) {
		var beams = new List<CBeam>(Math.Max(0, points.Count - 1));
		for (int i = 1; i < points.Count; i++) {
			var beam = Create(points[i - 1], points[i], width, color);
			if (beam != null) beams.Add(beam);
		}
		if (closed && points.Count > 2) {
			var beam = Create(points[^1], points[0], width, color);
			if (beam != null) beams.Add(beam);
		}
		return beams;
	}

	/// <summary>Removes every beam in <paramref name="beams"/> that still exists and clears the list.</summary>
	public static void RemoveAll(List<CBeam> beams) {
		foreach (var beam in beams)
			if (beam.IsValid) beam.Remove();
		beams.Clear();
	}
}
