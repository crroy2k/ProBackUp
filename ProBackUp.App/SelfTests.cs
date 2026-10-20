using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ProBackUp;
public static class SelfTests
{
    public static async Task Run(string report)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProBackUp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var results = new List<string>(); var oldRoot = Store.Root; Store.Root = Path.Combine(root, "state");
        void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); }
        async Task Reject(Func<Task> action, string name) { bool rejected = false; try { await action(); } catch { rejected = true; } Assert(rejected, name); }
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "vacía")); Directory.CreateDirectory(Path.Combine(source, "anidada"));
            await File.WriteAllTextAsync(Path.Combine(source, "datos.txt"), "Contenido original ñ á"); await File.WriteAllBytesAsync(Path.Combine(source, "anidada", "bin.dat"), Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray());
            var job = new BackupJob { Name = "Prueba", Sources = [source], Destination = Path.Combine(root, "backups") };
            var record = await BackupEngine.Run(job, new()); Assert(record.Status == "Correcto" && record.Files == 2 && File.Exists(record.Artifact), "Respaldo ZIP, binarios y Unicode");
            Assert(record.CopiedBytes == new FileInfo(record.Artifact).Length, "Métrica ZIP registra tamaño comprimido escrito al destino");
            var restored = await BackupEngine.Restore(record.Artifact, Path.Combine(root, "restore"), null, default);
            Assert(await File.ReadAllTextAsync(Path.Combine(restored, "01-source", "datos.txt")) == "Contenido original ñ á", "Restauración ZIP conserva contenido"); Assert(Directory.Exists(Path.Combine(restored, "01-source", "vacía")), "Conserva carpetas vacías");
            await File.WriteAllTextAsync(Path.Combine(source, "datos.txt"), "Nuevo contenido");
            job.Zip = false; var mirror = await BackupEngine.Run(job, new()); Assert(mirror.Status == "Correcto" && Directory.Exists(mirror.Artifact), "Copia espejo versionada");
            var mirrorRestored = await BackupEngine.Restore(mirror.Artifact, Path.Combine(root, "restore"), null, default);
            Assert(await File.ReadAllTextAsync(Path.Combine(mirrorRestored, "01-source", "datos.txt")) == "Nuevo contenido" && File.Exists(record.Artifact), "Restaura espejo y conserva versión anterior");
            await File.WriteAllTextAsync(Path.Combine(mirror.Artifact, "data", "01-source", "datos.txt"), "Manipulado");
            await Reject(() => BackupEngine.Restore(mirror.Artifact, Path.Combine(root, "restore"), null, default), "Detecta corrupción antes de restaurar");
            var unsafeJob = new BackupJob { Sources = [source], Destination = Path.Combine(source, "nested") };
            await Reject(() => BackupEngine.Create(unsafeJob, null, default), "Rechaza destino dentro del origen");
            using (var gate = Store.Acquire()) await Reject(() => BackupEngine.Run(job, new()), "Bloquea ejecuciones simultáneas");
            var failedJob = new BackupJob { Name = "Fallo esperado", Sources = [Path.Combine(root, "inexistente")], Destination = job.Destination };
            var failed = await BackupEngine.Run(failedJob, new()); Assert(failed.Status == "Error" && failed.Error.Length > 0, "Registra errores de respaldo");
            using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); var canceled = await BackupEngine.Run(job, new(), null, cancel.Token); Assert(canceled.Status == "Cancelado", "Cancelación registrada y sin respaldo parcial"); }
            Assert(!Directory.EnumerateFileSystemEntries(job.Destination).Any(p => p.EndsWith(".partial") || p.EndsWith(".partial.zip")), "Limpia archivos parciales");
            var malicious = Path.Combine(root, "malicioso.zip");
            using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("probackup-manifest.json").Open()); writer.Write(JsonSerializer.Serialize(new BackupManifest { Files = [new FileEntry("../../escape.txt", 0, new string('0', 64))] }));
            }
            await Reject(() => BackupEngine.Restore(malicious, Path.Combine(root, "restore"), null, default), "Rechaza rutas ZIP traversal"); Assert(!File.Exists(Path.Combine(root, "escape.txt")), "No escribe fuera del destino");
            var m = new MailSettings(); m.SetPassword("clave-local-de-prueba"); Assert(m.Password() == "clave-local-de-prueba" && !m.EncryptedPassword.Contains("clave-local"), "Contraseña protegida por DPAPI");
            var app = new AppSettings { Jobs = [job], Mail = m }; Store.Save(app); Store.Save(app); Assert(Store.Load().Jobs.Count == 1 && File.Exists(Store.SettingsPath + ".bak"), "Configuración persistente y copia de seguridad");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var smtpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(deadline.Token); using var stream = peer.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII); using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 localhost test SMTP"); bool data = false; var body = new StringBuilder();
                while (await reader.ReadLineAsync(deadline.Token) is { } line)
                {
                    if (data) { if (line == ".") { data = false; await writer.WriteLineAsync("250 queued"); } else body.AppendLine(line); }
                    else if (line.StartsWith("EHLO")) await writer.WriteLineAsync("250-localhost\r\n250 8BITMIME");
                    else if (line.StartsWith("DATA")) { data = true; await writer.WriteLineAsync("354 send data"); }
                    else if (line.StartsWith("QUIT")) { await writer.WriteLineAsync("221 bye"); break; }
                    else await writer.WriteLineAsync("250 OK");
                }
                return body.ToString();
            }, deadline.Token);
            var notification = new MailSettings { Enabled = true, Host = "127.0.0.1", Port = smtpPort, Security = "Sin TLS", From = "backup@example.test", Recipients = "admin@example.test", TimeoutSeconds = 5 };
            try { var notified = await BackupEngine.Run(job, notification, null, deadline.Token); var body = await server; Assert(notified.Status == "Correcto" && notified.EmailStatus == "Enviado" && body.Contains("Estado: Correcto") && body.Contains("admin@example.test"), "Respaldo envía notificación SMTP real contra servidor local"); } finally { listener.Stop(); }
            var emailFailure = await BackupEngine.Run(job, notification); Assert(emailFailure.Status == "Correcto" && emailFailure.EmailStatus.StartsWith("Error de correo"), "Fallo SMTP se registra separado del respaldo correcto");
            var backupFailure = await BackupEngine.Run(failedJob, notification); Assert(backupFailure.Status == "Error" && backupFailure.EmailStatus.StartsWith("Error de correo"), "Respaldo fallido intenta notificar el error");
            await Reject(() => MailService.Send(new MailSettings { Host = "localhost", Security = "Sin TLS", User = "usuario", From = "a@example.test", Recipients = "b@example.test" }, "", "", default), "Impide autenticación SMTP sin TLS");
            var scheduled = new BackupJob { Daily = true, Days = [DayOfWeek.Monday, DayOfWeek.Wednesday], Times = ["08:00", "18:00"], ScheduledFrom = new DateTime(2026, 10, 1), RunMissed = false };
            await Scheduler.Apply(scheduled);
            var monday = new DateTime(2026, 10, 12, 8, 0, 30);
            Assert(Scheduler.Due(scheduled, monday, [])?.key == "2026-10-12T08:00", "Planificador selecciona día y hora");
            Assert(Scheduler.Due(scheduled, monday.AddDays(1), []) == null, "No ejecuta en días desmarcados");
            Assert(Scheduler.Due(scheduled, monday, [new BackupRecord { JobId = scheduled.Id, ScheduleKey = "2026-10-12T08:00" }]) == null, "No duplica un horario ya intentado");
            scheduled.RunMissed = true; Assert(Scheduler.Due(scheduled, monday.AddHours(12), [])?.key == "2026-10-12T18:00", "Recupera solo el último horario pendiente");
            scheduled.Times = ["25:00"]; await Reject(() => Scheduler.Apply(scheduled), "Rechaza horarios inválidos");
            var incremental = new BackupJob { Name = "Incremental", Sources = [source], Destination = Path.Combine(root, "incremental"), Incremental = true };
            var first = await BackupEngine.Run(incremental, new()); Assert(first.Status == "Correcto" && first.ChangedFiles == 2, "Primer incremental guarda base completa");
            var objectFolder = Path.Combine(incremental.Destination, "ProBackUp-Incremental", incremental.Id, "objects"); int baseObjects = Directory.GetFiles(objectFolder).Length;
            var unchanged = await BackupEngine.Run(incremental, new()); Assert(unchanged.Status == "Correcto" && unchanged.ChangedFiles == 0 && Directory.GetFiles(objectFolder).Length == baseObjects, "Incremental sin cambios reutiliza contenido");
            Assert(unchanged.CopiedBytes == new FileInfo(unchanged.Artifact).Length, "Incremental sin cambios solo contabiliza metadatos nuevos");
            await File.WriteAllTextAsync(Path.Combine(source, "datos.txt"), "Versión 2 del día"); File.Delete(Path.Combine(source, "anidada", "bin.dat")); await File.WriteAllTextAsync(Path.Combine(source, "nuevo.txt"), "Nuevo archivo");
            var second = await BackupEngine.Run(incremental, new()); Assert(second.Status == "Correcto" && second.ChangedFiles == 2 && second.DeletedFiles == 1, "Incremental detecta cambios, archivos nuevos y eliminados");
            var oldRestore = await BackupEngine.Restore(first.Artifact, Path.Combine(root, "recover-old"), null, default); var newRestore = await BackupEngine.Restore(second.Artifact, Path.Combine(root, "recover-new"), null, default);
            Assert(await File.ReadAllTextAsync(Path.Combine(oldRestore, "01-source", "datos.txt")) == "Nuevo contenido" && File.Exists(Path.Combine(oldRestore, "01-source", "anidada", "bin.dat")), "Recupera versión anterior del día y archivo eliminado");
            Assert(await File.ReadAllTextAsync(Path.Combine(newRestore, "01-source", "datos.txt")) == "Versión 2 del día" && !File.Exists(Path.Combine(newRestore, "01-source", "anidada", "bin.dat")), "Recupera estado exacto de la nueva versión incremental");
            var oneFile = await IncrementalStore.RestoreFile(first.Artifact, "01-source/datos.txt", Path.Combine(root, "one-file"), default); Assert(await File.ReadAllTextAsync(Path.Combine(oneFile, "01-source", "datos.txt")) == "Nuevo contenido", "Restaura versión individual de un archivo");
            var manifest = await BackupArchive.ReadManifest(second.Artifact, default); var objectPath = IncrementalStore.ObjectPath(Path.GetDirectoryName(objectFolder)!, manifest.Files[0]); await File.WriteAllTextAsync(objectPath, "corrupto");
            await Reject(() => BackupEngine.Restore(second.Artifact, Path.Combine(root, "corrupt"), null, default), "Incremental detecta objeto corrupto y no publica restauración");
            Assert(Path.GetFileName(Path.GetDirectoryName(first.Artifact)) == DateTime.Now.ToString("yyyy-MM-dd"), "Agrupa versiones incrementales por día");
            await NetworkAccess.Test(new BackupJob { Destination = Path.Combine(root, "connection"), Network = new() }); Assert(!Directory.EnumerateFiles(Path.Combine(root, "connection")).Any(), "Prueba de acceso limpia archivo temporal");
            await Reject(() => NetworkAccess.Run(new NetworkSettings { Mode = "Other", User = "example" }, () => Task.FromResult(true)), "Requiere contraseña persistente para otra cuenta");
            Assert(Store.History().Count >= 4, "Historial de ejecuciones persistente");
            var workerJob = new BackupJob { Name = "Prueba del servicio", Sources = [source], Destination = Path.Combine(root, "worker"), Daily = true, Days = [DateTime.Now.DayOfWeek], Times = [DateTime.Now.ToString("HH:mm")], RunMissed = true, ScheduledFrom = DateTime.Now.AddMinutes(-2) };
            Store.Save(new AppSettings { Jobs = [workerJob] });
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                var worker = BackupService.Work(stop.Token);
                for (int i = 0; i < 100 && !Store.History().Any(r => r.JobId == workerJob.Id && r.Status == "Correcto"); i++) await Task.Delay(100);
                stop.Cancel(); await worker;
            }
            Assert(Store.History().Any(r => r.JobId == workerJob.Id && r.Status == "Correcto" && r.ScheduleKey.Length > 0), "Bucle del servicio ejecuta un horario sin interfaz");
            var personalNetwork = new BackupJob { Name = "Privado", Sources = [source], Destination = @"\\servidor\usuario", Network = new() };
            bool touchedNetwork = false;
            await Reject(() => NetworkAccess.RunJob(personalNetwork, () => { touchedNetwork = true; return Task.FromResult(true); }), "Respaldo de red sin credenciales se rechaza también al ejecutar localmente");
            Assert(!touchedNetwork, "No usa la sesión interactiva como alternativa para acceder al destino");
            personalNetwork.Network = new() { Mode = "Current", User = @"DOMINIO\usuario" }; personalNetwork.Network.SetPassword("prueba-no-real"); NetworkAccess.ValidateJob(personalNetwork);
            Assert(personalNetwork.Network.Password() == "prueba-no-real", "Valida identidad personal y recupera su contraseña cifrada localmente");
            personalNetwork.Network = new(); personalNetwork.Destination = Path.Combine(root, "local-destination"); personalNetwork.Sources = [@"\\servidor\usuario\datos"];
            await Reject(() => NetworkAccess.RunJob(personalNetwork, () => Task.FromResult(true)), "También exige cuenta personal cuando el origen es de red");
            Assert(NetworkAccess.IsNetworkPath(@"\\?\UNC\servidor\usuario") && !NetworkAccess.IsNetworkPath(@"\\?\C:\Datos"), "Distingue rutas UNC extendidas de rutas locales extendidas");
            await StoragePolicyTests.Run(root, Assert);
            await CentralTests.Run(root, Assert);
            await RemoteControlTests.Run(root, Assert);
            await UserSessionTests.Run(root, Assert);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); await File.WriteAllTextAsync(report, string.Join(Environment.NewLine, results) + Environment.NewLine + "ALL TESTS PASSED");
        }
        finally { Store.Root = oldRoot; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

