using System.Text.Json;

namespace ProBackUp;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.SequenceEqual(["--service"])) { System.ServiceProcess.ServiceBase.Run(new BackupService()); return 0; }
        if (args.Length == 2 && args[0] == "--session-probe") return SessionProbe.Run(args[1]).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length == 2 && args[0] == "--self-test") { SelfTests.Run(args[1]).GetAwaiter().GetResult(); return 0; }
            if (args.Length == 2 && args[0] == "--tray-test") { TrayTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--restore-test") { RestoreTests.Run(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--preview")
            {
                Store.Root = Path.Combine(Path.GetTempPath(), "ProBackUp-preview-" + Guid.NewGuid().ToString("N"));
                var demo = new AppSettings { Jobs = [new BackupJob { Name = "Documentos de trabajo", Destination = @"D:\Respaldos\Documentos", Sources = [@"C:\Usuarios\Documentos"], Daily = true, Time = "18:00" }, new BackupJob { Name = "Proyectos compartidos", Destination = @"\\NAS\Respaldos", Zip = false }], Mail = new MailSettings { Host = "smtp.tuempresa.com", From = "respaldos@tuempresa.com", Recipients = "administracion@tuempresa.com" } };
                Store.Record(new BackupRecord { JobName = "Documentos de trabajo", Status = "Correcto", Files = 1240, Bytes = 734003200, Finished = DateTimeOffset.Now, Artifact = @"D:\Respaldos\Documentos\Documentos-20261015.zip", EmailStatus = "Enviado" });
                using var form = new MainForm(demo, false); form.SavePreviews(args[1]); Directory.Delete(Store.Root, true); return 0;
            }
            if (args.Length == 2 && args[0] == "--run-job")
            {
                var settings = Store.Load(); var job = settings.Jobs.SingleOrDefault(j => j.Id == args[1]) ?? throw new ArgumentException("La tarea ya no existe.");
                var result = BackupEngine.Run(job, settings.Mail).GetAwaiter().GetResult(); return result.Status == "Correcto" ? 0 : 1;
            }
            if (args.Length > 0) throw new ArgumentException("Argumentos no reconocidos.");
            if (ProBackUp.Installation.ClientInstallation.Elevated)
            {
                ProBackUp.Installation.ClientInstallation.LaunchDesktop(Environment.ProcessPath!); return 0;
            }
            using (var current = System.Security.Principal.WindowsIdentity.GetCurrent())
                if (ProBackUp.Installation.ClientInstallation.OwnerSid != current.User?.Value) throw new IOException("Este cliente debe instalarse o repararse desde la sesión del usuario que realizará los respaldos. Contacte a T.I. Ext. 233.");
            MachineStorage.Initialize();
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ProBackUp-Activate-" + identity);
            using var instance = new Mutex(true, "Local\\ProBackUp-UI-" + identity, out var first);
            if (!first) { activation.Set(); return 0; }
            Application.Run(new MainForm(activation: activation)); return 0;
        }
        catch (Exception ex)
        {
            if (args.Length > 0)
            {
                var path = args.Length == 2 && args[0] is "--self-test" or "--tray-test" or "--restore-test" ? args[1] : Path.Combine(Store.Root, "last-error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.WriteAllText(path, ex.ToString());
            }
            else MessageBox.Show(ex.Message, "ProBackUp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
