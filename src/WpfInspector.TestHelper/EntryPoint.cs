using System.Runtime.InteropServices;

namespace WpfInspector.TestHelper;

/// <summary>
/// hostfxr の load_assembly_and_get_function_pointer から呼ばれるエントリポイント。
/// シグネチャは component_entry_point_fn に合わせる必要がある:
///   public static int Method(IntPtr arg, int argSize)
/// </summary>
public static class EntryPoint
{
    public static int Run(IntPtr arg, int argSize)
    {
        // arg は UTF-8 のパイプ名 ("\\\\.\\pipe\\wpfinspector-{PID}")
        var pipeName = Marshal.PtrToStringUTF8(arg, argSize - 1);
        if (string.IsNullOrEmpty(pipeName))
            return -1;

        // 既定の AssemblyLoadContext には小さい host だけを残し、
        // 実作業は collectible ALC にロードした worker へ委譲する。
        CollectibleSessionHost.StartSession(pipeName);
        return 0;
    }

    public static InjectionHostDiagnostics GetDiagnostics() =>
        CollectibleSessionHost.GetDiagnostics();

    public static Task<bool> WaitForIdleAsync(int timeoutMs = 5_000) =>
        CollectibleSessionHost.WaitForIdleAsync(
            TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)));
}
