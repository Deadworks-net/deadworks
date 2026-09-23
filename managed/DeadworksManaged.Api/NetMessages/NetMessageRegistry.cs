using System.Reflection;
using System.Runtime.InteropServices;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DeadworksManaged.Api;

/// <summary>
/// Maps protobuf message types to the network message IDs the engine registered them under.
/// Used internally by the net message send/hook system; also exposes manual registration for custom message types.
/// </summary>
public static unsafe class NetMessageRegistry
{
	private static readonly Dictionary<Type, int> s_typeToId = new();
	private static readonly Dictionary<int, MessageParser> s_idToParser = new();
	private static bool s_initialized;

	internal static void EnsureInitialized()
	{
		if (s_initialized) return;
		s_initialized = true;

		BuildRegistry();
	}

	/// <summary>Returns the network message ID for <typeparamref name="T"/>, or <c>-1</c> if not registered.</summary>
	/// <typeparam name="T">A protobuf message type.</typeparam>
	public static int GetMessageId<T>() where T : IMessage<T>
	{
		EnsureInitialized();
		return s_typeToId.TryGetValue(typeof(T), out var id) ? id : -1;
	}

	/// <summary>Returns the network message ID for the given protobuf message type, or <c>-1</c> if not registered.</summary>
	/// <param name="type">A protobuf message type.</param>
	public static int GetMessageId(Type type)
	{
		EnsureInitialized();
		return s_typeToId.TryGetValue(type, out var id) ? id : -1;
	}

	internal static MessageParser? GetParser(int messageId)
	{
		EnsureInitialized();
		return s_idToParser.TryGetValue(messageId, out var parser) ? parser : null;
	}

	private static void BuildRegistry()
	{
		var assembly = typeof(NetMessageRegistry).Assembly;

		// Build lookup of proto message types by their proto name (e.g. "CCitadelUserMsg_ChatMsg")
		var typesByProtoName = new Dictionary<string, Type>(StringComparer.Ordinal);
		foreach (var type in assembly.GetTypes())
		{
			if (!typeof(IMessage).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
				continue;

			// Get the protobuf descriptor to find the original proto message name
			var descriptorProp = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
			if (descriptorProp?.GetValue(null) is MessageDescriptor desc)
			{
				typesByProtoName[desc.Name] = type;
			}
		}

		// The engine knows which message each ID carries, including those whose name does not follow
		// their ID enum: k_EUserMsg_Damage carries CCitadelUserMessage_Damage and k_EUserMsg_ChatWheel
		// carries CCitadelUserMsg_PingWheel. Without an engine to ask, guess from the ID enums.
		if (RegisterEngineMessages(typesByProtoName)) return;

		// Scan all FileDescriptors for enum types that map message IDs to names
		var visitedFiles = new HashSet<string>();
		foreach (var type in assembly.GetTypes())
		{
			var reflectionProp = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
			if (reflectionProp == null) continue;

			FileDescriptor? fileDesc = null;
			var val = reflectionProp.GetValue(null);
			if (val is MessageDescriptor md) fileDesc = md.File;
			else if (val is FileDescriptor fd) fileDesc = fd;

			if (fileDesc == null || !visitedFiles.Add(fileDesc.Name)) continue;

			foreach (var enumDesc in fileDesc.EnumTypes)
			{
				ProcessEnumDescriptor(enumDesc, typesByProtoName);
			}
		}

	}

	private static bool RegisterEngineMessages(Dictionary<string, Type> typesByProtoName)
	{
		if (NativeInterop.GetNetMessageName == null) return false;

		bool any = false;
		for (int id = 0; id <= short.MaxValue; id++)
		{
			byte* name = NativeInterop.GetNetMessageName(id);
			if (name == null) continue;
			any = true;

			// The engine's name carries the ID: "CCitadelUserMsg_ChatMsg [314]"
			var protoName = Marshal.PtrToStringUTF8((nint)name) ?? "";
			int bracket = protoName.IndexOf(" [", StringComparison.Ordinal);
			if (bracket >= 0) protoName = protoName[..bracket];

			if (typesByProtoName.TryGetValue(protoName, out var type))
			{
				Register(type, id);
			}
		}
		return any;
	}

	private static void ProcessEnumDescriptor(EnumDescriptor enumDesc, Dictionary<string, Type> typesByProtoName)
	{
		// Known enum->message name mapping rules based on original proto names:
		// CitadelUserMessageIds:  k_EUserMsg_Foo   -> CCitadelUserMsg_Foo
		// ECitadelClientMessages: CITADEL_CM_Foo   -> CCitadelClientMsg_Foo
		// EBaseUserMessages:      k_EUserMsg_Foo   -> CUserMessageFoo
		// EBaseEntityMessages:    k_EEntityMsg_Foo -> CEntityMessageFoo
		// CLC_Messages:           clc_Foo          -> CCLCMsg_Foo
		// SVC_Messages:           svc_Foo          -> CSVCMsg_Foo
		// EBaseGameEvents:        GE_Foo           -> various

		Func<string, string?>? mapper = enumDesc.Name switch
		{
			"NET_Messages" => MapNetMsg,
			"CitadelUserMessageIds" => MapCitadelUserMsg,
			"ECitadelClientMessages" => MapCitadelClientMsg,
			"EBaseUserMessages" => MapBaseUserMsg,
			"EBaseEntityMessages" => MapBaseEntityMsg,
			"CLC_Messages" => MapClcMsg,
			"SVC_Messages" => MapSvcMsg,
			"EBaseGameEvents" => MapBaseGameEvent,
			"SVC_Messages_LowFrequency" => MapSvcMsgLowFreq,
			"Bidirectional_Messages" => MapBidirectionalMsg,
			"Bidirectional_Messages_LowFrequency" => MapBidirectionalMsgLowFreq,
			"ETEProtobufIds" => MapTEMsg,
			_ => null
		};

		if (mapper == null) return;

		foreach (var value in enumDesc.Values)
		{
			var className = mapper(value.Name);
			if (className == null) continue;

			if (typesByProtoName.TryGetValue(className, out var type))
			{
				Register(type, value.Number);
			}
		}
	}

	private static void Register(Type messageType, int id)
	{
		if (s_typeToId.ContainsKey(messageType)) return;

		var parserProp = messageType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static);
		if (parserProp?.GetValue(null) is not MessageParser parser) return;

		s_typeToId[messageType] = id;
		s_idToParser[id] = parser;
	}

