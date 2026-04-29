using System.Runtime.InteropServices;

namespace WpfInspector.Injector;

internal static partial class NativeMethods
{
    internal const uint PROCESS_ALL_ACCESS = 0x001FFFFF;
    internal const uint MEM_COMMIT         = 0x00001000;
    internal const uint MEM_RESERVE        = 0x00002000;
    internal const uint MEM_RELEASE        = 0x00008000;
    internal const uint PAGE_READWRITE     = 0x04;
    internal const uint INFINITE           = 0xFFFFFFFF;
    internal const uint WAIT_OBJECT_0      = 0x00000000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, nuint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr GetModuleHandle(string lpModuleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);
}
