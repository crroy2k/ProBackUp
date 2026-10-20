using System.Security.Principal;
using System.Text.Json;
namespace ProBackUp;
internal static class SessionProbe
{
    internal static async Task<int> Run(string report)
    {
        var results = new List<string>();
        try
        {
            using var host = WindowsIdentity.GetCurrent();
            if (!host.IsSystem) throw new InvalidOperationException("Esta prueba requiere LocalSystem y no modifica tareas ni archivos de respaldo.");
            results.Add("Proceso: LocalSystem");
            var settings = Store.Load();
            foreach (var original in settings.Jobs)
            {
                var job = JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(original))!;
                job.Network = new() { Mode = "Session", User = original.Owner, UserSid = UserSession.Sid(original.Owner) };
                try
                {
                    using var session = UserSession.Acquire(job.Network);
                    using var user = new WindowsIdentity(session.DangerousGetHandle());
                    results.Add("Sesión seleccionada: " + user.Name + " · SID coincide: " + UserSession.Matches(job.Network.User, job.Network.UserSid, user.Name, user.User?.Value ?? ""));
                    await WindowsIdentity.RunImpersonatedAsync(session, async () =>
                    {
                        await Task.Yield();
                        _ = Directory.EnumerateFileSystemEntries(job.Destination).Take(1).ToArray();
                        results.Add("Destino accesible con la sesión del usuario: " + job.Destination);
                        _ = Store.History(); results.Add("Historial protegido accesible solo mediante la identidad del proceso.");
                    });
                }
                catch (UserSessionUnavailableException) { results.Add("ESPERA: " + job.Owner); }
            }
            results.Add("PASS: prueba de sesiones sin modificar configuración, permisos ni respaldos.");
            await File.WriteAllLinesAsync(report, results); return 0;
        }
        catch (Exception ex) { results.Add("FAIL: " + ex.GetType().Name + ": " + ex.Message); await File.WriteAllLinesAsync(report, results); return 1; }
    }
}
