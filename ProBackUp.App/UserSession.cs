using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace ProBackUp;
public sealed class UserSessionUnavailableException : IOException
{
    public UserSessionUnavailableException(string user) : base("Pendiente de inicio de sesión de " + user + ". No se utiliza la sesión de otro usuario.") { }
}
internal static class UserSession
{
    [StructLayout(LayoutKind.Sequential)] private struct SessionInfo { public int Id; public IntPtr Station; public int State; }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    private static extern bool Enumerate(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle copy);
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenStatistics
    {
        public Luid TokenId, AuthenticationId; public long Expiration; public int Type, Level;
        public uint Charged, Available, Groups, Privileges; public Luid Modified;
    }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, out TokenStatistics stats, int length, out int returned);
    internal static string Key(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 10, out var stats, Marshal.SizeOf<TokenStatistics>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return stats.AuthenticationId.High.ToString("X8") + stats.AuthenticationId.Low.ToString("X8");
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    private static extern bool QuerySession(IntPtr server, int session, int information, out IntPtr value, out int bytes);
    internal static string DesktopUser()
    {
        var id = Process.GetCurrentProcess().SessionId; if (id == 0) return "";
        string Query(int information)
        {
            if (!QuerySession(IntPtr.Zero, id, information, out var value, out _)) return "";
            try { return Marshal.PtrToStringUni(value) ?? ""; } finally { WTSFreeMemory(value); }
        }
        var name = Query(5); var domain = Query(7);
        return name.Length > 0 && domain.Length > 0 ? domain + "\\" + name : "";
    }
    internal static bool Matches(string expectedUser, string expectedSid, string actualUser, string actualSid) => expectedSid.Length > 0
        ? expectedSid.Equals(actualSid, StringComparison.OrdinalIgnoreCase)
        : expectedUser.Contains('\\') && expectedUser.Equals(actualUser, StringComparison.OrdinalIgnoreCase);
    internal static string Sid(string user)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (current.Name.Equals(user, StringComparison.OrdinalIgnoreCase)) return current.User?.Value ?? "";
        // A qualified account name can be matched against WTS tokens without an AD lookup.
        return "";
    }
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] private static extern bool GetElevation(SafeAccessTokenHandle token, int type, out int elevation, int length, out int returned);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint process);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    private static SafeAccessTokenHandle DuplicateSource(SafeAccessTokenHandle source)
    {
        if (!DuplicateTokenEx(source, 0x02000000, IntPtr.Zero, 2, 2, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return token;
    }
    internal static SafeAccessTokenHandle DuplicateCurrent()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetElevation(identity.AccessToken, 18, out var elevation, sizeof(int), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (elevation != 2) return DuplicateSource(identity.AccessToken);
        return DuplicateDesktop();
    }
    internal static SafeAccessTokenHandle DuplicateDesktop()
    {
        using var identity = WindowsIdentity.GetCurrent();
        // A linked UAC token can be identification-only (1346). Use the real desktop
        // primary token, with an exact session and SID match, never the elevated token.
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out var pid) == 0) throw new UserSessionUnavailableException(identity.Name);
        using var desktop = Process.GetProcessById((int)pid);
        if (desktop.SessionId != Process.GetCurrentProcess().SessionId) throw new UserSessionUnavailableException(identity.Name);
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo consultar la sesión del escritorio.");
        if (!OpenProcessToken(process, 0x000A, out var source)) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo preparar la sesión del escritorio.");
        using (source)
        {
            using var owner = new WindowsIdentity(source.DangerousGetHandle());
            if (owner.User != identity.User) throw new UserSessionUnavailableException(identity.Name);
            if (!GetElevation(source, 18, out var shellElevation, sizeof(int), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (shellElevation == 2) throw new InvalidOperationException("El escritorio está elevado. Usa el escritorio normal del usuario para acceder a los respaldos.");
            return DuplicateSource(source);
        }
    }
    internal static readonly AsyncLocal<SafeAccessTokenHandle?> MetadataToken = new();
    internal static bool AuthorizedSession(string owner, NetworkSettings network) => owner.Length > 0 && network.Mode == "Session" && network.UserSid == owner;
    internal static bool ServiceProcess => WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle, () => { using var identity = WindowsIdentity.GetCurrent(); return identity.IsSystem; });
    internal static SafeAccessTokenHandle Acquire(NetworkSettings network)
    {
        if (ServiceProcess)
        {
            var owner = ProBackUp.Installation.ClientInstallation.OwnerSid;
            if (!AuthorizedSession(owner, network)) throw new UnauthorizedAccessException("La tarea debe usar la sesión del usuario autorizado durante la instalación.");
            return WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle, () => AcquireCore(network));
        }
        return AcquireCore(network);
    }
    private static SafeAccessTokenHandle AcquireCore(NetworkSettings network)
    {
        if (network.Mode != "Session") throw new ArgumentException("La tarea no usa sesión de usuario.");
        if (network.UserSid.Length == 0 && !network.User.Contains('\\')) throw new ArgumentException("Indica el usuario de la tarea como DOMINIO\\usuario.");
        using var current = WindowsIdentity.GetCurrent();
        if (!Enumerate(IntPtr.Zero, 0, 1, out var pointer, out var count)) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo consultar las sesiones de Windows.");
        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(IntPtr.Add(pointer, i * size));
                if (session.Id == 0 || session.State != 0) continue; // WTSActive, including a locked desktop.
                if (!current.IsSystem)
                {
                    if (session.Id == Process.GetCurrentProcess().SessionId && Matches(network.User, network.UserSid, current.Name, current.User?.Value ?? "")) return DuplicateCurrent();
                    continue;
                }
                if (!WTSQueryUserToken((uint)session.Id, out var token))
                {
                    var error = Marshal.GetLastWin32Error(); token?.Dispose();
                    if (error == 1008) continue; // Session ended between enumeration and token query.
                    throw new Win32Exception(error, "El servicio no pudo obtener la sesión del usuario.");
                }
                bool selected = false;
                try
                {
                    using var user = new WindowsIdentity(token.DangerousGetHandle());
                    selected = Matches(network.User, network.UserSid, user.Name, user.User?.Value ?? "");
                    if (selected) return token;
                }
                finally { if (!selected) token.Dispose(); }
            }
        }
        finally { WTSFreeMemory(pointer); }
        throw new UserSessionUnavailableException(network.User);
    }
    // Called only while holding the operation lock and before impersonating for file access.
    internal static void Bind(BackupJob job, SafeAccessTokenHandle token)
    {
        using var user = new WindowsIdentity(token.DangerousGetHandle()); var sid = user.User?.Value ?? "";
        if (!Matches(job.Network.User, job.Network.UserSid, user.Name, sid)) throw new UserSessionUnavailableException(job.Network.User);
        if (job.Network.UserSid.Length > 0) return;
        job.Network.UserSid = sid;
        var settings = Store.Load(); var stored = settings.Jobs.SingleOrDefault(j => j.Id == job.Id);
        if (stored?.Network.Mode == "Session" && stored.Network.User.Equals(job.Network.User, StringComparison.OrdinalIgnoreCase) && stored.Network.UserSid.Length == 0)
        { stored.Network.UserSid = sid; Store.Save(settings); }
    }
    internal static T AsProcess<T>(Func<T> action) => WindowsIdentity.RunImpersonated(MetadataToken.Value ?? SafeAccessTokenHandle.InvalidHandle, action);
    internal static void AsProcess(Action action) => WindowsIdentity.RunImpersonated(MetadataToken.Value ?? SafeAccessTokenHandle.InvalidHandle, action);
    internal static bool Migrate(AppSettings settings)
    {
        if (settings.SessionExecutionVersion >= 1) return false;
        foreach (var job in settings.Jobs)
        {
            var user = !string.IsNullOrWhiteSpace(job.Owner) ? job.Owner : job.Network.User;
            job.Network = new NetworkSettings { Mode = "Session", User = user, UserSid = Sid(user) };
            job.RunMissed = true;
        }
        settings.SessionExecutionVersion = 1; return true;
    }
}
