using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ProBackUp.Installation;
internal static class ClientInstallation
{
    internal const string RegistryPath = @"SOFTWARE\ProBackUp\Client";
    internal static string OwnerSid { get { using var key = Registry.LocalMachine.OpenSubKey(RegistryPath); return key?.GetValue("OwnerSid") as string ?? ""; } }
    internal static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle copy);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, out int value, int size, out int returned);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup { public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, XChars, YChars, Fill, Flags; public short Show, ReservedSize; public IntPtr ReservedBytes, In, Out, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, int flags, string application, StringBuilder command, int creation, IntPtr environment, string directory, ref Startup startup, out ProcessInfo process);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr block, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr block);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static SafeAccessTokenHandle DesktopToken()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out var pid) == 0) throw new IOException("Inicia la instalación desde la sesión de Windows del usuario que realizará los respaldos.");
        using var desktop = Process.GetProcessById((int)pid);
        if (desktop.SessionId != Process.GetCurrentProcess().SessionId) throw new IOException("El escritorio pertenece a otra sesión.");
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0x000A, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo consultar al usuario del escritorio.");
        using (token)
        {
            if (!GetTokenInformation(token, 18, out var elevation, sizeof(int), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (elevation == 2) throw new IOException("El escritorio está elevado. Inicia desde el escritorio normal del usuario.");
            if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out var copy)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return copy;
        }
    }
    internal static string DesktopSid() { using var token = DesktopToken(); using var user = new WindowsIdentity(token.DangerousGetHandle()); return user.User!.Value; }
    internal static void LaunchDesktop(string executable)
    {
        using var token = DesktopToken();
        if (!CreateEnvironmentBlock(out var environment, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Desktop = @"winsta0\default" };
            if (!CreateProcessWithTokenW(token, 0, executable, new StringBuilder('"' + executable + '"'), 0x400, environment, Path.GetDirectoryName(executable)!, ref startup, out var process)) throw new Win32Exception(Marshal.GetLastWin32Error(), "La instalación terminó. Abre ProBackUp desde su acceso directo con tu usuario.");
            CloseHandle(process.Thread); CloseHandle(process.Process);
        }
        finally { DestroyEnvironmentBlock(environment); }
    }
}
