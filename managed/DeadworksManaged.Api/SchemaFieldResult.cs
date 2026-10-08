using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

[StructLayout(LayoutKind.Sequential)]
internal struct SchemaFieldResult
{
	public int Offset;
	public short ChainOffset;
	/// <summary>0 not networked, 1 networked, <see cref="NetworkingUnknown"/> when the game cannot be asked yet.</summary>
	public byte Networked;
	public byte Pad;

	/// <summary>Before any entity exists the game cannot say what it networks: treat as networked and ask again.</summary>
	public const byte NetworkingUnknown = 2;
}
