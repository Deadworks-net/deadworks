namespace DeadworksManaged.Api;

/// <summary>Source 2 datadesc field type — populated by the native variant_t carried by an entity I/O hook.</summary>
/// <remarks>Mirrors <c>fieldtype_t</c> from <c>sourcesdk/public/datamap.h</c>. Only the types that actually appear in I/O variants are commonly seen.</remarks>
public enum FieldType : byte {
	Void = 0,
	Float32 = 1,
	String = 2,            // string_t
	Vector = 3,
	Quaternion = 4,
	Int32 = 5,
	Boolean = 6,
	Int16 = 7,
	Character = 8,
	Color32 = 9,
	ClassPtr = 12,         // CBaseEntity *
	EHandle = 13,
	PositionVector = 14,
	Time = 15,
	Tick = 16,
	SoundName = 17,
	Vector2D = 25,
	Int64 = 26,
	Vector4D = 27,
	Resource = 28,
	CString = 30,          // const char *
	HScript = 31,
	Variant = 32,
	UInt64 = 33,
	Float64 = 34,
	UInt32 = 37,
	UtlStringToken = 38,
	QAngle = 39,
}
