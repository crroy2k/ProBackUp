using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ProBackUp.Central;
namespace ProBackUp.Server;
internal static class ServerTests
{
    private sealed class TestDirectory : IDirectory
    {
        public Task<DirectoryUser?> Lookup(LdapSettings settings, string upn) => Task.FromResult(settings.Allowed(upn) == null ? null : new DirectoryUser(upn, upn.StartsWith("admin") ? "11111111111111111111111111111111" : "22222222222222222222222222222222"));
        public Task<DirectoryUser?> Authenticate(LdapSettings settings, string upn, string password) => password == "correcta" ? Lookup(settings, upn) : Task.FromResult<DirectoryUser?>(null);
    }
    internal static async Task Run(string reportPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProBackUp-server-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var results = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); }
        using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build()); using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)); using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx, "test"), "test", X509KeyStorageFlags.UserKeySet);
        var settings = new ServerSettings { PublicHost = "localhost", Port = 0, Ldap = new LdapSettings { Host = "dc.example.test", BaseDn = "DC=example,DC=test", BindDn = "reader@example.test", ProtectedPassword = Configuration.Protect("no-es-una-clave-real"), Users = [new() { Upn = "admin@example.test", Role = "Admin" }, new() { Upn = "reader@example.test", Role = "Reader" }] } };
        var config = new Configuration(root, settings);
        await using var app = Program.Build(config, new TestDirectory(), cert, true);
        try
        {
            Check(!Program.IisMode(null, "ProBackUp.Server") && Program.IisMode("IIS", "w3wp"), "Selecciona alojamiento IIS nativo sin alterar el servicio independiente");
            bool invalidIis = false; try { Program.IisMode("IIS", "ProBackUp.Server"); } catch (InvalidOperationException) { invalidIis = true; }
            Check(invalidIis, "Rechaza activar modo IIS fuera de IIS, sin abrir HTTP alternativo");
            await app.StartAsync(); var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(); address = address.Replace("127.0.0.1", "localhost");
            HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, ServerCertificateCustomValidationCallback = (_, serverCert, _, _) => serverCert != null && serverCert.RawData.SequenceEqual(cert.RawData) }) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
            using var admin = Client(); using var reader = Client(); using var machine = Client();
            async Task<string> Csrf(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/csrf")).GetProperty("token").GetString()!;
            async Task<HttpResponseMessage> Post(HttpClient client, string path, object data, bool csrf = true)
            { using var msg = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(data) }; if (csrf) msg.Headers.Add("X-CSRF-TOKEN", await Csrf(client)); return await client.SendAsync(msg); }
            Check((await admin.GetAsync("/api/reports/export.xlsx")).StatusCode == HttpStatusCode.Unauthorized, "Excel requiere sesion web autenticada");
            Check((await admin.GetAsync("/api/reports")).StatusCode == HttpStatusCode.Unauthorized, "Panel de reportes rechaza acceso anónimo");
            Check((await admin.GetAsync("/")).IsSuccessStatusCode && (await admin.GetStringAsync("/app.js")).Contains("csrf"), "Panel y recursos incrustados disponibles por HTTPS");
            using (var web = await admin.GetAsync("/")) {
                var markup = await web.Content.ReadAsStringAsync();
                Check(web.Content.Headers.ContentType?.CharSet == "utf-8" && markup.Contains("Supervisión de respaldos") && markup.Contains("Contraseña") && !markup.Contains("Ã") && !markup.Contains("Â"), "HTML conserva tildes y ene con charset UTF-8 explicito");
            }
            Check((await Post(admin, "/api/login", new { Upn = "admin@example.test", Password = "correcta" }, false)).StatusCode == HttpStatusCode.BadRequest, "Inicio de sesión exige token CSRF");
            Check((await Post(admin, "/api/login", new { Upn = "intruso@example.test", Password = "correcta" })).StatusCode == HttpStatusCode.Unauthorized, "Usuario fuera de lista explícita rechazado");
            Check(settings.Ldap.QualifyUser("nuevo") == "nuevo@example.test" && settings.Ldap.QualifyUser("actual@other.test") == "actual@other.test", "Usuarios nuevos completan dominio AD y cuentas existentes conservan su UPN");
            Check(settings.Ldap.ResolveLogin(" ADMIN ") == "admin@example.test" && settings.Ldap.ResolveLogin("intruso") == "intruso", "Usuario corto resuelve solo una cuenta autorizada, sin distinguir mayusculas");
            var ambiguous = new LdapSettings { Users = [new() { Upn = "admin@example.test" }, new() { Upn = "admin@other.test" }] };
            Check(ambiguous.ResolveLogin("admin") == "admin" && ambiguous.ResolveLogin("admin@other.test") == "admin@other.test", "Nombres ambiguos exigen UPN explicito");
            var login = await Post(admin, "/api/login", new { Upn = "admin", Password = "correcta" });
            Check(login.IsSuccessStatusCode && login.Headers.GetValues("Set-Cookie").Any(c => c.Contains("secure", StringComparison.OrdinalIgnoreCase) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)), "Cookie de sesión Secure, HttpOnly y SameSite Strict");
            Check((await Post(reader, "/api/login", new { Upn = "reader@example.test", Password = "correcta" })).IsSuccessStatusCode, "Usuario de consulta puede autenticarse");
            Check((await reader.GetAsync("/api/admin/ldap")).StatusCode == HttpStatusCode.Forbidden && (await Post(reader, "/api/admin/agents", new { Name = "No permitido" })).StatusCode == HttpStatusCode.Forbidden, "Reader no puede cambiar LDAP ni registrar equipos");
            var registration = await Post(admin, "/api/admin/agents", new { Name = "PC-Prueba" }); var registered = await registration.Content.ReadFromJsonAsync<JsonElement>(); var key = registered.GetProperty("token").GetString()!; var id = registered.GetProperty("id").GetString()!;
            Check(registration.IsSuccessStatusCode && key.Length == 64 && !(await admin.GetStringAsync("/api/agents")).Contains(key), "Clave individual fuerte, mostrada solo al crearla");
            var item = new CentralReport { Id = Guid.NewGuid().ToString("N"), JobId = Guid.NewGuid().ToString("N"), JobName = "Documentos", User = "usuario@example.test", Destination = @"\\nas\respaldo", Format = "Incremental", Status = "Correcto", Started = DateTimeOffset.UtcNow.AddMinutes(-2), Finished = DateTimeOffset.UtcNow, Bytes = 21L * 1024 * 1024 * 1024, CopiedBytes = 300L * 1024 * 1024, Files = 8 };
            var batch = new AgentBatch { MachineName = "PC-Prueba", Version = "3.0", Reports = [item] };
            Check((await Post(machine, "/api/agent/reports", batch, false)).StatusCode == HttpStatusCode.Unauthorized, "Ingesta rechaza máquinas sin clave");
            machine.DefaultRequestHeaders.Authorization = new("Bearer", key);
            Check((await Post(machine, "/api/agent/reports", batch, false)).IsSuccessStatusCode && (await Post(machine, "/api/agent/reports", batch, false)).IsSuccessStatusCode, "Reportes autenticados aceptados y reintento idempotente");
            Check((await machine.GetAsync("/api/reports")).StatusCode == HttpStatusCode.Unauthorized, "Clave de equipo no da acceso al panel");
            var data = await reader.GetFromJsonAsync<JsonElement>("/api/reports?type=Incremental&destination=nas&user=usuario"); var rows = data.GetProperty("rows");
            Check(rows.GetArrayLength() == 1 && rows[0].GetProperty("report").GetProperty("copiedBytes").GetInt64() == item.CopiedBytes && rows[0].GetProperty("report").GetProperty("bytes").GetInt64() == item.Bytes, "Filtros y métricas distinguen versión completa de datos nuevos");
            Check((await reader.GetFromJsonAsync<JsonElement>("/api/reports?type=FullZip")).GetProperty("rows").GetArrayLength() == 0, "Filtro excluye tipos diferentes");
            Check((await machine.GetAsync("/api/reports/export.xlsx")).StatusCode == HttpStatusCode.Unauthorized, "Clave de cliente no permite descargar Excel");
            using (var excel = await reader.GetAsync("/api/reports/export.xlsx?type=Incremental&destination=nas&user=usuario"))
            {
                using var package = new System.IO.Compression.ZipArchive(new MemoryStream(await excel.Content.ReadAsByteArrayAsync()));
                var worksheet = System.Xml.Linq.XDocument.Load(package.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                Check(excel.IsSuccessStatusCode && excel.Content.Headers.ContentType?.MediaType == ReportExcel.ContentType && excel.Content.Headers.ContentDisposition?.FileName?.Contains(".xlsx") == true && worksheet.Descendants(System.Xml.Linq.XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")).Count() == 2, "Usuario Consulta descarga XLSX con filtros y nombre de archivo");
            }
            ReportExcel.Gate.Wait();
            try { Check((await reader.GetAsync("/api/reports/export.xlsx")).StatusCode == HttpStatusCode.TooManyRequests, "Excel limita exportaciones simultaneas sin bloquear reportes de clientes"); }
            finally { ReportExcel.Gate.Release(); }
            ReportExcelTests.Run(root, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "excel-export-test.xlsx"), Check);
            var agents = await reader.GetFromJsonAsync<JsonElement>("/api/agents");
            Check(agents[0].GetProperty("lastBackup").GetDateTimeOffset() == item.Finished, "Tarjeta usa la fecha del respaldo exitoso y no el latido del equipo");
            var failed = new CentralReport { Id = Guid.NewGuid().ToString("N"), JobId = item.JobId, JobName = item.JobName, Format = item.Format, Status = "Error", Started = item.Started, Finished = item.Finished.AddSeconds(1) };
            await Post(machine, "/api/agent/reports", new AgentBatch { MachineName = "PC-Prueba", Version = "3.0", Reports = [failed] }, false);
            agents = await reader.GetFromJsonAsync<JsonElement>("/api/agents");
            Check(agents[0].GetProperty("lastBackup").GetDateTimeOffset() == item.Finished, "Un intento fallido no reinicia el contador sin respaldos");
            item.Status = "Eliminado por retención"; item.Updated = DateTimeOffset.UtcNow.AddSeconds(1); await Post(machine, "/api/agent/reports", batch, false);
            item.Status = "Correcto"; item.Updated = DateTimeOffset.UtcNow.AddMinutes(-1); await Post(machine, "/api/agent/reports", batch, false);
            var reordered = await reader.GetFromJsonAsync<JsonElement>("/api/reports");
            Check(reordered.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("report").GetProperty("id").GetString() == item.Id).GetProperty("report").GetProperty("status").GetString() == "Eliminado por retención", "Un reintento antiguo no restaura el estado de una versión depurada");
            var simultaneous = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(machine, "/api/agent/reports", new AgentBatch { MachineName = "PC-Prueba", Version = "3.0", Reports = [] }, false)));
            Check(simultaneous.All(r => r.IsSuccessStatusCode), "Ingesta concurrente mantiene transacciones sin perder confirmaciones");
            var remoteJob = new RemoteJob { Id = item.JobId, Name = "Respaldo remoto", Format = "Incremental", Destination = item.Destination };
            Check((await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob] }, false)).IsSuccessStatusCode, "Servicio anuncia tareas configuradas sin enviar credenciales");
            Check((await Post(reader, "/api/admin/agents/" + id + "/backup", new { JobId = item.JobId })).StatusCode == HttpStatusCode.Forbidden, "Reader no puede ordenar respaldos");
            Check((await Post(admin, "/api/admin/agents/" + id + "/backup", new { JobId = item.JobId }, false)).StatusCode == HttpStatusCode.BadRequest, "Orden remota exige CSRF");
            Check((await Post(admin, "/api/admin/agents/" + id + "/backup", new { JobId = Guid.NewGuid().ToString("N") })).StatusCode == HttpStatusCode.BadRequest, "No acepta tareas arbitrarias desconocidas por el cliente");
            var order = await Post(admin, "/api/admin/agents/" + id + "/backup", new { JobId = item.JobId }); var orderId = (await order.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var duplicate = await Post(admin, "/api/admin/agents/" + id + "/backup", new { JobId = item.JobId });
            Check(order.IsSuccessStatusCode && (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString() == orderId, "Repetir una solicitud pendiente no duplica el respaldo");
            var syncReply = await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob] }, false);
            Check((await syncReply.Content.ReadFromJsonAsync<RemoteReply>())!.Commands.Single().Id == orderId, "Solo el cliente autenticado recibe su orden pendiente");
            var otherRegistration = await Post(admin, "/api/admin/agents", new { Name = "Otro cliente" }); var other = await otherRegistration.Content.ReadFromJsonAsync<JsonElement>();
            using var otherMachine = Client(); otherMachine.DefaultRequestHeaders.Authorization = new("Bearer", other.GetProperty("token").GetString());
            var isolated = await Post(otherMachine, "/api/agent/sync", new RemoteSync { Results = [new() { Id = orderId, State = "Correcto", RecordId = orderId }] }, false);
            Check((await isolated.Content.ReadFromJsonAsync<RemoteReply>())!.Commands.Count == 0 && (await admin.GetStringAsync("/api/admin/agents/" + id + "/commands")).Contains("Pendiente"), "Otro cliente no recibe ni confirma órdenes ajenas");
            await Post(admin, "/api/admin/agents/" + other.GetProperty("id").GetString() + "/delete", new { });
            await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob], Results = [new() { Id = orderId, State = "En curso", RecordId = orderId }] }, false);
            var completed = await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob], Results = [new() { Id = orderId, State = "Correcto", RecordId = orderId }] }, false);
            await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob], Results = [new() { Id = orderId, State = "En curso", RecordId = orderId }] }, false);
            Check((await completed.Content.ReadFromJsonAsync<RemoteReply>())!.Commands.Count == 0 && (await admin.GetStringAsync("/api/admin/agents/" + id + "/commands")).Contains("Correcto"), "Orden completada no se reofrece ni retrocede por reintentos antiguos");
            var waitingOrder = await Post(admin, "/api/admin/agents/" + id + "/backup", new { JobId = item.JobId }); var waitingId = (await waitingOrder.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var waitingReply = await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob], Results = [new() { Id = waitingId, State = "Esperando sesión" }] }, false);
            Check((await waitingReply.Content.ReadFromJsonAsync<RemoteReply>())!.Commands.Single().Expires == DateTimeOffset.MaxValue, "Orden recogida sin usuario activo espera al inicio de sesión sin caducar");
            var secondJob = new RemoteJob { Id = Guid.NewGuid().ToString("N"), Name = "Otra sesión", Format = "FullZip", Destination = remoteJob.Destination };
            await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob, secondJob] }, false);
            var secondOrder = await Post(admin, "/api/admin/agents/" + id + "/backup", new { JobId = secondJob.Id }); var secondOrderId = (await secondOrder.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var multiple = await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob, secondJob] }, false);
            Check((await multiple.Content.ReadFromJsonAsync<RemoteReply>())!.Commands.Count == 2, "Una tarea esperando sesión no bloquea órdenes para otros usuarios del equipo");
            await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob], Results = [new() { Id = secondOrderId, State = "Rechazado", Message = "Prueba finalizada" }] }, false);
            var waitingDuplicate = await Post(admin, "/api/admin/agents/" + id + "/backup", new { JobId = item.JobId });
            Check((await waitingDuplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString() == waitingId, "No duplica una orden que espera sesión");
            await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob], Results = [new() { Id = waitingId, State = "En curso", RecordId = waitingId }] }, false);
            await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob], Results = [new() { Id = waitingId, State = "Esperando sesión" }] }, false);
            Check((await admin.GetStringAsync("/api/admin/agents/" + id + "/commands")).Contains("En curso"), "Un aviso antiguo de espera no retrocede una copia iniciada");
            await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob], Results = [new() { Id = waitingId, State = "Correcto", RecordId = waitingId }] }, false);
            var expiring = await Post(admin, "/api/admin/agents/" + id + "/backup", new { JobId = item.JobId }); var expiringId = (await expiring.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            using (var sql = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "reports.db") }.ToString()))
            { sql.Open(); using var update = sql.CreateCommand(); update.CommandText = "UPDATE commands SET expires=$at WHERE id=$id"; update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")); update.Parameters.AddWithValue("$id", expiringId); update.ExecuteNonQuery(); }
            var expired = await Post(machine, "/api/agent/sync", new RemoteSync { Jobs = [remoteJob] }, false);
            Check((await expired.Content.ReadFromJsonAsync<RemoteReply>())!.Commands.Count == 0 && (await admin.GetStringAsync("/api/admin/agents/" + id + "/commands")).Contains("Caducado"), "Una orden caducada no se entrega al reconectar");
            Check((await Post(admin, "/api/admin/agents/" + id + "/revoke", new { })).IsSuccessStatusCode && (await Post(machine, "/api/agent/reports", batch, false)).StatusCode == HttpStatusCode.Unauthorized, "Revocación bloquea inmediatamente la clave del equipo");
            Check((await Post(reader, "/api/admin/agents/" + id + "/edit", new { Name = "Intruso" })).StatusCode == HttpStatusCode.Forbidden && (await Post(reader, "/api/admin/agents/" + id + "/delete", new { })).StatusCode == HttpStatusCode.Forbidden, "Reader no puede editar ni eliminar clientes");
            Check((await Post(admin, "/api/admin/agents/" + id + "/edit", new { Name = "   " })).StatusCode == HttpStatusCode.BadRequest, "Nombre vacío rechazado al editar");
            Check((await Post(admin, "/api/admin/agents/" + id + "/edit", new { Name = "PC Renombrado" }, false)).StatusCode == HttpStatusCode.BadRequest, "Edición de cliente exige CSRF");
            Check((await Post(admin, "/api/admin/agents/" + id + "/edit", new { Name = "PC Renombrado" })).IsSuccessStatusCode && (await admin.GetStringAsync("/api/agents")).Contains("PC Renombrado"), "Administrador puede renombrar sin cambiar identidad ni historial");
            Check((await Post(admin, "/api/admin/agents/" + id + "/delete", new { }, false)).StatusCode == HttpStatusCode.BadRequest, "Eliminación de cliente exige CSRF");
            Check((await Post(admin, "/api/admin/agents/" + id + "/delete", new { })).IsSuccessStatusCode && (await admin.GetFromJsonAsync<JsonElement>("/api/agents")).GetArrayLength() == 0 && (await admin.GetFromJsonAsync<JsonElement>("/api/reports")).GetProperty("rows").GetArrayLength() == 0, "Eliminación transaccional retira cliente y reportes centrales");
            Check((await Post(admin, "/api/admin/agents/" + id + "/delete", new { })).StatusCode == HttpStatusCode.NotFound && new Database(root).Agent(key) == null, "Cliente eliminado no acepta su antigua clave y no se elimina dos veces");
            var sanitized = await admin.GetStringAsync("/api/admin/ldap"); Check(!sanitized.Contains(settings.Ldap.ProtectedPassword) && !sanitized.Contains("no-es-una-clave-real"), "API LDAP no revela contraseña ni secreto cifrado");
            var invalid = new LdapEdit(settings.Ldap.Host, 636, settings.Ldap.BaseDn, settings.Ldap.BindDn, "", [new() { Upn = "reader@example.test", Role = "Admin" }]);
            Check((await Post(admin, "/api/admin/ldap", invalid)).StatusCode == HttpStatusCode.BadRequest, "Cambio LDAP no puede retirar al administrador que lo realiza");
            var edit = new LdapEdit(settings.Ldap.Host, 636, settings.Ldap.BaseDn, settings.Ldap.BindDn, "", settings.Ldap.Users);
            Check((await Post(admin, "/api/admin/ldap", edit)).IsSuccessStatusCode && (await reader.GetAsync("/api/reports")).StatusCode == HttpStatusCode.Unauthorized, "Cambiar LDAP invalida las sesiones existentes");
            var response = await machine.GetAsync("/"); Check(response.Headers.Contains("Content-Security-Policy") && response.Headers.Contains("Strict-Transport-Security"), "Cabeceras CSP y HSTS activas");
            Check(ActiveDirectory.Escape("a*)(x=\0\\") == "a\\2a\\29\\28x=\\00\\5c", "Filtro LDAP escapa caracteres de inyección");
            Check(!ActiveDirectory.ValidateCertificate("otro.example.test", cert) && !ActiveDirectory.ValidateCertificate("localhost", cert), "LDAPS rechaza nombre incorrecto y certificados sin CA confiable");
            foreach (var invalidPath in new[] { "", "probackup.pfx", Path.Combine(root, "inexistente.pfx"), Path.Combine(root, "certificado.cer") })
            { bool rejectedPath = false; try { Bootstrap.PfxPath(invalidPath); } catch (ArgumentException) { rejectedPath = true; } Check(rejectedPath, "Asistente rechaza ruta PFX vacía, relativa, inexistente o sin clave privada"); }
            var throttle = new LoginThrottle(); Check(Enumerable.Range(0, 5).All(_ => throttle.Attempt("admin")) && !throttle.Attempt("admin"), "Límite de intentos por cuenta funciona");
            new Database(root).Backup(Path.Combine(root, "copy.db")); Check(File.Exists(Path.Combine(root, "copy.db")), "Copia consistente de base SQLite en funcionamiento");
            Check((await File.ReadAllTextAsync(Path.Combine(root, "server.json"))).Contains("ProtectedPassword") && Configuration.Unprotect(config.Current.Ldap.ProtectedPassword) == "no-es-una-clave-real", "Configuración secreta cifrada con DPAPI");
            var unsafeLdap = new LdapSettings { Host = "http://dc", BindDn = "x", BaseDn = "x" }; bool rejected = false; try { unsafeLdap.Validate(); } catch (ArgumentException) { rejected = true; } Check(rejected, "Configuración LDAP incompleta o con URL no admitida");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); await File.WriteAllTextAsync(reportPath, string.Join(Environment.NewLine, results) + "\nALL SERVER TESTS PASSED\n");
        }
        catch (Exception ex) { await File.WriteAllTextAsync(reportPath, string.Join(Environment.NewLine, results) + "\n" + ex); throw; }
        finally { await app.StopAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
