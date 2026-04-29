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

        // UI スレッドを占有しないようバックグラウンドで起動
        _ = Task.Run(() => UiCommandHandler.RunAsync(pipeName));
        return 0;
    }
}
