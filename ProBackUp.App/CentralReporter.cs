using System.Net.Http.Json;
using System.Text.Json;
using ProBackUp.Central;
namespace ProBackUp;
public sealed class CentralSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string EncryptedToken { get; set; } = "";
    public void Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.HostNameType != UriHostNameType.Dns || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("Indica una dirección HTTPS con nombre DNS, sin rutas, credenciales ni parámetros.");
        var key = Secrets.Unprotect(EncryptedToken); if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("Introduce la clave de 64 caracteres generada para este equipo en el servidor.");
    }
}
internal static class CentralReporter
{
    private static readonly SemaphoreSlim gate = new(1);
    private static string Outbox => Path.Combine(Store.Root, "central-outbox");
    internal static void Enqueue(BackupRecord record)
    {
        if (record.Status is not ("Correcto" or "Error" or "Cancelado" or "Eliminado por retención")) return;
        try
        {
            if (!Store.Load().Central.Enabled) return;
            var report = new CentralReport { Id = record.Id, JobId = record.JobId, JobName = record.JobName, User = record.User, Destination = record.Destination, Artifact = record.Artifact, Format = record.Format, Status = record.Status, Started = record.Started, Finished = record.Finished, Bytes = record.Bytes, CopiedBytes = record.CopiedBytes, Files = record.Files, Error = record.Error[..Math.Min(4096, record.Error.Length)] };
            report.Validate(); BackupEngine.NoLinks(Outbox);
            Store.Write(Path.Combine(Outbox, DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N") + ".json"), report);
        }
        catch { try { Store.Write(Path.Combine(Store.Root, "central-status.json"), new { At = DateTimeOffset.Now, State = "No se pudo encolar el reporte. El historial local conserva el resultado. Contacte a T.I. Ext. 233." }); } catch { } }
    }
    internal static async Task Flush(CentralSettings settings, CancellationToken token, HttpMessageHandler? testTransport = null)
    {
        settings.Validate(); if (!await gate.WaitAsync(0, token)) return;
        try
        {
            Directory.CreateDirectory(Outbox); BackupEngine.NoLinks(Outbox);
            // A separate file lock also coordinates the GUI with the Windows service.
            await using var fileLock = new FileStream(Path.Combine(Store.Root, "central-report.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var paths = Directory.EnumerateFiles(Outbox, "*.json").Order(StringComparer.Ordinal).Take(100).ToList();
            var reports = new List<CentralReport>();
            foreach (var path in paths) { BackupEngine.NoLinks(path); var report = JsonSerializer.Deserialize<CentralReport>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException("Reporte vacío."); report.Validate(); reports.Add(report); if (JsonSerializer.SerializeToUtf8Bytes(new AgentBatch { MachineName = Environment.MachineName, Version = "3.4.1", Reports = reports }).Length > 512 * 1024) { reports.RemoveAt(reports.Count - 1); break; } }
            using var client = new HttpClient(testTransport ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = true }) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", Secrets.Unprotect(settings.EncryptedToken));
            using var response = await client.PostAsJsonAsync(new Uri(new Uri(settings.Url), "/api/agent/reports"), new AgentBatch { MachineName = Environment.MachineName, Version = "3.4.1", Reports = reports }, token);
            if (!response.IsSuccessStatusCode) throw new IOException(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "Clave revocada o no autorizada por el servidor." : "El servidor no confirmó el reporte. HTTP " + (int)response.StatusCode);
            foreach (var path in paths.Take(reports.Count)) File.Delete(path);
            Store.Write(Path.Combine(Store.Root, "central-status.json"), new { At = DateTimeOffset.Now, State = "Conectado", Pending = Directory.EnumerateFiles(Outbox, "*.json").Count() });
        }
        finally { gate.Release(); }
    }
    internal static async Task Work(CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(60);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var settings = Store.Load().Central;
                if (settings.Enabled) await Flush(settings, token);
                delay = TimeSpan.FromSeconds(60);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { try { Store.Write(Path.Combine(Store.Root, "central-status.json"), new { At = DateTimeOffset.Now, State = ex is IOException ? ex.Message : "No se pudo comunicar con el servidor. Revise DNS, certificado, configuración y clave. Los respaldos continúan y los reportes quedan pendientes." }); } catch { } delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2)); }
            try { await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000)), token); } catch (OperationCanceledException) { break; }
        }
    }
}
