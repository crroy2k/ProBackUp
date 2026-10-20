using System.Globalization;
using System.ServiceProcess;
namespace ProBackUp;
public static class Scheduler
{
    public const string ServiceName = "ProBackUpService";
    public static List<string> Hours(BackupJob job) => job.Times.Count > 0 ? job.Times.Distinct().Order().ToList() : [job.Time];
    public static List<DayOfWeek> WeekDays(BackupJob job) => job.Days.Count > 0 ? job.Days.Distinct().ToList() : Enum.GetValues<DayOfWeek>().ToList();
    public static Task Apply(BackupJob job)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _)) throw new ArgumentException("ID de tarea inválido.");
        if (job.Days.Any(d => !Enum.IsDefined(d))) throw new ArgumentException("Día inválido.");
        if (job.Daily && Hours(job).Any(t => !TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) throw new ArgumentException("Usa horas HH:mm separadas por coma.");
        return Task.CompletedTask;
    }
    public static Task Remove(BackupJob job) => Task.CompletedTask;
    public static string Status()
    {
        try { using var service = new ServiceController(ServiceName); return service.Status == ServiceControllerStatus.Running ? "Servicio activo · espera la sesión del usuario" : "Servicio " + service.Status; }
        catch { return "Servicio no instalado · instala ProBackUp como administrador"; }
    }
    public static (DateTime moment, string key)? Due(BackupJob job, DateTime now, IEnumerable<BackupRecord> records, string sessionKey = "")
    {
        if (!job.Daily) return null;
        var history = records.Where(r => r.JobId == job.Id).ToList();
        var recover = job.RunMissed || job.Network.Mode == "Session";
        var due = new List<DateTime>(); int lookBack = recover ? 7 : 0;
        for (int day = 0; day <= lookBack; day++)
        {
            var date = now.Date.AddDays(-day); if (!WeekDays(job).Contains(date.DayOfWeek)) continue;
            foreach (var hour in Hours(job))
            {
                var at = date + TimeOnly.ParseExact(hour, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();
                if (at >= job.ScheduledFrom && at <= now && (recover || now - at < TimeSpan.FromMinutes(2))) due.Add(at);
            }
        }
        var latest = due.OrderDescending().FirstOrDefault(); if (latest == default) return null;
        var key = latest.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        if (sessionKey.Length == 0) return history.Any(r => r.ScheduleKey == key) ? null : (latest, key);
        // A successful manual/remote run also satisfies an overdue schedule.
        if (history.Any(r => r.Status == "Correcto" && r.Started.LocalDateTime >= latest)) return null;
        key += "@session:" + sessionKey;
        return history.Any(r => r.ScheduleKey == key) ? null : (latest, key);
    }
}
public sealed class BackupService : ServiceBase
{
    private CancellationTokenSource? stop;
    private Task? loop;
    private static readonly SemaphoreSlim wake = new(0, 1);
    public BackupService() { ServiceName = Scheduler.ServiceName; CanStop = true; CanHandleSessionChangeEvent = true; AutoLog = true; }
    protected override void OnSessionChange(SessionChangeDescription change)
    {
        try { wake.Release(); } catch (SemaphoreFullException) { }
    }
    protected override void OnStart(string[] args) { stop = new(); loop = Task.Run(() => Work(stop.Token)); }
    protected override void OnStop() { stop?.Cancel(); RequestAdditionalTime(30000); try { loop?.Wait(TimeSpan.FromSeconds(25)); } catch { } }
    public static async Task Work(CancellationToken token)
    {
        var reporting = CentralReporter.Work(token);
        var remote = RemoteControl.Work(token);
        try
        {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Store.Write(Path.Combine(Store.Root, "service-status.json"), new { Running = true, Updated = DateTimeOffset.Now });
                var settings = Store.Load(); var records = Store.History();
                foreach (var job in settings.Jobs)
                {
                    token.ThrowIfCancellationRequested(); await Scheduler.Apply(job);
                    try
                    {
                        using var session = NetworkAccess.AcquireJobSession(job);
                        var sessionKey = session == null ? "" : UserSession.Key(session);
                        if (Scheduler.Due(job, DateTime.Now, records, sessionKey) is not { } due) continue;
                        var result = await BackupEngine.Run(job, settings.Mail, null, token, due.key); records.Add(result);
                    }
                    catch (UserSessionUnavailableException)
                    { Store.Write(Path.Combine(Store.Root, "session-wait.json"), new { At = DateTimeOffset.Now, Job = job.Name, User = job.Network.User, State = "Pendiente de inicio de sesión" }); }
                    catch (IOException) { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Store.Write(Path.Combine(Store.Root, "service-error.json"), new { At = DateTimeOffset.Now, Error = ex.Message }); }
            try { await wake.WaitAsync(TimeSpan.FromSeconds(20), token); } catch (OperationCanceledException) { break; }
        }
        }
        finally { try { await Task.WhenAll(reporting, remote); } catch (OperationCanceledException) { } }
    }
}
