using System.Drawing;
using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// A glowing line between two points in the world that every player can see. Create one with <see cref="Create"/>,
/// or a whole box or path at once with <see cref="CreateBox"/> and <see cref="CreatePolyline"/>.
/// </summary>
/// <remarks>
/// <para>
/// Beams glow like a laser: they brighten whatever is behind them, so they always look a little see-through, and
/// the world's lighting shades them. They can't be made solid or fullbright. Black beams are invisible, and lowering
/// the alpha makes a beam fainter. Change the color at any time with <see cref="CBaseEntity.RenderColor"/>.
/// </para>
/// <para>
/// A beam stays until you remove it (<see cref="CBaseEntity.Remove"/>, or <see cref="RemoveAll"/> for a list) or
/// the map changes.
/// </para>
/// </remarks>
[NativeClass("CBeam")]
public sealed unsafe class CBeam : CBaseEntity {
	internal CBeam(nint handle) : base(handle) { }

	/// <summary>The widest a beam can be. <see cref="Width"/> caps anything wider to this.</summary>
	public const float MaxWidth = 102.3f;

	private static readonly SchemaAccessor<Vector3> _vecEndPos = new("CBeam"u8, "m_vecEndPos"u8);
	private static readonly SchemaAccessor<float> _fWidth = new("CBeam"u8, "m_fWidth"u8);

	/// <summary>
	/// Where the beam starts, which is also its <see cref="CBaseEntity.Position"/>. Move it with
	/// <see cref="SetStartPosition"/> or <see cref="SetEndpoints"/>.
	/// </summary>
	public Vector3 StartPosition => Position;

	/// <summary>Where the beam ends.</summary>
	public Vector3 EndPosition {
		get => _vecEndPos.Get(Handle);
		set => _vecEndPos.Set(Handle, value);
	}

	/// <summary>How thick the beam is, from 0 up to <see cref="MaxWidth"/>.</summary>
	public float Width {
		get => _fWidth.Get(Handle);
		set => _fWidth.Set(Handle, Math.Clamp(value, 0f, MaxWidth));
	}

	/// <summary>Moves the start of the beam, leaving the end where it is.</summary>
	public void SetStartPosition(Vector3 position) => Teleport(position: position);

	/// <summary>Moves both ends of the beam at once.</summary>
	public void SetEndpoints(Vector3 start, Vector3 end) {
		SetStartPosition(start);
		EndPosition = end;
	}

	/// <summary>Spawns a beam from <paramref name="start"/> to <paramref name="end"/>.</summary>
	/// <param name="start">Where the beam starts.</param>
	/// <param name="end">Where the beam ends.</param>
	/// <param name="width">How thick it is, up to <see cref="MaxWidth"/>.</param>
	/// <param name="color">Its color, white if not given. Black is invisible, and a lower alpha makes it fainter.</param>
	/// <returns>The new beam, or <see langword="null"/> if the game couldn't create it.</returns>
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
	/// Outlines a box with a beam along each of its 12 edges, for example to show a zone or trigger area.
	/// </summary>
	/// <param name="mins">One corner of the box.</param>
	/// <param name="maxs">The opposite corner. The two corners can be given in either order.</param>
	/// <param name="width">How thick each beam is, up to <see cref="MaxWidth"/>.</param>
	/// <param name="color">Their color, white if not given.</param>
	/// <returns>The beams, to remove later with <see cref="RemoveAll"/>. Any that failed to spawn are left out.</returns>
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

	/// <summary>Draws a path through <paramref name="points"/>, one beam per segment.</summary>
	/// <param name="points">The points to join, in order. Needs at least two.</param>
	/// <param name="width">How thick each beam is, up to <see cref="MaxWidth"/>.</param>
	/// <param name="color">Their color, white if not given.</param>
	/// <param name="closed">Also joins the last point back to the first, for example to draw a ring.</param>
	/// <returns>The beams, to remove later with <see cref="RemoveAll"/>. Any that failed to spawn are left out.</returns>
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

	/// <summary>Removes every beam in <paramref name="beams"/> that still exists, then empties the list.</summary>
	public static void RemoveAll(List<CBeam> beams) {
		foreach (var beam in beams)
			if (beam.IsValid) beam.Remove();
		beams.Clear();
	}
}
