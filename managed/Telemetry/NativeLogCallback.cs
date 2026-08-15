namespace DeadworksManaged.Telemetry;

/// <summary>
/// Static holder for the native engine log callback pointer.
/// Set once from <see cref="EntryPoint.Initialize"/> and read by <see cref="NativeEngineLoggerProvider"/>.
/// </summary>
internal static unsafe class NativeLogCallback
{
    private static delegate* unmanaged[Cdecl]<char*, void> _callback;

    public static delegate* unmanaged[Cdecl]<char*, void> Callback => _callback;

    public static void Set(delegate* unmanaged[Cdecl]<char*, void> callback)
    {
        _callback = callback;
    }
}
