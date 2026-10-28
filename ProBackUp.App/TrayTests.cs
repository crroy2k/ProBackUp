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
                using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
                Check(same.User == current.Name && same.UserSid == current.User?.Value && same.Mode == "Session" && same.EncryptedPassword.Length == 0, "Editar tarea usa el usuario conectado y elimina la cuenta administrativa o contraseña anterior");
                credentials.SuggestPersonalAccount(@"DOMINIO\tercero");
                Check(credentials.ReadSettings().User == current.Name, "Una sugerencia de cuenta diferente no sustituye al usuario conectado");
            }
            using (var editor = new JobDialog(new BackupJob { MaxUploadMbps = 2.5m }))
            {
                var limit = (CheckBox)editor.Controls.Find("limitUploadSpeed", true).Single();
                var rate = (NumericUpDown)editor.Controls.Find("maxUploadMbps", true).Single();
                Check(!limit.Checked && !rate.Enabled && rate.Value == 2.5m, "Editor muestra límite en Mbps desactivado por defecto y conserva el valor guardado");
                limit.Checked = true; rate.Value = 1.25m;
                typeof(JobDialog).GetMethod("ReadFields", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [false]);
                Check(rate.Enabled && editor.Job.LimitUploadSpeed && editor.Job.MaxUploadMbps == 1.25m, "Activar y guardar límite conserva Mbps con decimales en la tarea");
                limit.Checked = false;
                typeof(JobDialog).GetMethod("ReadFields", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [false]);
                Check(!rate.Enabled && !editor.Job.LimitUploadSpeed && editor.Job.MaxUploadMbps == 1.25m, "Desactivar el límite elimina la restricción sin perder la velocidad seleccionada");
            }
            Directory.CreateDirectory(temp); var source = Path.Combine(temp, "apply-source.txt"); File.WriteAllText(source, "datos");
            using (var owner = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var configured = new BackupJob { Name = "Aplicar configuración", Sources = [source], Destination = Path.Combine(temp, "apply-destination"), Owner = owner.Name, Network = new() { Mode = "Session", User = owner.Name, UserSid = owner.User!.Value } };
                Store.Save(new AppSettings { Jobs = [configured] });
                using var editor = new JobDialog(configured);
                var status = (Label)editor.Controls.Find("configurationStatus", true).Single(); var apply = (Button)editor.Controls.Find("applyChanges", true).Single();
                var limit = (CheckBox)editor.Controls.Find("limitUploadSpeed", true).Single(); var rate = (NumericUpDown)editor.Controls.Find("maxUploadMbps", true).Single();
                Check(!editor.PendingChanges && !apply.Enabled && status.Text == "Sin cambios pendientes", "Editor existente comienza sin cambios y desactiva Aplicar cambios");
                limit.Checked = true; rate.Value = 5;
                Check(editor.PendingChanges && apply.Enabled && status.Text == "Cambios pendientes de aplicar" && !Store.Load().Jobs.Single().LimitUploadSpeed, "Editar marca cambios pendientes sin modificar la configuración activa");
                editor.ApplyDraft().GetAwaiter().GetResult();
                Check(!editor.PendingChanges && !apply.Enabled && status.Text == "Configuración aplicada" && Store.Load().Jobs.Single().MaxUploadMbps == 5 && Store.Load().Jobs.Single().LimitUploadSpeed, "Aplicar guarda realmente la tarea sin elevarse y confirma Configuración aplicada");
                rate.Value = 7;
                Check(editor.PendingChanges && status.Text == "Cambios pendientes de aplicar" && Store.Load().Jobs.Single().MaxUploadMbps == 5, "Una edición posterior vuelve a pendiente y conserva la última configuración aplicada");
                rate.Value = 5;
                Check(!editor.PendingChanges && !apply.Enabled && status.Text == "Configuración aplicada", "Revertir la edición al valor aplicado elimina el aviso pendiente");
                rate.Value = 7; bool failed = false;
                using (Store.Acquire()) { try { editor.ApplyDraft().GetAwaiter().GetResult(); } catch (IOException) { failed = true; } }
                Check(failed && editor.PendingChanges && apply.Enabled && status.Text == "Cambios pendientes de aplicar" && Store.Load().Jobs.Single().MaxUploadMbps == 5, "Si el guardado falla por operación en curso conserva pendientes y no muestra un éxito falso");
                editor.ApplyDraft().GetAwaiter().GetResult();
                Check(status.Text == "Configuración aplicada" && Store.Load().Jobs.Count == 1 && Store.Load().Jobs.Single().MaxUploadMbps == 7, "Reintentar aplica correctamente sin duplicar la tarea");
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
