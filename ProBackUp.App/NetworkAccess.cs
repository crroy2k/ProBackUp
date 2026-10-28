using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
namespace ProBackUp;
public static class NetworkAccess
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string username, string? domain, string password, int type, int provider, out SafeAccessTokenHandle token);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string local, StringBuilder remote, ref int length);
    public static string Normalize(string path)
    {
        path = path.Trim();
        if (path.Length >= 2 && path[1] == ':')
        {
            var buffer = new StringBuilder(32768); int length = buffer.Capacity;
            if (WNetGetConnection(path[..2], buffer, ref length) == 0) return Path.Combine(buffer.ToString(), path[2..].TrimStart('\\', '/'));
            if (new DriveInfo(path[..2]).DriveType == DriveType.Network) throw new IOException("Escribe la ruta UNC \\\\servidor\\carpeta; no se pudo resolver la unidad de red.");
        }
        return path;
    }
    public static void Validate(NetworkSettings network)
    {
        if (network.Mode is not ("Service" or "Current" or "Other" or "Session")) throw new ArgumentException("Selecciona la cuenta de acceso a la red.");
        if (network.Mode == "Session")
        {
            if (string.IsNullOrWhiteSpace(network.User) || (network.UserSid.Length == 0 && !network.User.Contains('\\'))) throw new ArgumentException("Indica el usuario de la tarea como DOMINIO\\usuario.");
            return;
        }
        if (network.Mode != "Service" && (string.IsNullOrWhiteSpace(network.User) || network.EncryptedPassword.Length == 0)) throw new ArgumentException("Indica la cuenta de Windows y su contraseña para ejecutar sin sesión abierta.");
    }
    internal static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        return path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    }
    internal static bool RequiresPersonalCredentials(BackupJob job) => IsNetworkPath(job.Destination) || job.Sources.Any(IsNetworkPath);
    public static void ValidateJob(BackupJob job)
    {
        if (UserSession.ServiceProcess && !UserSession.AuthorizedSession(ProBackUp.Installation.ClientInstallation.OwnerSid, job.Network)) throw new UnauthorizedAccessException("El servicio solo ejecuta tareas con la sesión del usuario autorizado; nunca con SYSTEM ni con otra cuenta.");
        Validate(job.Network);
        if (RequiresPersonalCredentials(job) && job.Network.Mode == "Service")
            throw new ArgumentException("Este respaldo usa una carpeta de red. En Destino y acceso selecciona la cuenta de este usuario o una cuenta autorizada y guarda su contraseña de Windows. Las ejecuciones local, programada y remota usarán esas mismas credenciales; no se utiliza la sesión abierta ni la cuenta del servidor como alternativa.");
    }
    internal static SafeAccessTokenHandle? AcquireJobSession(BackupJob job) { if (job.Network.Mode != "Session") return null; ValidateJob(job); return UserSession.Acquire(job.Network); }
    public static Task<T> RunJob<T>(BackupJob job, Func<Task<T>> action, SafeAccessTokenHandle? session = null)
    {
        ValidateJob(job);
        return session != null ? WindowsIdentity.RunImpersonatedAsync(session, action) : Run(job.Network, action);
    }
    public static async Task<T> Run<T>(NetworkSettings network, Func<Task<T>> action)
    {
        Validate(network); if (network.Mode == "Service") return await action();
        if (network.Mode == "Session")
        {
            using var session = UserSession.Acquire(network);
            return await WindowsIdentity.RunImpersonatedAsync(session, action);
        }
        var user = network.User; string? domain = null;
        if (user.Contains('\\')) { var parts = user.Split('\\', 2); domain = parts[0]; user = parts[1]; }
        if (!LogonUser(user, domain, network.Password(), 9, 3, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo preparar la cuenta de acceso a la red.");
        using (token) return await WindowsIdentity.RunImpersonatedAsync(token, action);
    }
    public static async Task Test(BackupJob job)
    {
        await RunJob(job, async () =>
        {
            BackupEngine.NoLinks(job.Destination); Directory.CreateDirectory(job.Destination); BackupEngine.NoLinks(job.Destination);
            string temp = Path.Combine(job.Destination, ".probackup-connection-" + Guid.NewGuid().ToString("N"));
            try { await File.WriteAllTextAsync(temp, "ProBackUp"); if (await File.ReadAllTextAsync(temp) != "ProBackUp") throw new IOException("No se pudo comprobar la lectura del destino."); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return true;
        });
    }
}
