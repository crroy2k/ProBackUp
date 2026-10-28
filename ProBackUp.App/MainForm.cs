using System.Diagnostics;

namespace ProBackUp;
public sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(22, 37, 61), Muted = Color.FromArgb(91, 108, 131), Accent = Color.FromArgb(0, 112, 103), Background = Color.FromArgb(243, 246, 250);
    private AppSettings settings;
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill, Tag = "canvas" };
    private readonly List<Panel> pages = [];
    private readonly FlowLayoutPanel navigation = new() { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(14, 20, 8, 0) };
    private readonly List<Button> navButtons = [];
    private readonly Button themeToggle = new() { AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(14, 8, 14, 8), Cursor = Cursors.Hand };
    private int selectedPage;
    private Button? editButton, runButton, deleteButton, restoreButton, versionsButton;
    private readonly Label emptyJobs = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Tu primer respaldo empieza aquí\r\n\r\nPulsa + Nuevo respaldo para elegir archivos y un destino.", Font = new Font("Segoe UI", 13), Tag = "muted" };
    private readonly DataGridView jobs = Grid(), history = Grid();
    private readonly Label status = new() { Dock = DockStyle.Fill, Text = "Listo para proteger tus archivos", ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label stats = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 6, 0, 20) };
    private readonly TextBox host = new(), user = new(), password = new() { UseSystemPasswordChar = true }, from = new(), recipients = new();
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 587 }, timeout = new() { Minimum = 5, Maximum = 120, Value = 30 };
    private readonly FlowLayoutPanel security = new() { AutoSize = true, WrapContents = false };
    private readonly RadioButton startTls = new() { Text = "STARTTLS", AutoSize = true, Checked = true }, sslTls = new() { Text = "SSL/TLS", AutoSize = true }, noTls = new() { Text = "Sin TLS", AutoSize = true };
    private readonly CheckBox enabled = new() { Text = "Activar notificaciones por correo", AutoSize = true }, success = new() { Text = "Cuando el respaldo termina correctamente", AutoSize = true }, failure = new() { Text = "Cuando ocurre un error o se cancela", AutoSize = true };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly List<Button> actions = [];
    private CancellationTokenSource? running;
    private SystemTray? tray;
    private System.Windows.Forms.Timer? activationTimer;
    private bool exiting;
    internal bool OperationRunning => running != null;
    internal SystemTray? NotificationTray => tray;
    internal bool TrayVisible => tray?.Visible == true;
    private readonly Button cancel = new() { Text = "Cancelar operación", AutoSize = true, Enabled = false, Visible = false };

    public MainForm(AppSettings? initial = null, bool useTray = true, EventWaitHandle? activation = null)
    {
        settings = initial ?? Store.Load();
        Text = "ProBackUp · Respalda con confianza"; Size = new Size(1280, 880); MinimumSize = new Size(1120, 800); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); ForeColor = Ink; BackColor = Background; AutoScaleMode = AutoScaleMode.Dpi;
        var iconResource = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"); if (iconResource != null) Icon = new Icon(iconResource);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(0), Margin = new Padding(0) };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 94)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var banner = new TableLayoutPanel { Dock = DockStyle.Fill, Tag = "sidebar", Margin = new Padding(0), Padding = new Padding(28, 10, 26, 10), ColumnCount = 2 };
        banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        brand.Controls.Add(new Label { Text = "ProBackUp", Font = new Font("Segoe UI", 26, FontStyle.Bold), AutoSize = true, Margin = new Padding(0) });
        brand.Controls.Add(new Label { Text = "Protege tus archivos. Mantén el control.", AutoSize = true, ForeColor = Muted, Margin = new Padding(2, 0, 0, 0) });
        banner.Controls.Add(brand, 0, 0); themeToggle.Anchor = AnchorStyles.Right; banner.Controls.Add(themeToggle, 1, 0);
        var logoStream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
        if (logoStream != null) { var logo = new PictureBox { Image = Image.FromStream(logoStream), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Left, Width = 68, Margin = new Padding(0, 0, 16, 0) }; var branded = new Panel { Dock = DockStyle.Fill }; banner.Controls.Remove(brand); brand.Dock = DockStyle.Fill; branded.Controls.Add(brand); branded.Controls.Add(logo); banner.Controls.Add(branded, 0, 0); }
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0), Tag = "canvas" }; content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200)); content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = new Panel { Dock = DockStyle.Fill, Tag = "sidebar", Margin = new Padding(0) }; sidebar.Controls.Add(navigation);
        sidebar.Controls.Add(new Label { Text = "ASÍ DE FÁCIL\r\n\r\n1. Elige tus archivos\r\n2. Define un destino\r\n3. Ejecuta el respaldo", Dock = DockStyle.Bottom, Height = 164, Padding = new Padding(22, 20, 12, 12), ForeColor = Muted, Tag = "muted" });
        var workspace = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22), Tag = "canvas", Margin = new Padding(0) }; workspace.Controls.Add(pageHost); content.Controls.Add(sidebar, 0, 0); content.Controls.Add(workspace, 1, 0);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(28, 3, 24, 4) }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.Controls.Add(status, 0, 0); footer.Controls.Add(cancel, 1, 0);
        cancel.Click += (_, _) => running?.Cancel(); shell.Controls.Add(banner, 0, 0); shell.Controls.Add(content, 0, 1); shell.Controls.Add(footer, 0, 2); Controls.Add(shell);
        BuildJobs(); BuildHistory(); BuildSettings(); LoadMail();
        Appearance.MarkMuted(this); RefreshAll(); SelectPage(0);
        themeToggle.Click += (_, _) => Guard(() => { using var gate = Store.Acquire(); var updated = Store.Load(); updated.DarkMode = !settings.DarkMode; Store.Save(updated); settings.DarkMode = updated.DarkMode; ApplyTheme(); });
        if (useTray) Shown += (_, _) => tray ??= new SystemTray(this);
        Resize += (_, _) => { if (tray != null && WindowState == FormWindowState.Minimized) HideToTray(); };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            if (tray != null && !exiting) { e.Cancel = true; HideToTray(); return; }
            if (running != null) { e.Cancel = true; MessageBox.Show(this, "Cancela la operación y espera a que termine antes de salir.", "Operación en curso"); }
        };
        FormClosed += (_, _) => { tray?.Dispose(); tray = null; };
        if (activation != null)
        {
            activationTimer = new System.Windows.Forms.Timer { Interval = 500 };
            activationTimer.Tick += (_, _) => { if (activation.WaitOne(0)) OpenFromTray(); };
            activationTimer.Start(); FormClosed += (_, _) => activationTimer.Dispose();
        }
    }
    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        EnableHeadersVisualStyles = false, ColumnHeadersHeight = 40, RowTemplate = { Height = 42 },
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(235, 241, 246), ForeColor = Ink, Font = new Font("Segoe UI", 10, FontStyle.Bold), Padding = new Padding(8) },
        DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(8), SelectionBackColor = Color.FromArgb(218, 241, 235), SelectionForeColor = Ink },
        AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(249, 251, 253) }, GridColor = Color.FromArgb(231, 237, 242)
    };
    protected override void Dispose(bool disposing)
    {
        if (disposing) { activationTimer?.Dispose(); tray?.Dispose(); tray = null; }
        base.Dispose(disposing);
    }
    private Button Action(string text, EventHandler callback, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 38, Padding = new Padding(12, 7, 12, 7), FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Margin = new Padding(0, 0, 10, 10), Cursor = Cursors.Hand };
        button.Tag = primary ? "primary" : "secondary"; button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(207, 218, 229); button.Click += callback; actions.Add(button); return button;
    }
    private static void Columns(DataGridView grid, params (string key, string caption, int width)[] cols)
    { foreach (var col in cols) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = col.key, HeaderText = col.caption, FillWeight = col.width }); }
    private TableLayoutPanel Page(string name, string heading, string subtitle)
    {
        var tab = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), Tag = "surface", Visible = false }; pages.Add(tab); pageHost.Controls.Add(tab);
        int index = pages.Count - 1;
        var nav = new Button { Text = name, Width = 168, Height = 48, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Margin = new Padding(0, 0, 0, 8), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Tag = "nav" };
        nav.Click += (_, _) => SelectPage(index); navButtons.Add(nav); navigation.Controls.Add(nav);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = heading, Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 5) }, 0, 0);
        layout.Controls.Add(new Label { Text = subtitle, ForeColor = Muted, AutoSize = true, MaximumSize = new Size(940, 0), Margin = new Padding(0, 0, 0, 20) }, 0, 1); tab.Controls.Add(layout); return layout;
    }
    private void BuildJobs()
    {
        var page = Page("Respaldos", "Tus respaldos", "Completos o incrementales, con versiones por día. El servicio ejecuta la programación aunque cierres sesión.");
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        toolbar.Controls.Add(Action("+ Nuevo respaldo", async (_, _) => await EditJob(null), true)); toolbar.Controls.Add(editButton = Action("Editar", async (_, _) => { if (SelectedJob() is { } j) await EditJob(j); }));
        toolbar.Controls.Add(runButton = Action("Respaldar ahora", async (_, _) => { if (SelectedJob() is { } j) await RunJob(j); }, true));
        toolbar.Controls.Add(deleteButton = Action("Eliminar tarea", async (_, _) => await DeleteJob()));
        page.Controls.Add(toolbar, 0, 2);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = new Padding(0) }; body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        body.Controls.Add(stats, 0, 0); Columns(jobs, ("Name", "Tarea", 27), ("Type", "Tipo", 18), ("Destination", "Destino", 35), ("Schedule", "Programación", 20)); var list = new Panel { Dock = DockStyle.Fill }; list.Controls.Add(jobs); list.Controls.Add(emptyJobs); body.Controls.Add(list, 0, 1);
        body.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Completo: copia todos los archivos. Incremental: guarda solo el contenido nuevo o modificado.\r\n" + Scheduler.Status(), ForeColor = Muted, Padding = new Padding(0, 14, 0, 0) }, 0, 2); page.Controls.Add(body, 0, 3);
        jobs.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && SelectedJob() is { } j) await EditJob(j); };
        jobs.SelectionChanged += (_, _) => UpdateActions();
    }
    private void BuildHistory()
    {
        var page = Page("Historial", "Historial y restauración", "Revisa el resultado de cada respaldo. Para recuperar archivos, selecciona una versión y pulsa Restaurar.");
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        toolbar.Controls.Add(Action("Actualizar", (_, _) => RefreshAll())); toolbar.Controls.Add(restoreButton = Action("Restaurar seleccionado", async (_, _) => { if (SelectedRecord() is { Status: "Correcto" } r) await Restore(r.Artifact); else Info("Selecciona un respaldo correcto para restaurar."); }, true));
        toolbar.Controls.Add(Action("Abrir otro respaldo…", async (_, _) => await Restore("")));
        toolbar.Controls.Add(versionsButton = Action("Versiones de archivos", (_, _) => { if (SelectedRecord() is { Status: "Correcto" } record) { using var dialog = new FileVersionsDialog(record, settings); dialog.ShowDialog(this); } else Info("Selecciona una ejecución correcta del historial."); }));
        page.Controls.Add(toolbar, 0, 2);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0) }; body.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        Columns(history, ("Started", "Fecha y hora", 24), ("Name", "Tarea", 23), ("Type", "Tipo", 24), ("Status", "Resultado", 13), ("Files", "Archivos", 10), ("Email", "Correo", 18)); body.Controls.Add(history, 0, 0); details.Margin = new Padding(0, 14, 0, 0); body.Controls.Add(details, 0, 1); page.Controls.Add(body, 0, 3);
        history.SelectionChanged += (_, _) => { UpdateActions(); if (SelectedRecord() is { } r) details.Text = $"{r.JobName}  ·  {r.Status}\r\nInicio: {r.Started:yyyy-MM-dd HH:mm:ss.fff zzz}    Fin: {r.Finished:yyyy-MM-dd HH:mm:ss.fff zzz}\r\nArchivos: {r.Files}    Tamaño: {SizeText(r.Bytes)}\r\nRespaldo: {r.Artifact}\r\nCorreo: {r.EmailStatus}\r\n{r.Error}"; else details.Text = "Aquí aparecerán los detalles del respaldo seleccionado."; };
    }
    private void BuildSettings()
    {
        var page = Page("Configuración", "Avisos por correo", "Recibe un aviso cuando un respaldo termine o necesite atención. Configura tu correo, guarda y envía una prueba.");
        page.RowStyles[2] = new RowStyle(SizeType.Percent, 100); page.RowStyles[3] = new RowStyle(SizeType.AutoSize);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
        var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 4, Padding = new Padding(0, 0, 12, 10) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        table.Controls.Add(enabled, 0, 0); table.SetColumnSpan(enabled, 4); enabled.Margin = new Padding(0, 0, 0, 16);
        Field(table, 1, 0, "Servidor SMTP", host); Field(table, 1, 2, "Puerto", port);
        security.Controls.AddRange([startTls, sslTls, noTls]); Field(table, 2, 0, "Seguridad", security); Field(table, 2, 2, "Espera (seg.)", timeout);
        Field(table, 3, 0, "Usuario", user); Field(table, 3, 2, "Contraseña", password);
        Field(table, 4, 0, "Remitente", from); table.SetColumnSpan(from, 3);
        Field(table, 5, 0, "Destinatarios", recipients); table.SetColumnSpan(recipients, 3);
        var hint = new Label { Text = "Separa destinatarios con ;  ·  Puerto habitual: 587 / STARTTLS o 465 / SSL/TLS.\r\nDeja usuario vacío para un relay sin autenticación. Si dejas la clave vacía, se conserva la guardada.\r\nLas cuentas que exigen OAuth necesitan un relay SMTP o una contraseña de aplicación admitida.", AutoSize = true, ForeColor = Muted, MaximumSize = new Size(850, 0), Margin = new Padding(0, 8, 0, 16) };
        table.Controls.Add(hint, 0, 6); table.SetColumnSpan(hint, 4); table.Controls.Add(success, 0, 7); table.SetColumnSpan(success, 4); table.Controls.Add(failure, 0, 8); table.SetColumnSpan(failure, 4);
        var removePassword = new CheckBox { Text = "Eliminar la contraseña guardada al guardar", AutoSize = true, Tag = "clear" }; table.Controls.Add(removePassword, 0, 9); table.SetColumnSpan(removePassword, 4);
        var location = new Label { Text = "Configuración e historial: " + Store.Root, AutoSize = true, ForeColor = Muted, MaximumSize = new Size(850, 0), Margin = new Padding(0, 18, 0, 8) }; table.Controls.Add(location, 0, 10); table.SetColumnSpan(location, 4);
        scroll.Controls.Add(table); page.Controls.Add(scroll, 0, 2);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        toolbar.Controls.Add(Action("Guardar configuración", (_, _) => Guard(() => { using var gate = Store.Acquire(); var next = ReadMail(); if (removePassword.Checked) next.SetPassword(""); if (next.Enabled) MailService.Validate(next); var updated = Store.Load(); updated.Mail = next; Store.Save(updated); settings = updated; password.Clear(); removePassword.Checked = false; RefreshAll(); status.Text = "Configuración guardada"; }), true));
        toolbar.Controls.Add(Action("Enviar correo de prueba", async (_, _) => await Busy(async token => { var mail = ReadMail(); MailService.Validate(mail); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(mail.TimeoutSeconds)); try { await MailService.Send(mail, "ProBackUp · correo de prueba", "La configuración SMTP funciona. Este correo de prueba fue solicitado desde ProBackUp.", deadline.Token); } catch (Exception ex) { throw new IOException(MailService.SafeError(ex, mail)); } Info("Correo de prueba enviado al servidor SMTP. Revisa la bandeja de entrada o spam."); })));
        toolbar.Controls.Add(Action("Servidor central…", (_, _) => { using var dialog = new CentralDialog(settings.DarkMode); dialog.ShowDialog(this); settings = Store.Load(); }));
        toolbar.Controls.Add(Action("Buscar actualizaciones", async (_, _) => await UpdateClient()));
        toolbar.Controls.Add(Action("Reparar servicio (administrador)…", async (_, _) => await Busy(async token =>
        {
            var setup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProBackUp", "Uninstall.exe");
            if (!File.Exists(setup)) throw new IOException("Ejecuta el instalador de ProBackUp para instalar el servicio.");
            using var process = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", Arguments = "--repair-service" })!;
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new IOException("No se pudo reparar el servicio. Contacte a T.I. Ext. 233.");
            status.Text = Scheduler.Status();
        })));
        toolbar.Controls.Add(Action("Abrir carpeta de datos", (_, _) => Guard(() => { Directory.CreateDirectory(Store.Root); Process.Start(new ProcessStartInfo(Store.Root) { UseShellExecute = true }); }))); page.Controls.Add(toolbar, 0, 3);
    }
    private static void Field(TableLayoutPanel table, int row, int col, string caption, Control control)
    {
        table.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 12) }, col, row);
        control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 3, 16, 12); table.Controls.Add(control, col + 1, row);
    }
    private void LoadMail()
    {
        var m = settings.Mail; enabled.Checked = m.Enabled; host.Text = m.Host; port.Value = Math.Clamp(m.Port, 1, 65535); startTls.Checked = m.Security == "STARTTLS"; sslTls.Checked = m.Security == "SSL/TLS"; noTls.Checked = m.Security == "Sin TLS"; user.Text = m.User; from.Text = m.From; recipients.Text = m.Recipients;
        timeout.Value = Math.Clamp(m.TimeoutSeconds, 5, 120); success.Checked = m.OnSuccess; failure.Checked = m.OnFailure;
    }
    private async Task UpdateClient()
    {
        string? installer = null;
        await Busy(async token =>
        {
            status.Text = "Buscando una versión del cliente en GitHub…";
            using var client = ClientUpdates.Client(); var release = await ClientUpdates.Latest(client, token);
            if (release == null) { Info("Todavía no hay una versión estable del cliente publicada en GitHub Releases."); return; }
            if (release.Version <= ClientUpdates.Current) { Info("ProBackUp está actualizado. Versión instalada: " + ClientUpdates.Current.ToString(3)); return; }
            if (MessageBox.Show(this, $"Está disponible ProBackUp {release.Version}. ¿Descargar la actualización?", "Actualizar ProBackUp", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProBackUp", "updates");
            var downloaded = await ClientUpdates.Download(client, release, cache, Progress(), token);
            if (MessageBox.Show(this, "Instalador descargado y verificado. ¿Instalar ahora? ProBackUp se cerrará y Windows pedirá permisos de administrador para actualizar la aplicación y el servicio.", "Instalar actualización", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) installer = downloaded;
            else status.Text = "Actualización descargada: " + downloaded;
        });
        if (installer == null) return;
        Guard(() => { using var process = ClientUpdates.StartInstaller(installer); exiting = true; Close(); });
    }
    private MailSettings ReadMail()
    {
        var m = new MailSettings { Enabled = enabled.Checked, Host = host.Text.Trim(), Port = (int)port.Value, Security = sslTls.Checked ? "SSL/TLS" : noTls.Checked ? "Sin TLS" : "STARTTLS", User = user.Text.Trim(), From = from.Text.Trim(), Recipients = recipients.Text.Trim(), TimeoutSeconds = (int)timeout.Value, OnSuccess = success.Checked, OnFailure = failure.Checked, EncryptedPassword = settings.Mail.EncryptedPassword };
        if (password.Text.Length > 0) m.SetPassword(password.Text); return m;
    }
    private BackupJob? SelectedJob() => jobs.CurrentRow?.Tag as BackupJob;
    private BackupRecord? SelectedRecord() => history.CurrentRow?.Tag as BackupRecord;
    private void RefreshAll()
    {
        var jobId = SelectedJob()?.Id; var recordId = SelectedRecord()?.Id;
        jobs.Rows.Clear(); foreach (var j in settings.Jobs) { int i = jobs.Rows.Add(j.Name, BackupArchive.Label(BackupArchive.FromJob(j)), j.Destination, j.Daily ? string.Join(", ", Scheduler.WeekDays(j).Select(d => new[] { "Dom", "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb" }[(int)d])) + " · " + string.Join(", ", Scheduler.Hours(j)) : "Manual"); jobs.Rows[i].Tag = j; }
        history.Rows.Clear(); var records = Store.History(); foreach (var r in records) { int i = history.Rows.Add(r.Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), r.JobName, BackupArchive.Label(BackupArchive.FromRecord(r)), r.Status, r.Files, r.EmailStatus); history.Rows[i].Tag = r; }
        stats.Text = $"{settings.Jobs.Count} tareas configuradas     ·     {records.Count(r => r.Status == "Correcto")} respaldos correctos     ·     Correo {(settings.Mail.Enabled ? "activado" : "desactivado")}";
        foreach (DataGridViewRow row in jobs.Rows) if ((row.Tag as BackupJob)?.Id == jobId) { jobs.ClearSelection(); jobs.CurrentCell = row.Cells[0]; row.Selected = true; break; }
        foreach (DataGridViewRow row in history.Rows) if ((row.Tag as BackupRecord)?.Id == recordId) { history.ClearSelection(); history.CurrentCell = row.Cells[0]; row.Selected = true; break; }
        emptyJobs.Visible = settings.Jobs.Count == 0; if (emptyJobs.Visible) emptyJobs.BringToFront(); UpdateActions(); if (pages.Count == 3) ApplyTheme();
    }
    private Task EditJob(BackupJob? existing)
    {
        using var editor = new JobDialog(existing, settings.DarkMode, async job =>
        {
            await JobConfiguration.Apply(job);
            settings = Store.Load(); RefreshAll(); status.Text = "Configuración aplicada";
        });
        editor.ShowDialog(this); return Task.CompletedTask;
    }
    private async Task DeleteJob()
    {
        if (SelectedJob() is not { } job) return;
        if (MessageBox.Show(this, $"¿Eliminar la tarea «{job.Name}»? Se conservarán los respaldos y el historial.", "Eliminar tarea", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await Busy(async _ => { using var gate = Store.Acquire(); await Scheduler.Remove(job); settings.Jobs.RemoveAll(j => j.Id == job.Id); Store.Save(settings); RefreshAll(); });
    }
    private async Task RunJob(BackupJob job) => await Busy(async token =>
    {
        var progress = Progress();
        var result = await Task.Run(() => BackupEngine.Run(job, settings.Mail, progress, token), token); RefreshAll();
        status.Text = result.Status + " · " + job.Name;
        tray?.CheckNow();
        if (Visible) { if (result.Status != "Correcto" && tray == null) Info(ResultNotification.Support + "\r\n" + result.Error); else if (result.Status == "Correcto" && result.EmailStatus.StartsWith("Error")) Info("El respaldo se completó, pero falló la notificación.\r\n" + result.EmailStatus); }
    });
    private async Task Restore(string artifact)
    {
        var record = Store.History().FirstOrDefault(r => r.Artifact == artifact);
        var credentials = settings.Jobs.FirstOrDefault(j => j.Id == record?.JobId)?.Network ?? new();
        using var dialog = new RestoreDialog(settings.DarkMode, artifact, credentials, record == null ? BackupKind.FullZip : BackupArchive.FromRecord(record));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Busy(async token => { var progress = Progress(); var path = await Task.Run(() => RestoreOperations.Run(dialog.Request!, progress, token), token); status.Text = "Restauración verificada"; Info("Archivos restaurados en:\r\n" + path); });
    }
    private IProgress<string> Progress() => new Progress<string>(s => status.Text = s);
    private async Task Busy(Func<CancellationToken, Task> operation)
    {
        if (running != null) return; running = new(); actions.ForEach(b => b.Enabled = false); themeToggle.Enabled = false; cancel.Enabled = true; cancel.Visible = true;
        try { await operation(running.Token); }
        catch (OperationCanceledException) { status.Text = "Operación cancelada"; }
        catch (Exception ex) { status.Text = "No se completó la operación"; if (Visible) MessageBox.Show(this, ex.Message, "ProBackUp", MessageBoxButtons.OK, MessageBoxIcon.Warning); else tray?.Notify("No se pudo completar la operación", "Abre ProBackUp para revisar el estado o vuelve a intentarlo.", ToolTipIcon.Warning); }
        finally { running.Dispose(); running = null; actions.ForEach(b => b.Enabled = true); themeToggle.Enabled = true; cancel.Enabled = false; cancel.Visible = false; UpdateActions(); }
    }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "ProBackUp", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    private void HideToTray() { Hide(); ShowInTaskbar = false; tray?.HiddenHint(); }
    internal void OpenFromTray(bool showHistory = false)
    {
        ShowInTaskbar = true; Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        RefreshAll(); if (showHistory) SelectPage(1); Activate(); BringToFront();
    }
    internal async Task RunFromTray(string id)
    {
        if (OperationRunning) return;
        try { var current = Store.Load(); var job = current.Jobs.FirstOrDefault(j => j.Id == id); if (job == null) return; settings = current; await RunJob(job); }
        catch (Exception ex) { tray?.Notify("No se pudo iniciar el respaldo", ex.Message, ToolTipIcon.Warning); }
    }
    internal void ExitFromTray()
    {
        if (OperationRunning) { OpenFromTray(); Info("Hay una operación en curso. Espera a que termine o cancélala antes de salir del icono."); return; }
        exiting = true; Close();
    }
    private void UpdateActions()
    {
        bool ready = running == null;
        if (editButton != null) editButton.Enabled = ready && SelectedJob() != null;
        if (runButton != null) runButton.Enabled = ready && SelectedJob() != null;
        if (deleteButton != null) deleteButton.Enabled = ready && SelectedJob() != null;
        if (restoreButton != null) restoreButton.Enabled = ready && SelectedRecord()?.Status == "Correcto";
        if (versionsButton != null) versionsButton.Enabled = ready && SelectedRecord()?.Status == "Correcto";
    }
    private void SelectPage(int index)
    {
        selectedPage = index;
        for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; navButtons[i].Tag = i == index ? "nav-active" : "nav"; }
        pages[index].BringToFront(); ApplyTheme();
    }
    private void ApplyTheme()
    {
        themeToggle.Text = settings.DarkMode ? "☀  Modo claro" : "☾  Modo oscuro";
        Appearance.Apply(this, settings.DarkMode);
    }
    private void Info(string text) => MessageBox.Show(this, text, "ProBackUp", MessageBoxButtons.OK, MessageBoxIcon.Information);
    private static string SizeText(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:F2} GB" : $"{bytes / 1048576d:F2} MB";
    public void SavePreviews(string directory)
    {
        Directory.CreateDirectory(directory); Show(); Application.DoEvents();
        bool original = settings.DarkMode;
        foreach (bool dark in new[] { true, false })
        {
            settings.DarkMode = dark;
            for (int i = 0; i < pages.Count; i++)
            { SelectPage(i); Application.DoEvents(); using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, $"{(dark ? "oscuro" : "claro")}-{i + 1}.png")); }
            using var editor = new JobDialog(null, dark); editor.Show(); Application.DoEvents(); using var dialogImage = new Bitmap(editor.Width, editor.Height); editor.DrawToBitmap(dialogImage, new Rectangle(Point.Empty, editor.Size)); dialogImage.Save(Path.Combine(directory, $"{(dark ? "oscuro" : "claro")}-nuevo-respaldo.png")); editor.Dispose();
        }
        settings.DarkMode = original; SelectPage(selectedPage);
        Hide();
    }
}



