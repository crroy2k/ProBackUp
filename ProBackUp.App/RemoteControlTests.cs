using System.Net;
using System.Net.Http.Json;
using ProBackUp.Central;
namespace ProBackUp;
internal static class RemoteControlTests
{
    private sealed class Transport(Action<HttpRequestMessage, RemoteSync> inspect, bool success = true) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var sync = await request.Content!.ReadFromJsonAsync<RemoteSync>(cancellationToken: token); inspect(request, sync!);
            return new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new RemoteReply()) };
        }
    }
    internal static async Task Run(string root, Action<bool, string> check)
    {
        var previous = Store.Root; Store.Root = Path.Combine(root, "remote-tests");
        try
        {
            var source = Path.Combine(Store.Root, "source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "test.txt"), "datos de prueba");
            var job = new BackupJob { Name = "Remoto", Sources = [source], Destination = Path.Combine(Store.Root, "destination"), Incremental = true };
            var settings = new AppSettings { Jobs = [job], Central = new() { Enabled = true, Url = "https://central.example.test/", EncryptedToken = Secrets.Protect(new string('B', 64)) } }; Store.Save(settings);
            var command = new RemoteCommand { Id = Guid.NewGuid().ToString("N"), JobId = job.Id, Expires = DateTimeOffset.UtcNow.AddHours(1) };
            bool announced = false;
            await RemoteControl.Execute(command, settings, default, () => { announced = true; throw new IOException("Servidor desconectado"); });
            var record = Store.History().Single();
            check(announced && record.Id == command.Id && record.Status == "Correcto" && record.ScheduleKey == "remote:" + command.Id, "Orden remota ejecuta tarea local aun si falla la confirmación inicial de red");
            await RemoteControl.Execute(command, settings, default);
            check(Store.History().Count == 1 && Directory.GetFiles(Path.GetDirectoryName(record.Artifact)!, "*.pbi").Length == 1, "Reentrega de una orden no duplica historial ni copia de respaldo");
            var busy = new RemoteCommand { Id = Guid.NewGuid().ToString("N"), JobId = job.Id, Expires = command.Expires };
            using (Store.Acquire()) await RemoteControl.Execute(busy, settings, default);
            check(Store.History().Count == 1 && !File.Exists(Path.Combine(Store.Root, "remote-results", busy.Id + ".json")), "Operación local ocupada deja la orden pendiente sin iniciar otra copia");
            var expired = new RemoteCommand { Id = Guid.NewGuid().ToString("N"), JobId = job.Id, Expires = DateTimeOffset.UtcNow.AddSeconds(-1) }; await RemoteControl.Execute(expired, settings, default);
            check(Store.History().Count == 1 && File.ReadAllText(Path.Combine(Store.Root, "remote-results", expired.Id + ".json")).Contains("Rechazado"), "Cliente rechaza órdenes caducadas antes de copiar");
            var unknown = new RemoteCommand { Id = Guid.NewGuid().ToString("N"), JobId = Guid.NewGuid().ToString("N"), Expires = command.Expires }; await RemoteControl.Execute(unknown, settings, default);
            check(Store.History().Count == 1, "Servidor no puede ejecutar una tarea que no existe localmente");
            var interrupted = new BackupRecord { Id = Guid.NewGuid().ToString("N"), JobId = job.Id, Status = "En curso" }; Store.Record(interrupted);
            Store.Write(Path.Combine(Store.Root, "remote-results", interrupted.Id + ".json"), new RemoteResult { Id = interrupted.Id, State = "En curso", RecordId = interrupted.Id }); RemoteControl.Recover();
            check(File.ReadAllText(Path.Combine(Store.Root, "remote-results", interrupted.Id + ".json")).Contains("interrumpida"), "Reinicio comunica ejecución interrumpida sin repetirla");
            await RemoteControl.Execute(new RemoteCommand { Id = interrupted.Id, JobId = job.Id, Expires = command.Expires }, settings, default);
            check(Store.History().Count == 2, "Orden interrumpida conserva su identidad y no se ejecuta dos veces");
            var privateJob = new BackupJob { Name = "Carpeta privada", Sources = [source], Destination = @"\\servidor\privado" };
            var privateCommand = new RemoteCommand { Id = Guid.NewGuid().ToString("N"), JobId = privateJob.Id, Expires = command.Expires };
            await RemoteControl.Execute(privateCommand, new AppSettings { Jobs = [privateJob] }, default);
            var denied = Store.History().Single(r => r.Id == privateCommand.Id);
            check(denied.Status == "Error" && denied.Error.Contains("guarda su contraseña") && privateJob.Network.Mode == "Service", "Remoto rechaza una carpeta privada sin identidad persistida y nunca cambia la cuenta del cliente");
            var count = Directory.GetFiles(Path.Combine(Store.Root, "remote-results")).Length;
            try { await RemoteControl.Sync(settings, default, new Transport((_, _) => { }, false)); } catch (HttpRequestException) { }
            check(Directory.GetFiles(Path.Combine(Store.Root, "remote-results")).Length == count, "Sin confirmación HTTPS conserva resultados para reintentar");
            await RemoteControl.Sync(settings, default, new Transport((request, sync) => check(request.Headers.Authorization?.Parameter == new string('B', 64) && request.RequestUri!.AbsolutePath == "/api/agent/sync" && sync.Jobs.Single().Id == job.Id && sync.Results.Count == count && !System.Text.Json.JsonSerializer.Serialize(sync).Contains("Encrypted"), "Sincroniza inventario y resultados autenticados sin transmitir secretos")));
            check(Directory.GetFiles(Path.Combine(Store.Root, "remote-results")).Length == 0, "Confirmación elimina resultados terminales ya entregados");
        }
        finally { Store.Root = previous; }
    }
}
