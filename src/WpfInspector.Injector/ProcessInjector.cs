using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfInspector.Injector;

/// <summary>
/// CreateRemoteThread + LoadLibraryW を使って対象プロセスに
/// ネイティブ DLL を注入する。
/// </summary>
public static class ProcessInjector
{
    /// <summary>
    /// <paramref name="nativeDllPath"/> のフルパスを対象プロセスに注入する。
    /// DLL は x64 でビルドされている必要がある。
    /// </summary>
    public static void Inject(int processId, string nativeDllPath)
    {
        if (!File.Exists(nativeDllPath))
            throw new FileNotFoundException("ネイティブ DLL が見つかりません。", nativeDllPath);

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_ALL_ACCESS, false, processId);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"OpenProcess 失敗 (PID {processId})");

        try
        {
            InjectDll(handle, nativeDllPath);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static void InjectDll(IntPtr processHandle, string dllPath)
    {
        // DLL パスを Unicode 文字列として対象プロセスのメモリに書き込む
        var pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        var remoteBuffer = NativeMethods.VirtualAllocEx(
            processHandle,
            IntPtr.Zero,
            (nuint)pathBytes.Length,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
            NativeMethods.PAGE_READWRITE);

        if (remoteBuffer == IntPtr.Zero)
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "VirtualAllocEx 失敗");

        try
        {
            NativeMethods.WriteProcessMemory(
                processHandle, remoteBuffer, pathBytes, (nuint)pathBytes.Length, out _);

            // LoadLibraryW のアドレス（ASLR があっても同一セッション内では同じ）
            var kernel32 = NativeMethods.GetModuleHandle("kernel32.dll");
            var loadLibraryW = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");

            var thread = NativeMethods.CreateRemoteThread(
                processHandle, IntPtr.Zero, 0,
                loadLibraryW, remoteBuffer,
                0, out _);

            if (thread == IntPtr.Zero)
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "CreateRemoteThread 失敗");

            // LoadLibraryW スレッドの完了を待つ（最大 10 秒）
            var waitResult = NativeMethods.WaitForSingleObject(thread, 10_000);
            if (waitResult != NativeMethods.WAIT_OBJECT_0)
            {
                NativeMethods.CloseHandle(thread);
                throw new TimeoutException("LoadLibraryW リモートスレッドがタイムアウトしました。");
            }

            // 終了コード = LoadLibraryW の戻り値（HMODULE）。0 は失敗を意味する。
            NativeMethods.GetExitCodeThread(thread, out var exitCode);
            NativeMethods.CloseHandle(thread);
            if (exitCode == 0)
                throw new InvalidOperationException(
                    $"LoadLibraryW が 0 を返しました。DLL が見つからないか、依存関係が不足しています: {dllPath}");
        }
        finally
        {
            NativeMethods.VirtualFreeEx(processHandle, remoteBuffer, 0, NativeMethods.MEM_RELEASE);
        }
    }
}