	// k_EUserMsg_Foo -> CCitadelUserMsg_Foo
	private static string? MapCitadelUserMsg(string name)
	{
		if (!name.StartsWith("k_EUserMsg_")) return null;
		return "CCitadelUserMsg_" + name["k_EUserMsg_".Length..];
	}

	// CITADEL_CM_Foo -> CCitadelClientMsg_Foo
	private static string? MapCitadelClientMsg(string name)
	{
		if (!name.StartsWith("CITADEL_CM_")) return null;
		return "CCitadelClientMsg_" + name["CITADEL_CM_".Length..];
	}

	// UM_Foo -> CUserMessageFoo
	private static string? MapBaseUserMsg(string name)
	{
		if (!name.StartsWith("UM_")) return null;
		return "CUserMessage" + name["UM_".Length..];
	}

	// EM_Foo -> CEntityMessageFoo
	private static string? MapBaseEntityMsg(string name)
	{
		if (!name.StartsWith("EM_")) return null;
		return "CEntityMessage" + name["EM_".Length..];
	}

	// clc_Foo -> CCLCMsg_Foo
	private static string? MapClcMsg(string name)
	{
		if (!name.StartsWith("clc_")) return null;
		return "CCLCMsg_" + name["clc_".Length..];
	}

	// svc_Foo -> CSVCMsg_Foo
	private static string? MapSvcMsg(string name)
	{
		if (!name.StartsWith("svc_")) return null;
		return "CSVCMsg_" + name["svc_".Length..];
	}

	// svc_Foo -> CSVCMsg_Foo (low frequency)
	private static string? MapSvcMsgLowFreq(string name)
	{
		if (!name.StartsWith("svc_")) return null;
		return "CSVCMsg_" + name["svc_".Length..];
	}

	// bi_Foo -> CBidirMsg_Foo
	private static string? MapBidirectionalMsg(string name)
	{
		if (!name.StartsWith("bi_")) return null;
		return "CBidirMsg_" + name["bi_".Length..];
	}

	// bi_Foo -> CBidirMsg_Foo (low frequency)
	private static string? MapBidirectionalMsgLowFreq(string name)
	{
		if (!name.StartsWith("bi_")) return null;
		return "CBidirMsg_" + name["bi_".Length..];
	}

	// GE_Foo -> CMsgFoo
	private static string? MapBaseGameEvent(string name)
	{
		if (!name.StartsWith("GE_")) return null;
		return "CMsg" + name["GE_".Length..];
	}

	// TE_Foo -> CMsgTEFoo
	private static string? MapTEMsg(string name)
	{
		if (!name.StartsWith("TE_")) return null;
		return "CMsgTE" + name["TE_".Length..];
	}

	// net_Foo -> CNETMsg_Foo
	private static string? MapNetMsg(string name)
	{
		if (!name.StartsWith("net_")) return null;
		return "CNETMsg_" + name["net_".Length..];
	}

	/// <summary>Manually registers a protobuf message type with a specific network message ID, bypassing the automatic enum-based discovery.</summary>
	/// <typeparam name="T">The protobuf message type to register.</typeparam>
	/// <param name="messageId">The network message ID to associate with <typeparamref name="T"/>.</param>
	public static void RegisterManual<T>(int messageId) where T : IMessage<T>, new()
	{
		EnsureInitialized();
		Register(typeof(T), messageId);
	}
}
