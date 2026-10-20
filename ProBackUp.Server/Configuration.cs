using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
namespace ProBackUp.Server;
public sealed class AllowedUser
{
    public string Upn { get; set; } = "";
    public string Role { get; set; } = "Reader";
}
public sealed class LdapSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 636;
    public string BaseDn { get; set; } = "";
    public string BindDn { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public List<AllowedUser> Users { get; set; } = [];
    public AllowedUser? Allowed(string upn) => Users.FirstOrDefault(u => u.Upn.Equals(upn, StringComparison.OrdinalIgnoreCase));
    internal string UserDomain()
    {
        var domain = string.Join(".", BaseDn.Split(',').Select(p => p.Trim()).Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..].Trim()));
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns) throw new ArgumentException("Base DN no contiene un dominio DNS valido.");
        return domain;
    }
    internal string QualifyUser(string input)
    {
        var value = input.Trim();
        if (value.Contains('@')) return value;
        if (value.Length == 0 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '/' or '*' or '(' or ')')) throw new ArgumentException("Usuario no valido.");
        return value + "@" + UserDomain();
    }
    internal string ResolveLogin(string input)
    {
        var value = input.Trim();
        if (value.Contains('@')) return value;
        if (value.Length == 0 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '/' or '*' or '(' or ')')) return value;
        var matches = Users.Where(u => u.Upn.Split('@')[0].Equals(value, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0].Upn : value;
    }
    public void Validate()
    {
        if (Uri.CheckHostName(Host) != UriHostNameType.Dns || Port is < 1 or > 65535 || BaseDn.Length is < 3 or > 2048 || BindDn.Length is < 3 or > 512 || ProtectedPassword.Length == 0)
            throw new ArgumentException("Configura host DNS LDAPS, puerto, base DN y cuenta de consulta con contraseña.");
        if (Users.Count is < 1 or > 200 || !Users.Any(u => u.Role == "Admin") || Users.Select(u => u.Upn.ToLowerInvariant()).Distinct().Count() != Users.Count ||
            Users.Any(u => u.Upn.Length is < 3 or > 256 || !u.Upn.Contains('@') || u.Upn.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '*' or '\\' or '(' or ')') || u.Role is not ("Reader" or "Admin")))
            throw new ArgumentException("Autoriza usuarios UPN concretos, sin comodines; incluye al menos un administrador. Roles: Admin o Reader.");
    }
}
public sealed class ServerSettings
{
    public string PublicHost { get; set; } = "";
    public int Port { get; set; } = 8443;
    public string ProtectedPfxPassword { get; set; } = "";
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public LdapSettings Ldap { get; set; } = new();
    public void Validate()
    {
        if (Uri.CheckHostName(PublicHost) != UriHostNameType.Dns || Port is < 1 or > 65535) throw new ArgumentException("Configura nombre DNS HTTPS y puerto válido.");
        Ldap.Validate();
    }
}
internal sealed class Configuration
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProBackUpServer");
    public string Root { get; }
    private ServerSettings current;
    private readonly object gate = new();
    internal ServerSettings Current { get { lock (gate) return JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(current))!; } }
    internal Configuration(string root, ServerSettings settings) { Root = root; current = settings; }
    internal static Configuration Load(string root)
    {
        NoLinks(root); NoLinks(Path.Combine(root, "server.json")); var settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(Path.Combine(root, "server.json"))) ?? throw new InvalidDataException("Configuración vacía.");
        settings.Validate(); return new(root, settings);
    }
    internal void SaveLdap(LdapSettings ldap)
    {
        ldap.Validate(); lock (gate) { var next = Current; next.Ldap = ldap; next.Revision = Guid.NewGuid().ToString("N"); Write(Path.Combine(Root, "server.json"), next); current = next; }
    }
    internal static void Write<T>(string path, T value)
    {
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void NoLinks(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("No se admiten redirecciones: " + p);
    }
    internal static void SecureDirectory(string root)
    {
        NoLinks(root); var dir = Directory.CreateDirectory(root); var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        try { var sid = (SecurityIdentifier)new NTAccount(@"NT SERVICE\ProBackUpServer").Translate(typeof(SecurityIdentifier)); acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); } catch (IdentityNotMappedException) { }
        dir.SetAccessControl(acl);
    }
    internal static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine));
    internal static string Unprotect(string value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.LocalMachine));
    internal X509Certificate2 Certificate(string? source = null)
    {
        var file = source ?? Path.Combine(Root, "server.pfx"); NoLinks(file);
        var cert = X509CertificateLoader.LoadPkcs12FromFile(file, Unprotect(Current.ProtectedPfxPassword), X509KeyStorageFlags.MachineKeySet);
        try
        {
            if (!cert.HasPrivateKey) throw new InvalidOperationException("El PFX no incluye la clave privada del certificado.");
            if (!cert.MatchesHostname(Current.PublicHost, allowWildcards: true, allowCommonName: false)) throw new InvalidOperationException("El certificado no incluye el nombre DNS configurado en su extensión SAN: " + Current.PublicHost);
            if (cert.NotBefore > DateTime.Now || cert.NotAfter <= DateTime.Now) throw new InvalidOperationException("El certificado aún no es válido o ya venció. Vencimiento: " + cert.NotAfter.ToString("yyyy-MM-dd"));
            using var chain = new X509Chain(); chain.ChainPolicy.RevocationMode = X509RevocationMode.Online; chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10); chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            if (!chain.Build(cert)) throw new InvalidOperationException("La cadena del certificado HTTPS no es confiable o no puede validarse. Instala la CA interna y comprueba su revocación.");
            return cert;
        }
        catch { cert.Dispose(); throw; }
    }
}
