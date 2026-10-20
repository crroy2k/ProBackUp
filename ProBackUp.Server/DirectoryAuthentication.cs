using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
namespace ProBackUp.Server;
internal sealed class SessionEvents(Configuration config, IDirectory directory) : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) { context.Response.StatusCode = 401; return Task.CompletedTask; }
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) { context.Response.StatusCode = 403; return Task.CompletedTask; }
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var settings = config.Current; var principal = context.Principal!;
        var user = settings.Ldap.Allowed(principal.Identity?.Name ?? "");
        bool valid = user != null && principal.FindFirst("revision")?.Value == settings.Revision && principal.FindFirst(ClaimTypes.Role)?.Value == user.Role &&
            long.TryParse(principal.FindFirst("started")?.Value, out var started) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - started < 8 * 3600;
        if (valid && (!context.Properties.Items.TryGetValue("checked", out var value) || !long.TryParse(value, out var last) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - last >= 300))
        {
            try { var found = await directory.Lookup(settings.Ldap, user!.Upn); valid = found != null && found.Id == principal.FindFirst(ClaimTypes.NameIdentifier)?.Value; }
            catch { valid = false; }
            if (valid) { context.Properties.Items["checked"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(); context.ShouldRenew = true; }
        }
        if (!valid) { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(); }
    }
}
// Bound memory and per-account throttling in addition to the HTTP/IP rate limiter.
internal sealed class LoginThrottle
{
    private readonly Dictionary<string, (int count, DateTimeOffset since)> attempts = new(StringComparer.OrdinalIgnoreCase);
    internal bool Attempt(string upn)
    {
        lock (attempts)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in attempts.Where(k => now - k.Value.since > TimeSpan.FromMinutes(15)).Select(k => k.Key).ToList()) attempts.Remove(key);
            if (attempts.TryGetValue(upn, out var value)) { if (value.count >= 5) return false; attempts[upn] = (value.count + 1, value.since); }
            else { if (attempts.Count >= 10000) return false; attempts[upn] = (1, now); }
            return true;
        }
    }
    internal void Success(string upn) { lock (attempts) attempts.Remove(upn); }
}
