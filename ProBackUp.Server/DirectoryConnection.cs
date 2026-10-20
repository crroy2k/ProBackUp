using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
namespace ProBackUp.Server;
internal sealed record DirectoryUser(string Upn, string Id);
internal interface IDirectory
{
    Task<DirectoryUser?> Authenticate(LdapSettings settings, string upn, string password);
    Task<DirectoryUser?> Lookup(LdapSettings settings, string upn);
}
internal sealed class ActiveDirectory : IDirectory
{
    private readonly SemaphoreSlim slots = new(4);
    internal static string Escape(string value) => value.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
    internal static bool ValidateCertificate(string host, X509Certificate certificate)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            if (!cert.MatchesHostname(host, allowWildcards: true, allowCommonName: false)) return false;
            using var chain = new X509Chain(); chain.ChainPolicy.RevocationMode = X509RevocationMode.Online; chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(cert);
        }
        catch { return false; }
    }
    private static LdapConnection Connect(LdapSettings settings, string user, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("No se permite bind anónimo.");
        var connection = new LdapConnection(new LdapDirectoryIdentifier(settings.Host, settings.Port, true, false), new NetworkCredential(user, password), AuthType.Basic) { Timeout = TimeSpan.FromSeconds(5) };
        connection.SessionOptions.ProtocolVersion = 3; connection.SessionOptions.SecureSocketLayer = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        // Require SAN hostname, trusted chain, server-auth usage and revocation; never accept invalid certificates.
        connection.SessionOptions.VerifyServerCertificate = (_, certificate) => ValidateCertificate(settings.Host, certificate);
        try { connection.Bind(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static DirectoryUser? Find(LdapSettings settings, string upn)
    {
        using var connection = Connect(settings, settings.BindDn, Configuration.Unprotect(settings.ProtectedPassword));
        var request = new SearchRequest(settings.BaseDn, $"(&(objectCategory=person)(objectClass=user)(userPrincipalName={Escape(upn)}))", SearchScope.Subtree, "userPrincipalName", "objectGUID", "userAccountControl") { SizeLimit = 2, TimeLimit = TimeSpan.FromSeconds(5) };
        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count != 1) return null;
        var entry = response.Entries[0];
        string Value(string name) => entry.Attributes[name]?.GetValues(typeof(string)).FirstOrDefault() as string ?? "";
        string canonical = Value("userPrincipalName");
        if (!canonical.Equals(upn, StringComparison.OrdinalIgnoreCase) || settings.Allowed(canonical) == null ||
            !int.TryParse(Value("userAccountControl"), out var flags) || (flags & 2) != 0 ||
            entry.Attributes["objectGUID"]?[0] is not byte[] guid || guid.Length != 16) return null;
        return new(canonical, new Guid(guid).ToString("N"));
    }
    public async Task<DirectoryUser?> Lookup(LdapSettings settings, string upn)
    {
        if (settings.Allowed(upn) == null || !await slots.WaitAsync(0)) return null;
        try { return await Task.Run(() => Find(settings, upn)); } finally { slots.Release(); }
    }
    public async Task<DirectoryUser?> Authenticate(LdapSettings settings, string upn, string password)
    {
        if (settings.Allowed(upn) == null || password.Length is < 1 or > 1024 || !await slots.WaitAsync(0)) return null;
        try
        {
            return await Task.Run(() => { var user = Find(settings, upn); if (user == null) return null; using var verified = Connect(settings, user.Upn, password); return user; });
        }
        finally { slots.Release(); }
    }
}
