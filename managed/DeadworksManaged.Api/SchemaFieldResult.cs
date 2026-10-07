using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

[StructLayout(LayoutKind.Sequential)]
internal struct SchemaFieldResult
{
	public int Offset;
	public short ChainOffset;
	public byte Networked;
	/// <summary><see cref="Present"/> or <see cref="Missing"/>; 0 from a native core that predates the flag.</summary>
	public byte Found;

	public const byte Present = 1;
	public const byte Missing = 2;
}
