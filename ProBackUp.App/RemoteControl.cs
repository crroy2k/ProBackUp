using System.Net.Http.Json;
using System.Text.Json;
using ProBackUp.Central;
namespace ProBackUp;
internal static class RemoteControl
{
    private static string Receipts => Path.Combine(Store.Root, "remote-results");
    private static string ReceiptPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Orden inválida.");
        BackupEngine.NoLinks(Receipts); return Path.Combine(Receipts, id + ".json");
    }
    private static void Save(RemoteResult result) => Store.Write(ReceiptPath(result.Id), result);
    private static RemoteResult Result(BackupRecord record) => new() { Id = record.Id, RecordId = record.Id, State = record.Status == "En curso" ? "Error" : record.Status is "Correcto" or "Cancelado" ? record.Status : "Error", Message = record.Status == "En curso" ? "Ejecución interrumpida; no se repite automáticamente. Revise el historial local." : record.Status == "Correcto" ? "Respaldo completado. Consulte el historial central." : "El respaldo no se completó. Consulte el historial del cliente. T.I. Ext. 233." };
    internal static void Recover()
    {
        if (!Directory.Exists(Receipts)) return;
        BackupEngine.NoLinks(Receipts);
        foreach (var path in Directory.EnumerateFiles(Receipts, "*.json"))
        {
            BackupEngine.NoLinks(path); var receipt = JsonSerializer.Deserialize<RemoteResult>(File.ReadAllText(path))!;
            if (receipt.State != "En curso") continue;
            var record = Store.History().SingleOrDefault(r => r.Id == receipt.Id);
            Save(record == null ? new() { Id = receipt.Id, State = "Error", Message = "Ejecución interrumpida; revise el cliente antes de enviar otra orden." } : Result(record));
        }
    }
    internal static async Task<RemoteReply> Sync(AppSettings settings, CancellationToken token, HttpMessageHandler? transport = null)
    {
        settings.Central.Validate(); Directory.CreateDirectory(Receipts); BackupEngine.NoLinks(Receipts);
        var paths = Directory.EnumerateFiles(Receipts, "*.json").Order(StringComparer.Ordinal).Take(100).ToList();
        var results = new List<RemoteResult>(); foreach (var path in paths) { BackupEngine.NoLinks(path); results.Add(JsonSerializer.Deserialize<RemoteResult>(await File.ReadAllTextAsync(path, token))!); }
        var sync = new RemoteSync { Jobs = settings.Jobs.Select(j => new RemoteJob { Id = j.Id, Name = j.Name, Format = BackupArchive.FromJob(j).ToString(), Destination = j.Destination }).ToList(), Results = results };
        foreach (var job in sync.Jobs) job.Validate();
        using var client = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = true }) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", Secrets.Unprotect(settings.Central.EncryptedToken));
        using var response = await client.PostAsJsonAsync(new Uri(new Uri(settings.Central.Url), "/api/agent/sync"), sync, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 65536) throw new InvalidDataException("Respuesta de órdenes demasiado grande.");
        var reply = await response.Content.ReadFromJsonAsync<RemoteReply>(cancellationToken: token) ?? throw new InvalidDataException("Respuesta vacía.");
        if (reply.Commands.Count > 200) throw new InvalidDataException("Demasiadas órdenes.");
        for (int i = 0; i < paths.Count; i++) if (results[i].State != "En curso") File.Delete(paths[i]);
        return reply;
    }
    internal static async Task Execute(RemoteCommand command, AppSettings settings, CancellationToken token, Func<Task>? announce = null)
    {
        var receipt = ReceiptPath(command.Id);
        if (!Guid.TryParseExact(command.JobId, "N", out _)) throw new ArgumentException("Tarea inválida.");
        var existing = Store.History().SingleOrDefault(r => r.Id == command.Id);
        if (existing != null) { Save(Result(existing)); return; }
        var job = settings.Jobs.SingleOrDefault(j => j.Id == command.JobId);
        if (command.Expires <= DateTimeOffset.UtcNow || job == null)
        { Save(new() { Id = command.Id, State = "Rechazado", Message = job == null ? "La tarea ya no existe en el cliente." : "La orden caducó." }); return; }
        try
        {
            var record = await BackupEngine.Run(job, settings.Mail, null, token, "remote:" + command.Id, command.Id, async () =>
            {
                Save(new() { Id = command.Id, RecordId = command.Id, State = "En curso", Message = "Respaldo iniciado por el servicio del cliente." });
                if (announce != null) { try { await announce(); } catch { /* Network errors must not cancel a started backup. */ } }
            });
            Save(Result(record));
        }
        catch (UserSessionUnavailableException)
        { Save(new() { Id = command.Id, State = "Esperando sesión", Message = "Se ejecutará cuando el usuario de la tarea inicie sesión en Windows." }); }
        catch (IOException) when (!File.Exists(Path.Combine(Store.Root, "history", command.Id + ".json"))) { /* Local operation is busy: leave the server request pending. */ }
    }
    internal static async Task Work(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var settings = Store.Load();
                if (settings.Central.Enabled)
                {
                    // Only the service consumes orders. Lock prevents concurrent consumers after restarts.
                    await using var gate = new FileStream(Path.Combine(Store.Root, "remote-control.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    Recover(); var reply = await Sync(settings, token);
                    foreach (var command in reply.Commands)
                    {
                        var current = Store.Load();
                        if (current.Central.Enabled && current.Central.Url == settings.Central.Url && current.Central.EncryptedToken == settings.Central.EncryptedToken)
                            await Execute(command, current, token, async () => { await Sync(settings, token); });
                    }
                    if (reply.Commands.Count > 0) await Sync(Store.Load(), token);
                    Store.Write(Path.Combine(Store.Root, "remote-status.json"), new { At = DateTimeOffset.Now, State = "Control remoto conectado" });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { try { Store.Write(Path.Combine(Store.Root, "remote-status.json"), new { At = DateTimeOffset.Now, State = "Sin conexión de control remoto. Los respaldos locales continúan." }); } catch { } }
            try { await Task.Delay(TimeSpan.FromSeconds(15), token); } catch (OperationCanceledException) { break; }
        }
    }
}
