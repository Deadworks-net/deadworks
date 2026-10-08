using DeadworksManaged.Api;

namespace DeadworksManaged.Game;

/// <summary>
/// One schema field of one class, named as the game names it. The offset is looked up
/// in the running game the first time the field is used, so generated code keeps working
/// across game updates that move fields.
/// </summary>
public sealed unsafe class SchemaField {
	private volatile int _offset = -1;
	private short _chainOffset;
	private bool _networked;
	private bool _networkingKnown;

	/// <summary>The class that declares the field, e.g. <c>CBaseEntity</c>.</summary>
	public string ClassName { get; }

	/// <summary>The field's name, e.g. <c>m_iHealth</c>.</summary>
	public string FieldName { get; }

	public SchemaField(string className, string fieldName) {
		ClassName = className;
		FieldName = fieldName;
	}

	/// <summary>Byte offset of the field inside its class in the running game.</summary>
	/// <exception cref="SchemaFieldMissingException">The running game has no such field.</exception>
	public int Offset { get { if (_offset < 0) Resolve(); return _offset; } }

	/// <summary>True if the game networks this field, so writes must be announced.</summary>
	public bool Networked { get { if (_offset < 0 || !_networkingKnown) Resolve(); return _networked; } }

	/// <summary>Offset of the declaring class's network chain, or 0 if it has none.</summary>
	internal short ChainOffset { get { if (_offset < 0) Resolve(); return _chainOffset; } }

	/// <summary>Looks the field up without throwing. False if the running game does not have it.</summary>
	public bool TryResolve() {
		return _offset >= 0 || Lookup();
	}

	private void Resolve() {
		if (!Lookup())
			throw new SchemaFieldMissingException(ClassName, FieldName);
	}

	private bool Lookup() {
		Span<byte> cls = Utf8.Encode(ClassName, stackalloc byte[Utf8.Size(ClassName)]);
		Span<byte> fld = Utf8.Encode(FieldName, stackalloc byte[Utf8.Size(FieldName)]);
		SchemaFieldResult r;
		fixed (byte* c = cls, f = fld)
			NativeInterop.GetSchemaField(c, f, &r);
		if (r.Found == SchemaFieldResult.Missing)
			return false;
		_chainOffset = r.ChainOffset;
		_networked = r.Networked != 0;   // not known yet counts as networked, and is asked again
		_networkingKnown = r.Networked != SchemaFieldResult.NetworkingUnknown;
		_offset = r.Offset; // volatile write last - publishes the others
		return true;
	}

	public override string ToString() => $"{ClassName}.{FieldName}";
}

/// <summary>
/// Thrown when generated code uses a schema field the running game does not have: the
/// field was renamed or removed after the build <see cref="GameBuild.Version"/> this
/// assembly was generated from, or the server runs an older build.
/// </summary>
public sealed class SchemaFieldMissingException : Exception {
	public string ClassName { get; }
	public string FieldName { get; }

	public SchemaFieldMissingException(string className, string fieldName)
		: base($"The running game has no schema field {className}.{fieldName}. DeadworksManaged.Game was generated from build {GameBuild.Version}; "
			+ $"https://deadworks.net/db/schema/server/{Uri.EscapeDataString(className)} shows the class as it is now and what changed.") {
		ClassName = className;
		FieldName = fieldName;
	}
}
