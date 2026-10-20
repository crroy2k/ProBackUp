namespace ProBackUp;
internal static class TrayTests
{
    public static void Run(string report)
    {
        var previous = Store.Root; var temp = Path.Combine(Path.GetTempPath(), "ProBackUp-tray-test-" + Guid.NewGuid().ToString("N")); Store.Root = temp;
        var results = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        try
        {
            using (var credentials = new NetworkCredentialsEditor())
            {
                var personal = new NetworkSettings { Mode = "Current", User = @"DOMINIO\otro.usuario" }; personal.SetPassword("clave-ficticia-de-prueba");
                credentials.LoadSettings(personal); var same = credentials.ReadSettings();
                Check(same.User == personal.User && same.Mode == "Session" && same.EncryptedPassword.Length == 0, "Editar conserva el usuario guardado y migra a sesión sin contraseña");
                credentials.SuggestPersonalAccount(@"DOMINIO\tercero");
                Check(credentials.ReadSettings().User == personal.User, "Sugerencia de cuenta no sustituye una identidad explícita ya configurada");
            }
            using var activation = new EventWaitHandle(false, EventResetMode.AutoReset);
            using var form = new MainForm(new AppSettings(), activation: activation);
            form.Show(); Application.DoEvents(); Check(form.TrayVisible && form.Visible, "Crea icono al abrir la aplicación");
            form.Close(); Application.DoEvents(); Check(!form.Visible && !form.IsDisposed && form.TrayVisible, "Cerrar oculta la ventana y conserva el icono");
            activation.Set(); var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!form.Visible && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
            Check(form.Visible && form.ShowInTaskbar, "Activación restaura la instancia oculta");
            form.WindowState = FormWindowState.Minimized; Application.DoEvents(); Check(!form.Visible && form.TrayVisible, "Minimizar deja la aplicación en la bandeja");
            form.OpenFromTray(true); Application.DoEvents(); Check(form.Visible && form.WindowState == FormWindowState.Normal, "Abrir desde bandeja restaura la ventana");
            var tray = form.NotificationTray!;
            Store.Record(new BackupRecord { JobName = "Documentos de trabajo", Status = "Correcto", Files = 12, Finished = DateTimeOffset.Now });
            Store.Record(new BackupRecord { JobName = "Carpeta compartida", Status = "Error", Error = "Sin espacio", Finished = DateTimeOffset.Now.AddMilliseconds(1) });
            Store.Record(new BackupRecord { JobName = "Versión antigua", Status = "Eliminado por retención", Finished = DateTimeOffset.Now.AddMilliseconds(2) });
            tray.CheckNow(); Application.DoEvents();
            Check(tray.CurrentNotification is { Success: true, AutoDismiss: true, Heading: "Ejecutado con éxito" } && tray.CurrentNotification.StatusColor.G > tray.CurrentNotification.StatusColor.R, "Éxito muestra check verde, texto solicitado y cierre automático");
            Check(tray.PendingNotifications == 1, "Encola el error sin perderlo y no alerta por versiones depuradas");
            var preview = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "previews"); Directory.CreateDirectory(preview);
            void Capture(string file) { var popup = tray.CurrentNotification!; using var bitmap = new Bitmap(popup.Width, popup.Height); popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, popup.Size)); bitmap.Save(Path.Combine(preview, file)); }
            Capture("notificacion-exito.png");
            tray.CurrentNotification!.Close(); Application.DoEvents();
            Check(tray.CurrentNotification is { Success: false, AutoDismiss: false } && tray.CurrentNotification.Message.Contains("Llame a T.I. Ext. 233") && tray.CurrentNotification.StatusColor.R > tray.CurrentNotification.StatusColor.G, "Error muestra X roja y contacto T.I. Ext. 233, sin cierre automático");
            Capture("notificacion-error.png");
            tray.CheckNow(); Check(tray.PendingNotifications == 0, "No duplica notificaciones al consultar de nuevo");
            form.ExitFromTray(); Application.DoEvents(); Check(form.IsDisposed && !form.TrayVisible && tray.CurrentNotification == null, "Salir cierra y retira el icono");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); File.WriteAllText(report, string.Join(Environment.NewLine, results) + Environment.NewLine + "ALL TRAY TESTS PASSED");
        }
        finally { Store.Root = previous; if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
}
