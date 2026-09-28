using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ZapretTester.App;

internal static class HelperPipeIdentity
{
    // Token.Owner can be Administrators under UAC; the server deliberately owns
    // its pipe as Token.User so the same account can connect at either elevation.
    internal static void Validate(NamedPipeClientStream pipe, int expectedProcessId)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)), identity.User))
            throw new UnauthorizedAccessException("Канал Helper принадлежит другому пользователю.");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualProcessId))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (actualProcessId != expectedProcessId)
            throw new UnauthorizedAccessException("Канал открыт другим процессом, а не ожидаемым Helper.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
