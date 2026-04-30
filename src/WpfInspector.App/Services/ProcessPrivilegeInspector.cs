using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WpfInspector.App.Services;

internal static class ProcessPrivilegeInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    internal static bool TryGetProcessContext(int processId, out ProcessPrivilegeContext context, out Exception? error)
    {
        try
        {
            context = GetProcessContext(processId);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            context = ProcessPrivilegeContext.Unknown(processId);
            error = exception;
            return false;
        }
    }

    private static ProcessPrivilegeContext GetProcessContext(int processId)
    {
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"プロセス情報を取得できませんでした (PID {processId})");
        }

        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"プロセストークンを取得できませんでした (PID {processId})");
            }

            using (tokenHandle)
            {
                return new ProcessPrivilegeContext(
                    processId,
                    ReadIsElevated(tokenHandle),
                    ReadIntegrityLevel(tokenHandle));
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static bool ReadIsElevated(SafeAccessTokenHandle tokenHandle)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<TOKEN_ELEVATION>());
        try
        {
            if (!GetTokenInformation(
                tokenHandle,
                TOKEN_INFORMATION_CLASS.TokenElevation,
                buffer,
                Marshal.SizeOf<TOKEN_ELEVATION>(),
                out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "TokenElevation の取得に失敗しました。");
            }

            var elevation = Marshal.PtrToStructure<TOKEN_ELEVATION>(buffer);
            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntegrityLevel ReadIntegrityLevel(SafeAccessTokenHandle tokenHandle)
    {
        GetTokenInformation(
            tokenHandle,
            TOKEN_INFORMATION_CLASS.TokenIntegrityLevel,
            IntPtr.Zero,
            0,
            out var bufferLength);

        if (bufferLength <= 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "TokenIntegrityLevel のサイズ取得に失敗しました。");
        }

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            if (!GetTokenInformation(
                tokenHandle,
                TOKEN_INFORMATION_CLASS.TokenIntegrityLevel,
                buffer,
                bufferLength,
                out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "TokenIntegrityLevel の取得に失敗しました。");
            }

            var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
            if (label.Label.Sid == IntPtr.Zero)
            {
                return IntegrityLevel.Unknown;
            }

            var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
            if (countPointer == IntPtr.Zero)
            {
                return IntegrityLevel.Unknown;
            }

            var subAuthorityCount = Marshal.ReadByte(countPointer);
            if (subAuthorityCount == 0)
            {
                return IntegrityLevel.Unknown;
            }

            var ridPointer = GetSidSubAuthority(label.Label.Sid, (uint)(subAuthorityCount - 1));
            if (ridPointer == IntPtr.Zero)
            {
                return IntegrityLevel.Unknown;
            }

            var rid = Marshal.ReadInt32(ridPointer);
            return MapIntegrityLevel(rid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntegrityLevel MapIntegrityLevel(int rid) => rid switch
    {
        < 0x1000 => IntegrityLevel.Untrusted,
        < 0x2000 => IntegrityLevel.Low,
        < 0x3000 => IntegrityLevel.Medium,
        < 0x4000 => IntegrityLevel.High,
        < 0x5000 => IntegrityLevel.System,
        _ => IntegrityLevel.Protected
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20,
        TokenIntegrityLevel = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        internal SID_AND_ATTRIBUTES Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        internal int TokenIsElevated;
    }
}

internal sealed record ProcessPrivilegeContext(
    int ProcessId,
    bool IsElevated,
    IntegrityLevel IntegrityLevel)
{
    internal static ProcessPrivilegeContext Unknown(int processId) =>
        new(processId, IsElevated: false, IntegrityLevel.Unknown);

    internal string Describe() =>
        $"{(IsElevated ? "管理者" : "通常権限")} / {IntegrityLevel.ToDisplayText()}";
}

internal enum IntegrityLevel
{
    Unknown = 0,
    Untrusted = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    System = 5,
    Protected = 6
}

internal static class IntegrityLevelExtensions
{
    internal static string ToDisplayText(this IntegrityLevel level) => level switch
    {
        IntegrityLevel.Untrusted => "Untrusted",
        IntegrityLevel.Low => "Low",
        IntegrityLevel.Medium => "Medium",
        IntegrityLevel.High => "High",
        IntegrityLevel.System => "System",
        IntegrityLevel.Protected => "Protected",
        _ => "Unknown"
    };
}
