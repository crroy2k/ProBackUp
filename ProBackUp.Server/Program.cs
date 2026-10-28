using System.Net;
using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using ProBackUp.Central;
namespace ProBackUp.Server;
internal sealed record LoginRequest(string Upn, string Password);
internal sealed record LdapEdit(string Host, int Port, string BaseDn, string BindDn, string Password, List<AllowedUser> Users);
internal sealed record AgentRegistration(string Name);
internal sealed record BackupRequest(string JobId);
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            if (args.Length == 2 && args[0] == "--self-test") { await ServerTests.Run(args[1]); return 0; }
            if (args.SequenceEqual(["--configure"]))
            {
                if (File.Exists(Path.Combine(Configuration.DefaultRoot, "iis-hosting.json"))) throw new InvalidOperationException("Este servidor se administra desde IIS. No se reconfigura con el asistente independiente mientras IIS este activo.");
                await Bootstrap.Configure(); return 0;
            }
            var config = Configuration.Load(Configuration.DefaultRoot);
            if (args.Length == 2 && args[0] == "--backup-db") { new Database(config.Root).Backup(Path.GetFullPath(args[1])); return 0; }
            if (args.Length != 0) throw new ArgumentException("Argumentos: --configure, --self-test archivo o --backup-db archivo.");
            var iis = IisMode(Environment.GetEnvironmentVariable("PROBACKUP_HOSTING"), Process.GetCurrentProcess().ProcessName);
            if (!iis && File.Exists(Path.Combine(config.Root, "iis-hosting.json"))) throw new InvalidOperationException("Esta instalacion esta publicada en IIS. No se inicia una segunda instancia; usa Restaurar-Servicio.ps1 para volver al servicio.");
            using var certificate = iis ? null : config.Certificate();
            await Build(config, new ActiveDirectory(), certificate, inProcessIis: iis).RunAsync(); return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Configuración cancelada. El servicio permanece detenido."); return 2; }
        catch (Exception ex)
        {
            var detail = ex switch
            {
                ArgumentException or InvalidOperationException => ex.Message,
                System.Security.Cryptography.CryptographicException => "No se pudo abrir o validar el PFX. Compruebe contraseña, clave privada y certificado.",
                System.DirectoryServices.Protocols.LdapException ldap => "Error LDAPS " + ldap.ErrorCode + ". Revise controlador, certificado y cuenta de consulta.",
                UnauthorizedAccessException => "Permisos insuficientes. Ejecute la configuración como administrador.",
                FileNotFoundException => "No se encontró un archivo de configuración o certificado requerido.",
                _ => "Revise configuración, archivos y permisos (" + ex.GetType().Name + ")."
            };
            Console.Error.WriteLine("ProBackUp Server: " + detail + " No se inició un servidor sin protección."); return 1;
        }
    }
    internal static bool IisMode(string? mode, string processName)
    {
        if (string.IsNullOrEmpty(mode) || mode == "Service") return false;
        if (mode != "IIS") throw new ArgumentException("PROBACKUP_HOSTING debe ser IIS o Service.");
        if (!processName.Equals("w3wp", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("El modo IIS solo se admite dentro del proceso de IIS mediante ASP.NET Core Module.");
        return true;
    }
    internal static WebApplication Build(Configuration config, IDirectory directory, X509Certificate2? certificate, bool testing = false, bool inProcessIis = false)
    {
        var settings = config.Current;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production, ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Configuration["AllowedHosts"] = settings.PublicHost;
        if (inProcessIis)
        {
            builder.WebHost.UseIIS();
            builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = 1024 * 1024);
            builder.Logging.AddEventLog(options => options.SourceName = "ProBackUp.Server");
        }
        else builder.Host.UseWindowsService(options => options.ServiceName = "ProBackUpServer");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Limits.MaxRequestBodySize = 1024 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            options.Limits.MaxConcurrentConnections = 200;
            if (!inProcessIis)
            {
                if (certificate == null) throw new InvalidOperationException("El servicio independiente requiere su certificado HTTPS.");
                options.Listen(testing ? IPAddress.Loopback : IPAddress.Any, settings.Port, listen => listen.UseHttps(certificate, https => https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13));
            }
        });
        builder.Services.AddSingleton(config); builder.Services.AddSingleton(directory); builder.Services.AddSingleton(new Database(config.Root)); builder.Services.AddSingleton<LoginThrottle>(); builder.Services.AddScoped<SessionEvents>();
        Configuration.NoLinks(Path.Combine(config.Root, "keys"));
        builder.Services.AddDataProtection().SetApplicationName("ProBackUp.Server").PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(config.Root, "keys"))).ProtectKeysWithDpapi(true);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "__Host-ProBackUp"; options.Cookie.HttpOnly = true; options.Cookie.SecurePolicy = CookieSecurePolicy.Always; options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(15); options.SlidingExpiration = true; options.EventsType = typeof(SessionEvents);
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("Admin")));
        builder.Services.AddAntiforgery(options => { options.HeaderName = "X-CSRF-TOKEN"; options.Cookie.Name = "__Host-ProBackUp-CSRF"; options.Cookie.SecurePolicy = CookieSecurePolicy.Always; options.Cookie.SameSite = SameSiteMode.Strict; });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetConcurrencyLimiter("server", _ => new ConcurrencyLimiterOptions { PermitLimit = 64, QueueLimit = 0 }));
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("agent", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var app = builder.Build();
        if (inProcessIis) app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps) { context.Response.StatusCode = 400; return; }
            await next();
        });
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff"; context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer"; context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
            try { await next(); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; }
            catch (ArgumentException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Datos inválidos. Revise los campos." }); }
            catch (UnauthorizedAccessException) { context.Response.StatusCode = 403; }
            catch (Exception ex) { app.Logger.LogError("Operación rechazada: {Type}", ex.GetType().Name); if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Servicio temporalmente no disponible. Contacte a T.I. Ext. 233." }); } }
        });
        app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
        // Every browser write, including login, requires a framework antiforgery token.
        app.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST" && context.Request.Path != "/api/agent/reports" && context.Request.Path != "/api/agent/sync") await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            await next();
        });
        foreach (var file in new[] { "index.html", "app.js", "app.css" })
        {
            var asset = file; var path = file == "index.html" ? "/" : "/" + file;
            app.MapGet(path, () => { using var stream = typeof(Program).Assembly.GetManifestResourceStream("ProBackUp.Server.www." + asset)!; using var reader = new StreamReader(stream); return Results.Text(reader.ReadToEnd(), asset.EndsWith("js") ? "text/javascript; charset=utf-8" : asset.EndsWith("css") ? "text/css; charset=utf-8" : "text/html; charset=utf-8"); }).AllowAnonymous();
        }
        foreach (var asset in new[] { (Path: "/favicon.ico", Resource: "favicon.ico", Type: "image/x-icon"), (Path: "/logo.png", Resource: "logo.png", Type: "image/png") })
        {
            var resource = asset;
            app.MapGet(resource.Path, () => Results.Stream(typeof(Program).Assembly.GetManifestResourceStream("ProBackUp.Server.www." + resource.Resource)!, resource.Type)).AllowAnonymous();
        }
        app.MapGet("/api/csrf", (HttpContext context, IAntiforgery antiforgery) => Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken })).AllowAnonymous();
        app.MapPost("/api/login", async (LoginRequest request, HttpContext context, LoginThrottle throttle, Database db) =>
        {
            var current = config.Current;
            var upn = current.Ldap.ResolveLogin(request.Upn ?? "");
            if (upn.Length > 256 || !throttle.Attempt(upn)) return Results.StatusCode(429);
            DirectoryUser? user = null;
            try { user = await directory.Authenticate(current.Ldap, upn, request.Password ?? ""); } catch { }
            if (user == null || current.Ldap.Allowed(user.Upn) is not { } allowed) { db.Audit(upn, "Inicio de sesión rechazado"); return Results.Unauthorized(); }
            throttle.Success(upn); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var claims = new[] { new Claim(ClaimTypes.Name, user.Upn), new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Role, allowed.Role), new Claim("revision", current.Revision), new Claim("started", now) };
            var properties = new AuthenticationProperties { IsPersistent = false }; properties.Items["checked"] = now;
            db.Audit(user.Upn, "Inicio de sesión correcto"); await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), properties); return Results.Ok();
        }).RequireRateLimiting("login");
        app.MapPost("/api/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
        app.MapGet("/api/me", (HttpContext context) => Results.Ok(new { user = context.User.Identity!.Name, admin = context.User.IsInRole("Admin") })).RequireAuthorization();
        app.MapGet("/api/agents", (Database db) => Results.Ok(db.Agents())).RequireAuthorization();
        app.MapGet("/api/reports", (Database db, string? agent, string? type, string? status, string? destination, string? user, DateTimeOffset? from, DateTimeOffset? to, int? page) => Results.Ok(db.Reports(agent, type, status, destination, user, from, to, page ?? 0))).RequireAuthorization();
        app.MapGet("/api/reports/export.xlsx", (HttpContext context, Database db, string? agent, string? type, string? status, string? destination, string? user, DateTimeOffset? from, DateTimeOffset? to) =>
        {
            if (!ReportExcel.Gate.Wait(0)) return Results.StatusCode(429);
            try
            {
                var filters = new Dictionary<string, string> { ["Equipo (ID)"] = agent ?? "Todos", ["Tipo"] = type ?? "Todos", ["Resultado"] = status ?? "Todos", ["Destino contiene"] = destination ?? "Todos", ["Usuario contiene"] = user ?? "Todos", ["Desde (Costa Rica)"] = from.HasValue ? ReportExcel.Local(from.Value).ToString("yyyy-MM-dd HH:mm:ss") : "Sin límite", ["Hasta, exclusivo (Costa Rica)"] = to.HasValue ? ReportExcel.Local(to.Value).ToString("yyyy-MM-dd HH:mm:ss") : "Sin límite" };
                var export = ReportExcel.Create(db.ReadReports(agent, type, status, destination, user, from, to), config.Root, filters, context.RequestAborted);
                try { db.Audit(context.User.Identity!.Name!, "Exportó historial a Excel: " + export.Rows + " registros"); }
                catch { export.Stream.Dispose(); throw; }
                return Results.Stream(export.Stream, ReportExcel.ContentType, "ProBackUp-respaldos-" + ReportExcel.Local(DateTimeOffset.UtcNow).ToString("yyyyMMdd-HHmmss") + ".xlsx");
            }
            finally { ReportExcel.Gate.Release(); }
        }).RequireAuthorization();
        app.MapGet("/api/admin/audit", (Database db) => Results.Ok(db.AuditLog())).RequireAuthorization("Admin");
        app.MapGet("/api/admin/ldap", () => { var ldap = config.Current.Ldap; return Results.Ok(new { ldap.Host, ldap.Port, ldap.BaseDn, ldap.BindDn, ldap.Users, domain = ldap.UserDomain(), passwordConfigured = ldap.ProtectedPassword.Length > 0 }); }).RequireAuthorization("Admin");
        app.MapPost("/api/admin/ldap", async (LdapEdit edit, HttpContext context, Database db) =>
        {
            var old = config.Current.Ldap;
            if (edit.Password.Length == 0 && (edit.Host != old.Host || edit.BindDn != old.BindDn)) throw new ArgumentException("Reintroduzca la contraseña de consulta al cambiar host o cuenta.");
            var ldap = new LdapSettings { Host = edit.Host.Trim(), Port = edit.Port, BaseDn = edit.BaseDn.Trim(), BindDn = edit.BindDn.Trim(), ProtectedPassword = edit.Password.Length > 0 ? Configuration.Protect(edit.Password) : old.ProtectedPassword, Users = edit.Users };
            ldap.Users = ldap.Users.Select(u => new AllowedUser { Upn = ldap.QualifyUser(u.Upn), Role = u.Role }).ToList();
            ldap.Validate(); var caller = context.User.Identity!.Name!;
            if (ldap.Allowed(caller)?.Role != "Admin") throw new ArgumentException("Mantenga al administrador actual autorizado.");
            var found = await directory.Lookup(ldap, caller);
            if (found == null || found.Id != context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value) return Results.BadRequest(new { error = "No se verificó el administrador actual en el nuevo directorio. No se guardó." });
            config.SaveLdap(ldap); db.Audit(caller, "Cambió configuración LDAP y usuarios autorizados"); await context.SignOutAsync(); return Results.Ok();
        }).RequireAuthorization("Admin");
        app.MapPost("/api/admin/agents", (AgentRegistration registration, HttpContext context, Database db) => { var registered = db.Register(registration.Name.Trim(), context.User.Identity!.Name!); return Results.Ok(new { registered.id, registered.token }); }).RequireAuthorization("Admin");
        app.MapPost("/api/admin/agents/{id}/edit", (string id, AgentRegistration edit, HttpContext context, Database db) => { if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Trim().Length > 100) return Results.BadRequest(); return db.Rename(id, edit.Name, context.User.Identity!.Name!) ? Results.Ok() : Results.NotFound(); }).RequireAuthorization("Admin");
        app.MapPost("/api/admin/agents/{id}/delete", (string id, HttpContext context, Database db) => { if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(); return db.Delete(id, context.User.Identity!.Name!) ? Results.Ok() : Results.NotFound(); }).RequireAuthorization("Admin");
        app.MapPost("/api/admin/agents/{id}/revoke", (string id, HttpContext context, Database db) => { if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(); db.Revoke(id, context.User.Identity!.Name!); return Results.Ok(); }).RequireAuthorization("Admin");
        app.MapGet("/api/admin/agents/{id}/jobs", (string id, Database db) => Results.Ok(db.Jobs(id))).RequireAuthorization("Admin");
        app.MapGet("/api/admin/agents/{id}/commands", (string id, Database db) => Results.Ok(db.Commands(id))).RequireAuthorization("Admin");
        app.MapPost("/api/admin/agents/{id}/backup", (string id, BackupRequest request, HttpContext context, Database db) =>
        {
            if (!Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(request.JobId, "N", out _)) return Results.BadRequest();
            return Results.Ok(new { id = db.RequestBackup(id, request.JobId, context.User.Identity!.Name!) });
        }).RequireAuthorization("Admin");
        app.MapPost("/api/agent/sync", (RemoteSync sync, HttpContext context, Database db) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var id = header.StartsWith("Bearer ", StringComparison.Ordinal) ? db.Agent(header[7..]) : null;
            return id == null ? Results.Unauthorized() : Results.Ok(db.Sync(id, sync));
        }).AllowAnonymous().RequireRateLimiting("agent");
        app.MapPost("/api/agent/reports", (AgentBatch batch, HttpContext context, Database db) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var id = header.StartsWith("Bearer ", StringComparison.Ordinal) ? db.Agent(header[7..]) : null;
            if (id == null) return Results.Unauthorized(); db.Receive(id, batch); return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting("agent");
        return app;
    }
}
