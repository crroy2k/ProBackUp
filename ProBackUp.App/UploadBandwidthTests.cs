using System.Diagnostics;
using System.Text.Json;
namespace ProBackUp;
internal static class UploadBandwidthTests
{
    internal static async Task Run(string root, Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<BackupJob>("{\"Name\":\"Anterior\"}")!;
        check(!legacy.LimitUploadSpeed && legacy.MaxUploadMbps == 10, "Tareas nuevas y anteriores mantienen el límite de subida deshabilitado por defecto");
        double clock = 0; var waits = new List<double>();
        var budget = new UploadBandwidth(1, () => clock, (duration, token) => { token.ThrowIfCancellationRequested(); waits.Add(duration.TotalSeconds); clock += duration.TotalSeconds; return Task.CompletedTask; });
        await budget.Pace(125000, default);
        check(Math.Abs(clock - 1) < .000001, "Conversión exacta: 1 Mbps son 125000 bytes por segundo, sin confundir MB ni MiB");
        clock += 30; await budget.Pace(125000, default);
        check(Math.Abs(waits[^1] - 1) < .000001, "El tiempo leyendo o verificando no acumula crédito para una ráfaga posterior");
        await budget.Pace(62500, default); await budget.Pace(62500, default);
        check(Math.Abs(waits[^1] + waits[^2] - 1) < .000001, "El presupuesto se comparte entre archivos y escrituras, sin reiniciarse por archivo");
        int called = 0; var unlimited = new UploadBandwidth(0, () => 0, (_, _) => { called++; return Task.CompletedTask; });
        using var memory = new MemoryStream(); check(ReferenceEquals(memory, unlimited.Wrap(memory, default)), "Con límite desactivado se conserva la escritura directa sin envoltorio ni esperas");
        await unlimited.Pace(1000000, default); check(called == 0, "Modo ilimitado nunca agrega pausas");
        using (var cancelled = new CancellationTokenSource())
        {
            var slow = new UploadBandwidth(.1m); var pending = slow.Pace(1000000, cancelled.Token); cancelled.Cancel();
            bool stopped = false; try { await pending; } catch (OperationCanceledException) { stopped = true; }
            check(stopped, "Cancelar interrumpe de inmediato la espera del limitador");
        }
        var source = Path.Combine(root, "bandwidth-source"); Directory.CreateDirectory(source);
        var bytes = new byte[65536]; new Random(17).NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), bytes[..32768]); await File.WriteAllBytesAsync(Path.Combine(source, "b.bin"), bytes[32768..]);
        var previous = Store.Root; Store.Root = Path.Combine(root, "bandwidth-history");
        try
        {
            foreach (var type in new[] { "Espejo", "ZIP", "Incremental" })
            {
                var job = new BackupJob { Name = type, Sources = [source], Destination = Path.Combine(root, "bandwidth-" + type), Zip = type == "ZIP", Incremental = type == "Incremental", LimitUploadSpeed = true, MaxUploadMbps = 1 };
                var timer = Stopwatch.StartNew(); var first = await BackupEngine.Run(job, new()); timer.Stop();
                var minimumSeconds = bytes.Length / 125000d * (job.Zip ? 2 : 1);
                check(first.Status == "Correcto" && timer.Elapsed.TotalSeconds >= minimumSeconds * .95, type + ": transferencia real respeta el presupuesto compartido de 1 Mbps");
                var restored = await BackupEngine.Restore(first.Artifact, Path.Combine(root, "bandwidth-restore-" + type), null, default);
                check((await File.ReadAllBytesAsync(Path.Combine(restored, "01-bandwidth-source", "a.bin"))).SequenceEqual(bytes[..32768]), type + ": limitar la carga conserva los datos y su restauración verificada");
                if (job.Incremental)
                {
                    var second = await BackupEngine.Run(job, new());
                    check(second.Status == "Correcto" && second.ChangedFiles == 0 && second.CopiedBytes < bytes.Length, "Incremental con límite reutiliza los objetos y solo escribe metadatos cuando no hay cambios");
                }
                var persisted = JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(job))!;
                check(persisted.LimitUploadSpeed && persisted.MaxUploadMbps == 1, type + ": configuración persiste para ejecuciones locales, programadas y remotas");
            }
            var invalid = new BackupJob { Sources = [source], Destination = Path.Combine(root, "bad-speed"), LimitUploadSpeed = true, MaxUploadMbps = 0 };
            bool rejected = false; try { BackupEngine.ValidateDefinition(invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Límite activado con velocidad cero se rechaza antes de escribir al destino");
            var cancelJob = new BackupJob { Sources = [source], Destination = Path.Combine(root, "cancel-speed"), LimitUploadSpeed = true, MaxUploadMbps = .1m };
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var cancelledRun = await BackupEngine.Run(cancelJob, new(), null, stop.Token);
            check(cancelledRun.Status == "Cancelado" && !Directory.EnumerateFileSystemEntries(cancelJob.Destination).Any(p => Path.GetFileName(p).Contains("partial")), "Cancelación durante carga limitada elimina archivos parciales y conserva el historial");
        }
        finally { Store.Root = previous; }
    }
}
