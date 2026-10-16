using System.Reflection;
using System.Runtime.InteropServices;

namespace Cyborgroid;

/// <summary>P/Invoke surface of the Rust core (libcyborgroid_core.so).</summary>
internal static unsafe partial class Native
{
    private const string Lib = "cyborgroid_core";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return 0;
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("CYBORGROID_CORE"),
            Path.Combine(AppContext.BaseDirectory, "libcyborgroid_core.so"),
            "/opt/cyborgroid/bin/libcyborgroid_core.so",
        };
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c) && NativeLibrary.TryLoad(c, out var h))
                return h;
        return 0;
    }

    [DllImport(Lib)] private static extern nint cy_version();
    [DllImport(Lib)] private static extern nint cy_last_error();
    [DllImport(Lib)] private static extern void cy_string_free(nint p);

    [DllImport(Lib)]
    public static extern int cy_engine_start(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string model,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
        ushort port, uint context, uint threads, int gpuLayers,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string logPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string extraArgs,
        uint startupTimeoutSecs);

    [DllImport(Lib)] public static extern int cy_engine_stop();
    [DllImport(Lib)] public static extern int cy_engine_health([MarshalAs(UnmanagedType.LPUTF8Str)] string host, ushort port);

    [DllImport(Lib)]
    public static extern int cy_chat_stream(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host, ushort port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string bodyJson,
        delegate* unmanaged<int, byte*, nint, void> callback, nint user);

    [DllImport(Lib)] public static extern void cy_cancel();
    [DllImport(Lib)] private static extern nint cy_sha256_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib)] public static extern int cy_network_online();
    [DllImport(Lib)] private static extern nint cy_network_describe();

    private static string? Take(nint p)
    {
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { cy_string_free(p); }
    }

    public static string Version => Take(cy_version()) ?? "?";
    public static string LastError => Take(cy_last_error()) ?? "";
    public static string NetworkDescription => Take(cy_network_describe()) ?? "";
    public static bool NetworkOnline => cy_network_online() == 1;

    public static string? Sha256File(string path) => Take(cy_sha256_file(path));

    public static bool IsAvailable()
    {
        try { _ = Version; return true; }
        catch (DllNotFoundException) { return false; }
    }
}
