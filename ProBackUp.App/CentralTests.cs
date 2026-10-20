using System.Net;
using System.Text.Json;
using ProBackUp.Central;
namespace ProBackUp;
internal static class CentralTests
{
    private sealed class Transport(bool success, Action<HttpRequestMessage, AgentBatch> inspect) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var batch = JsonSerializer.Deserialize<AgentBatch>(await request.Content!.ReadAsStringAsync(token), new JsonSerializerOptions(JsonSerializerDefaults.Web))!; inspect(request, batch); return new(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable); }
    }
    internal static async Task Run(string root, Action<bool, string> check)
    {
        var oldRoot = Store.Root; Store.Root = Path.Combine(root, "central-tests");
        try
        {
            var key = new string('A', 64); var settings = new CentralSettings { Enabled = true, Url = "https://central.example.test:8443/", EncryptedToken = Secrets.Protect(key) }; Store.Save(new AppSettings { Central = settings });
            var record = new BackupRecord { JobId = Guid.NewGuid().ToString("N"), JobName = "Reporte", Format = "Incremental", Status = "Correcto", Finished = DateTimeOffset.Now.AddSeconds(1), Destination = @"\\nas\backup", User = "usuario@example.test", Bytes = 21000, CopiedBytes = 300, Files = 2 };
            Store.Record(record); var outbox = Path.Combine(Store.Root, "central-outbox"); check(Directory.GetFiles(outbox).Length == 1, "Cliente encola resultados de forma persistente");
            void Inspect(HttpRequestMessage request, AgentBatch batch) { check(request.RequestUri!.Scheme == "https" && request.RequestUri.AbsolutePath == "/api/agent/reports" && request.Headers.Authorization?.Parameter == key && batch.Reports[0].CopiedBytes == 300, "Cliente envía por HTTPS, con clave y métricas correctas"); }
            bool failed = false; try { await CentralReporter.Flush(settings, default, new Transport(false, Inspect)); } catch (IOException) { failed = true; }
            check(failed && Directory.GetFiles(outbox).Length == 1 && Store.History().Single().Status == "Correcto", "Servidor caído conserva la cola y no cambia éxito del respaldo");
            await CentralReporter.Flush(settings, default, new Transport(true, Inspect)); check(Directory.GetFiles(outbox).Length == 0, "Confirmación del servidor elimina solo reportes enviados");
            var unsafeSettings = new CentralSettings { Url = "http://central.example.test", EncryptedToken = settings.EncryptedToken }; failed = false; try { unsafeSettings.Validate(); } catch (ArgumentException) { failed = true; }
            check(failed && !(await File.ReadAllTextAsync(Store.SettingsPath)).Contains(key), "Cliente rechaza HTTP y cifra su clave en configuración");
        }
        finally { Store.Root = oldRoot; }
    }
}
