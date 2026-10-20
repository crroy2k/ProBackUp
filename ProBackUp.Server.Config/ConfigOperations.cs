using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
namespace ProBackUp.Server;
internal static class ConfigOperations
{
    internal static string Friendly(Exception ex) => ex switch
    {
        CryptographicException => "No se pudo abrir el PFX. Revisa la contraseña y que incluya su clave privada.",
        System.DirectoryServices.Protocols.LdapException ldap => "No se pudo validar LDAPS (código " + ldap.ErrorCode + "). Revisa host, certificado, usuario y contraseña de consulta.",
        UnauthorizedAccessException => "Permisos insuficientes. Abre el configurador como administrador.",
        _ => ex.Message
    };
    internal static void CheckPort(int port, int? runningPort = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("El puerto debe estar entre 1 y 65535.");
        if (runningPort != port && IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == port)) throw new IOException("El puerto " + port + " está ocupado por otra aplicación. Elige otro puerto.");
    }
    internal static async Task<DirectoryUser> ValidateDirectory(ServerSettings settings, IDirectory directory)
    {
        settings.Ldap.Validate(); var admin = settings.Ldap.Users.First(u => u.Role == "Admin");
        return await directory.Lookup(settings.Ldap, admin.Upn) ?? throw new InvalidOperationException("La cuenta de consulta no pudo encontrar al primer administrador autorizado o está deshabilitado. Revisa Base DN, permisos y UPN.");
    }
    internal static async Task Apply(ServerSettings settings, string pfx, bool start)
    {
        if (File.Exists(Path.Combine(Configuration.DefaultRoot, "iis-hosting.json"))) throw new InvalidOperationException("Este servidor está publicado en IIS. Administra usuarios y LDAPS desde el panel y HTTPS desde IIS. Para volver al servicio usa Restaurar-Servicio.ps1 del paquete IIS.");
        settings.Validate();
        if (ServerControl.Binary() == null) await ServerControl.Install();
        var binary = ServerControl.Binary()!; ServerControl.PrepareRuntime();
        bool wasRunning = ServerControl.Stop();
        try
        {
            using var transaction = new ConfigFiles(Configuration.DefaultRoot);
            transaction.Save(settings, pfx);
            using var firewall = new ServerControl.FirewallChange(binary, settings.Port);
            try { if (start) ServerControl.Start(); firewall.Commit(); transaction.Commit(); }
            catch { try { ServerControl.Stop(); } catch { } throw; }
        }
        catch
        {
            if (wasRunning && File.Exists(Path.Combine(Configuration.DefaultRoot, "server.json"))) { try { ServerControl.Start(); } catch { } }
            throw;
        }
    }
}
// Applies the two files while the web service is stopped; rolls back if firewall or startup fails.
internal sealed class ConfigFiles : IDisposable
{
    private readonly string root;
    private readonly FileStream gate;
    private readonly Dictionary<string, byte[]?> originals = [];
    private bool changed, committed;
    internal ConfigFiles(string root)
    {
        this.root = root; Configuration.NoLinks(root); Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "configuration.lock"); Configuration.NoLinks(lockPath);
        gate = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { foreach (var name in new[] { "server.json", "server.pfx" }) { var path = Path.Combine(root, name); Configuration.NoLinks(path); originals[name] = File.Exists(path) ? File.ReadAllBytes(path) : null; } }
        catch { gate.Dispose(); throw; }
    }
    internal void Save(ServerSettings settings, string pfx, Action<ServerSettings, string>? validation = null)
    {
        settings.Validate(); Configuration.NoLinks(pfx);
        var staged = Path.Combine(root, "certificate-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Bytes(staged, File.ReadAllBytes(pfx));
            if (validation == null) { using var certificate = new Configuration(root, settings).Certificate(staged); } else validation(settings, staged);
            foreach (var pair in originals) if (pair.Value != null) Bytes(Path.Combine(root, pair.Key + ".previous"), pair.Value);
            changed = true;
            File.Move(staged, Path.Combine(root, "server.pfx"), true);
            settings.Revision = Guid.NewGuid().ToString("N"); Configuration.Write(Path.Combine(root, "server.json"), settings);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    private static void Bytes(string path, byte[] value)
    {
        Configuration.NoLinks(path); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(value); stream.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal void Commit() => committed = true;
    public void Dispose()
    {
        try
        {
            if (changed && !committed)
                foreach (var pair in originals) { var path = Path.Combine(root, pair.Key); Configuration.NoLinks(path); if (pair.Value == null) File.Delete(path); else Bytes(path, pair.Value); }
        }
        finally { gate.Dispose(); }
    }
}
