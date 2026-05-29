namespace DeadworksManaged.Api;

/// <summary>Discovery helpers for optional capabilities provided by this Deadworks native runtime.</summary>
public static unsafe class NativeFeatures
{
    public static uint NativeApiVersion => NativeInterop.GetNativeApiVersion != null ? NativeInterop.GetNativeApiVersion() : 0;

    public static bool HasCapability(string capabilityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityName);
        if (NativeInterop.HasNativeCapability == null)
            return false;

        Span<byte> utf8 = Utf8.Encode(capabilityName, stackalloc byte[Utf8.Size(capabilityName)]);
        fixed (byte* ptr = utf8)
        {
            return NativeInterop.HasNativeCapability(ptr) != 0;
        }
    }
}

public enum UsercmdNativeMode
{
    DefaultFromEnvironment = -1,
    SerializeManaged = 0,
    Off = 1,
    CountOnly = 2,
    DirectSample = 3,
    FastDispatch = 4,
    MountedPolicy = 5,
}

[Flags]
public enum UsercmdMount : uint
{
    None = 0,
    FullProtobuf = 1u << 0,
    FastRead = 1u << 1,
    ButtonTriggers = 1u << 2,
    All = FullProtobuf | FastRead | ButtonTriggers,
}

[Flags]
public enum UsercmdFields : uint
{
    None = 0,
    ClientTick = 1u << 0,
    Buttons = 1u << 1,
    ViewAngles = 1u << 2,
    Movement = 1u << 3,
    All = ClientTick | Buttons | ViewAngles | Movement,
}

/// <summary>
/// Controls the native usercmd hook. The mounted policy is the production path:
/// with no mounts it only counts batches, and it only performs protobuf/direct reads
/// for features explicitly mounted by a plugin or environment variable.
/// </summary>
public static unsafe class Usercmds
{
    /// <summary>Mounts the read-only fast native field extraction path.</summary>
    public static void EnableFastRead() => Visit(UsercmdFields.All);

    /// <summary>Mounts the read-only fast native field extraction path for the requested fields.</summary>
    public static void Visit(UsercmdFields fields)
    {
        SetFieldMask(fields == UsercmdFields.None ? UsercmdFields.All : fields);
        Mount(UsercmdMount.FastRead);
        SetNativeMode(UsercmdNativeMode.MountedPolicy);
    }

    /// <summary>Mounts the legacy full-protobuf path used by <see cref="IDeadworksPlugin.OnProcessUsercmds"/>.</summary>
    public static void MountFullProtobuf()
    {
        Mount(UsercmdMount.FullProtobuf);
        SetNativeMode(UsercmdNativeMode.MountedPolicy);
    }

    /// <summary>Mounts native edge-trigger callbacks for the supplied buttons.</summary>
    public static void MountButtonTriggers(InputButton buttons)
    {
        SetButtonTriggerMask(buttons);
        Mount(UsercmdMount.ButtonTriggers);
        SetNativeMode(UsercmdNativeMode.MountedPolicy);
    }

    public static void Mount(UsercmdMount features) => SetMountMask(GetMountMask() | features);

    public static void Unmount(UsercmdMount features) => SetMountMask(GetMountMask() & ~features);

    public static void SetMountMask(UsercmdMount mount)
    {
        if (NativeInterop.SetUsercmdMountMask == null)
            throw new NotSupportedException("Native usercmd mount control is not available in this Deadworks build.");
        NativeInterop.SetUsercmdMountMask((uint)mount);
    }

    public static UsercmdMount GetMountMask()
    {
        if (NativeInterop.GetUsercmdMountMask == null)
            return UsercmdMount.None;
        return (UsercmdMount)NativeInterop.GetUsercmdMountMask();
    }

    public static void SetButtonTriggerMask(InputButton buttons)
    {
        if (NativeInterop.SetUsercmdButtonTriggerMask == null)
            throw new NotSupportedException("Native usercmd trigger control is not available in this Deadworks build.");
        NativeInterop.SetUsercmdButtonTriggerMask((ulong)buttons);
    }

    public static InputButton GetButtonTriggerMask()
    {
        if (NativeInterop.GetUsercmdButtonTriggerMask == null)
            return InputButton.None;
        return (InputButton)NativeInterop.GetUsercmdButtonTriggerMask();
    }

    public static void SetFieldMask(UsercmdFields fields)
    {
        if (NativeInterop.SetUsercmdFieldMask == null)
            throw new NotSupportedException("Native usercmd field-mask control is not available in this Deadworks build.");
        NativeInterop.SetUsercmdFieldMask((uint)fields);
    }

    public static UsercmdFields GetFieldMask()
    {
        if (NativeInterop.GetUsercmdFieldMask == null)
            return UsercmdFields.All;
        return (UsercmdFields)NativeInterop.GetUsercmdFieldMask();
    }

    public static void SetNativeMode(UsercmdNativeMode mode)
    {
        if (NativeInterop.SetUsercmdNativeMode == null)
            throw new NotSupportedException("Native usercmd mode control is not available in this Deadworks build.");
        NativeInterop.SetUsercmdNativeMode((int)mode);
    }

    public static UsercmdNativeMode GetNativeMode()
    {
        if (NativeInterop.GetUsercmdNativeMode == null)
            return UsercmdNativeMode.SerializeManaged;
        return (UsercmdNativeMode)NativeInterop.GetUsercmdNativeMode();
    }
}
