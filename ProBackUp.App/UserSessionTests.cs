using System.Security.Principal;
using ProBackUp.Central;
namespace ProBackUp;
internal static class UserSessionTests
{
    internal static async Task Run(string root, Action<bool, string> check)
    {
        var previous = Store.Root; Store.Root = Path.Combine(root, "session-tests"); Directory.CreateDirectory(Store.Root);
        try
        {
            var assigned = new NetworkSettings { Mode = "Session", UserSid = "S-1-5-21-100" };
            check(UserSession.AuthorizedSession("S-1-5-21-100", assigned) && !UserSession.AuthorizedSession("S-1-5-21-200", assigned) && !UserSession.AuthorizedSession("", assigned) && !UserSession.AuthorizedSession("S-1-5-21-100", new NetworkSettings { Mode = "Service", UserSid = assigned.UserSid }), "Servicio rechaza ejecutar como SYSTEM, otra cuenta o sin propietario registrado por el instalador");
            check(UserSession.Matches(@"DOMINIO\ana", "S-1-5-21-100", @"DOMINIO\ana.renombrada", "S-1-5-21-100") && !UserSession.Matches(@"DOMINIO\ana", "S-1-5-21-100", @"DOMINIO\ana", "S-1-5-21-200"), "La sesión se vincula al SID y nunca se sustituye por otra identidad");
            check(UserSession.Matches(@"DOMINIO\ana", "", @"dominio\ANA", "S-1-5-21-100") && !UserSession.Matches(@"DOMINIO\ana", "", @"OTRO\ana", "S-1-5-21-200") && !UserSession.Matches("ana", "", "ana", ""), "Sin SID solo coincide una cuenta cualificada exacta, no un usuario homónimo");
            using var identity = WindowsIdentity.GetCurrent();
            using (var token = UserSession.DuplicateCurrent())
            {
                var firstKey = UserSession.Key(token);
                using var second = UserSession.DuplicateCurrent();
                var sid = await WindowsIdentity.RunImpersonatedAsync(token, async () => { await Task.Delay(10); using var active = WindowsIdentity.GetCurrent(); return active.User?.Value; });
                using var tokenIdentity = new WindowsIdentity(token.DangerousGetHandle());
                check(token.IsInvalid == false && tokenIdentity.ImpersonationLevel >= TokenImpersonationLevel.Impersonation, "Token de escritorio permite suplantación real también con interfaz elevada por UAC");
                using var after = WindowsIdentity.GetCurrent();
                check(firstKey == UserSession.Key(second) && sid == identity.User?.Value && after.User?.Value == identity.User?.Value, "Token real conserva sesión a través de await, identifica el inicio y restaura la identidad original");
            }
            using (var desktop = UserSession.DuplicateDesktop())
            {
                var desktopSid = await WindowsIdentity.RunImpersonatedAsync(desktop, async () => { await Task.Yield(); using var owner = WindowsIdentity.GetCurrent(); return owner.User?.Value; });
                check(desktopSid == identity.User?.Value, "Ruta usada por UAC obtiene el token real del escritorio y permite acceder con su identidad");
            }
            var job = new BackupJob { Name = "Personal", Owner = identity.Name, Sources = [@"C:\Datos"], Destination = @"\\nas\privado", RunMissed = false, Network = new() { Mode = "Current", User = identity.Name } }; job.Network.SetPassword("clave-ficticia-antigua");
            var app = new AppSettings { Jobs = [job], Mail = new() }; app.Mail.SetPassword("clave-smtp-ficticia");
            check(UserSession.Migrate(app) && job.Network.Mode == "Session" && job.Network.UserSid == identity.User?.Value && job.Network.EncryptedPassword.Length == 0 && job.Owner == identity.Name && job.Destination == @"\\nas\privado" && job.RunMissed && app.Mail.Password() == "clave-smtp-ficticia", "Migración conserva tarea y SMTP, elimina contraseña de red y activa recuperación al iniciar sesión");
            check(!UserSession.Migrate(app), "Migración de sesión se aplica una sola vez");
            job.Network.UserSid = ""; Store.Save(app);
            using (var token = UserSession.DuplicateCurrent()) using (Store.Acquire()) UserSession.Bind(job, token);
            check(Store.Load().Jobs.Single().Network.UserSid == identity.User?.Value, "Primera ejecución fija el SID en disco para no adoptar una cuenta homónima futura");
            job.Network = new() { Mode = "Session", User = @"PROBACKUP_TEST\usuario-inexistente" };
            bool waiting = false; try { await BackupEngine.Run(job, new()); } catch (UserSessionUnavailableException) { waiting = true; }
            check(waiting && Store.History().Count == 0, "Sin sesión del dueño no registra un intento fallido ni consume la programación");
            var command = new RemoteCommand { Id = Guid.NewGuid().ToString("N"), JobId = job.Id, Expires = DateTimeOffset.UtcNow.AddHours(1) };
            await RemoteControl.Execute(command, app, default);
            check(Store.History().Count == 0 && File.ReadAllText(Path.Combine(Store.Root, "remote-results", command.Id + ".json")).Contains("Esperando sesi"), "Orden remota queda esperando al usuario sin ejecutar bajo otra cuenta");
            var scheduled = new BackupJob { Id = job.Id, Daily = true, RunMissed = true, Days = [DayOfWeek.Monday], Times = ["08:00", "18:00"], ScheduledFrom = new DateTime(2026, 1, 1) };
            var login = new DateTime(2026, 10, 19, 19, 0, 0); var due = Scheduler.Due(scheduled, login, [], "login-A")!.Value;
            check(due.key == "2026-10-19T18:00@session:login-A", "Al volver tras meses recoge solo el último horario vencido");
            var failed = new BackupRecord { JobId = job.Id, ScheduleKey = due.key, Status = "Error" };
            check(Scheduler.Due(scheduled, login, [failed], "login-A") == null && Scheduler.Due(scheduled, login, [failed], "login-B") != null, "Un fallo no se repite cada ciclo, pero se recupera en un nuevo inicio de sesión");
            var successful = new BackupRecord { JobId = job.Id, Status = "Correcto", Started = new DateTimeOffset(login.AddMinutes(-10)) };
            check(Scheduler.Due(scheduled, login, [successful], "login-B") == null, "Una copia local o remota reciente satisface el respaldo vencido sin duplicarlo");
        }
        finally { Store.Root = previous; }
    }
}
