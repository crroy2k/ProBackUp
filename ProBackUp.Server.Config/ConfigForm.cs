using ProBackUp;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.DirectoryServices.ActiveDirectory;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
namespace ProBackUp.Server;
internal sealed class ConfigForm : Form
{
    private readonly string root;
    private readonly bool preview;
    private ServerSettings? saved;
    private bool busy, dirty;
    private bool dark = true;
    private bool? outcome;
    private readonly List<Panel> pages = [];
    private readonly List<Button> nav = [];
    private readonly List<Control> actions = [];
    private readonly TextBox host = new(), pfx = new(), pfxPassword = new() { UseSystemPasswordChar = true }, ldapHost = new(), baseDn = new(), bindDn = new(), bindPassword = new() { UseSystemPasswordChar = true };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 8445 }, ldapPort = new() { Minimum = 1, Maximum = 65535, Value = 636 };
    private readonly DataGridView users = new() { Dock = DockStyle.Fill, Height = 270, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(850, 0), Text = "Configura y valida el servidor antes de guardar.", Tag = "muted" }, service = new() { AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold) }, address = new() { AutoSize = true, MaximumSize = new Size(700, 0) }, certInfo = new() { AutoSize = true, MaximumSize = new Size(730, 0), Text = "Selecciona un PFX con clave privada. La contraseña nunca se muestra ni se guarda en texto plano.", Tag = "muted" }, ldapInfo = new() { AutoSize = true, MaximumSize = new Size(730, 0), Text = "La conexión siempre utiliza LDAPS y valida su certificado.", Tag = "muted" };
    private readonly CheckBox startAfterSave = new() { Text = "Iniciar/reiniciar el servicio después de guardar", Checked = true, AutoSize = true };
    private readonly Button theme = new() { Text = "Modo claro", AutoSize = true };
    internal bool Working => busy;
    internal ConfigForm(string? root = null, bool preview = false)
    {
        this.root = root ?? Configuration.DefaultRoot; this.preview = preview;
        Text = "ProBackUp Server · Configuración"; Size = new Size(1120, 860); MinimumSize = new Size(980, 760); StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        using var iconStream = typeof(ConfigForm).Assembly.GetManifestResourceStream("app.ico"); if (iconStream != null) Icon = new Icon(iconStream);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 }; shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(shell);
        var sidebar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 22, 18, 18), Tag = "sidebar" }; shell.Controls.Add(sidebar, 0, 0); shell.SetRowSpan(sidebar, 2);
        var side = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Tag = "sidebar" }; sidebar.Controls.Add(side);
        using var logo = typeof(ConfigForm).Assembly.GetManifestResourceStream("logo.png"); if (logo != null) side.Controls.Add(new PictureBox { Image = Image.FromStream(logo), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 0, 16) });
        side.Controls.Add(new Label { Text = "ProBackUp", Font = new Font("Segoe UI", 21, FontStyle.Bold), AutoSize = true }); side.Controls.Add(new Label { Text = "SERVER · CONFIGURACIÓN", AutoSize = true, Tag = "muted", Margin = new Padding(0, 0, 0, 30) });
        var hostPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(26, 24, 26, 12), Tag = "canvas" }; shell.Controls.Add(hostPanel, 1, 0);
        TableLayoutPanel Page(string title, string heading, string subtitle)
        {
            int index = pages.Count; var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = index == 0, Tag = "canvas" }; pages.Add(panel); hostPanel.Controls.Add(panel);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 12, 12) }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.Controls.Add(table);
            table.Controls.Add(new Label { Text = heading, Font = new Font("Segoe UI", 23, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
            table.Controls.Add(new Label { Text = subtitle, AutoSize = true, MaximumSize = new Size(750, 0), Tag = "muted", Margin = new Padding(0, 0, 0, 22) });
            var button = new Button { Text = title, Width = 195, Height = 48, TextAlign = ContentAlignment.MiddleLeft, Tag = index == 0 ? "nav-active" : "nav", Margin = new Padding(0, 0, 0, 8) }; button.Click += (_, _) => SelectPage(index); nav.Add(button); side.Controls.Add(button); return table;
        }
        void Label(TableLayoutPanel table, string text) => table.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 7) });
        void Field(TableLayoutPanel table, Control control) { control.Dock = DockStyle.Top; control.Margin = new Padding(0, 0, 0, 12); table.Controls.Add(control); }
        Button Action(string text, Func<Task> action, bool primary = false)
        { var button = new Button { Text = text, AutoSize = true, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 10, 8), Tag = primary ? "primary" : "secondary" }; button.Click += async (_, _) => await Run(action); actions.Add(button); return button; }
        var overview = Page("Resumen", "Tu servidor, bajo control", "Configura HTTPS y Active Directory, valida el acceso y administra el servicio desde aquí.");
        Label(overview, "Estado del servicio"); Field(overview, service); Label(overview, "Dirección del panel central"); Field(overview, address);
        var controls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        controls.Controls.Add(Action("Instalar servicio", async () => { await ServerControl.Install(); RefreshStatus(); SetStatus("Servicio preparado. Configura y guarda antes de iniciarlo.", true); }));
        controls.Controls.Add(Action("Iniciar", async () => { RequireSaved(); await Task.Run(ServerControl.Start); RefreshStatus(); SetStatus("Servidor iniciado.", true); }));
        controls.Controls.Add(Action("Detener", async () => { await Task.Run(ServerControl.Stop); RefreshStatus(); SetStatus("Servicio detenido.", true); }));
        controls.Controls.Add(Action("Abrir panel", () => { RequireSaved(); Process.Start(new ProcessStartInfo(Url(saved!)) { UseShellExecute = true }); return Task.CompletedTask; }));
        controls.Controls.Add(Action("Actualizar estado", () => { RefreshStatus(); return Task.CompletedTask; })); overview.Controls.Add(controls);
        Label(overview, "Cómo empezar"); Field(overview, new Label { Text = "1. Elige el nombre DNS, puerto y certificado PFX.\n2. Comprueba la cuenta de consulta de AD mediante LDAPS.\n3. Autoriza al menos un administrador del panel.\n4. Pulsa Guardar configuración; el servicio se inicia si marcas la opción inferior.", AutoSize = true, MaximumSize = new Size(730, 0), Tag = "muted" });
        Label(overview, "Almacenamiento protegido"); Field(overview, new Label { Text = this.root + "\nLas contraseñas vacías conservan las guardadas cuando no cambia el certificado o la cuenta. No se omite ninguna comprobación TLS.", AutoSize = true, MaximumSize = new Size(730, 0), Tag = "muted" });
        var https = Page("HTTPS y certificado", "Una conexión confiable", "El certificado debe incluir el nombre DNS del servidor y su clave privada. Usa el PFX emitido por tu CA interna.");
        Label(https, "Nombre DNS del servidor"); host.PlaceholderText = "probackup.empresa.local"; Field(https, host); Label(https, "Puerto HTTPS"); Field(https, port);
        Label(https, "Archivo PFX / P12 con clave privada"); Field(https, pfx);
        https.Controls.Add(Action("Examinar certificado…", () => { using var dialog = new OpenFileDialog { Filter = "Certificado con clave privada|*.pfx;*.p12", CheckFileExists = true }; if (dialog.ShowDialog(this) == DialogResult.OK) pfx.Text = dialog.FileName; return Task.CompletedTask; }));
        Label(https, "Contraseña del PFX"); Field(https, pfxPassword); https.Controls.Add(Action("Validar certificado y puerto", async () => { var settings = ReadCertificate(); var source = pfx.Text.Trim().Trim('"'); CheckPort(settings); certInfo.Text = await Task.Run(() => { using var cert = new Configuration(this.root, settings).Certificate(source); return "✓ " + cert.Subject + "\nEmisor: " + cert.Issuer + "\nVence: " + cert.NotAfter.ToString("yyyy-MM-dd") + " · Certificado y cadena válidos."; }); SetStatus("Certificado HTTPS y puerto verificados.", true); }, true)); Field(https, certInfo);
        var ldap = Page("Active Directory", "Acceso con tu directorio", "LDAPS cifra las consultas y verifica el certificado del controlador. Usa una cuenta dedicada con permisos de lectura.");
        Label(ldap, "Controlador de dominio (nombre DNS)"); Field(ldap, ldapHost); Label(ldap, "Puerto LDAPS"); Field(ldap, ldapPort); Label(ldap, "Base DN"); baseDn.PlaceholderText = "DC=empresa,DC=local"; Field(ldap, baseDn);
        Label(ldap, "Cuenta de consulta (UPN o DN)"); bindDn.PlaceholderText = "consulta@empresa.local"; Field(ldap, bindDn); Label(ldap, "Contraseña de consulta"); Field(ldap, bindPassword);
        var ldapButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        ldapButtons.Controls.Add(Action("Detectar dominio", DetectDomain)); ldapButtons.Controls.Add(Action("Probar LDAPS y administrador", async () => { var settings = ReadDirectory(ReadCertificate(false)); var found = await ConfigOperations.ValidateDirectory(settings, new ActiveDirectory()); ldapInfo.Text = "✓ Conexión LDAPS válida. Administrador encontrado: " + found.Upn; SetStatus("Active Directory verificado.", true); }, true)); ldap.Controls.Add(ldapButtons); Field(ldap, ldapInfo);
        var access = Page("Usuarios y acceso", "Solo las personas autorizadas", "Indica el UPN completo y el rol. Admin gestiona el servidor; Reader consulta reportes. El primer administrador debe existir en AD.");
        users.Columns.Add(new DataGridViewTextBoxColumn { Name = "Upn", HeaderText = "Usuario (UPN)", FillWeight = 72 }); users.Columns.Add(new DataGridViewComboBoxColumn { Name = "Role", HeaderText = "Rol", DataSource = new[] { "Admin", "Reader" }, FillWeight = 28, FlatStyle = FlatStyle.Flat }); Field(access, users); users.DataError += (_, e) => { e.ThrowException = false; SetStatus("El rol debe ser Admin o Reader.", false); };
        var userButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        userButtons.Controls.Add(Action("+ Usuario", () => { users.Rows.Add("", "Reader"); dirty = true; return Task.CompletedTask; }));
        userButtons.Controls.Add(Action("Quitar seleccionado", () => { if (users.CurrentRow != null) { users.Rows.Remove(users.CurrentRow); dirty = true; } return Task.CompletedTask; })); access.Controls.Add(userButtons);
        Field(access, new Label { Text = "El acceso al configurador local requiere administrador de Windows. El panel web solo admite los usuarios de esta lista. Guardar cambios invalida las sesiones web existentes.", AutoSize = true, MaximumSize = new Size(730, 0), Tag = "muted" });
        theme.Click += (_, _) => { dark = !dark; theme.Text = dark ? "Modo claro" : "Modo oscuro"; Appearance.Apply(this, dark); StatusColor(); }; side.Controls.Add(theme);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(26, 10, 26, 18), ColumnCount = 1 }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.Controls.Add(status); footer.Controls.Add(startAfterSave);
        footer.Controls.Add(Action("Guardar configuración", Save, true)); shell.Controls.Add(footer, 1, 1);
        foreach (var input in new Control[] { host, pfx, pfxPassword, ldapHost, baseDn, bindDn, bindPassword }) input.TextChanged += (_, _) => { dirty = true; };
        port.ValueChanged += (_, _) => dirty = true; ldapPort.ValueChanged += (_, _) => dirty = true; users.CellValueChanged += (_, _) => dirty = true;
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; return; } if (!preview && dirty && MessageBox.Show(this, "Hay cambios sin guardar. ¿Cerrar el configurador?", "ProBackUp Server", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true; };
        LoadSettings(); Appearance.Apply(this, dark); RefreshStatus();
    }
    private void SelectPage(int index) { for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; nav[i].Tag = i == index ? "nav-active" : "nav"; } pages[index].BringToFront(); Appearance.Apply(this, dark); StatusColor(); }
    private void SetStatus(string text, bool ok) { outcome = ok; status.Text = (ok ? "✓ " : "✕ ") + text; status.Tag = ""; status.ForeColor = ok ? Color.FromArgb(34, 197, 94) : Color.FromArgb(248, 113, 113); }
    private void StatusColor() { if (outcome is bool ok) status.ForeColor = ok ? Color.FromArgb(34, 197, 94) : Color.FromArgb(248, 113, 113); }
    private void RefreshStatus() { service.Text = preview ? "Detenido · vista previa" : ServerControl.Status(); address.Text = "https://" + host.Text.Trim() + ":" + (int)port.Value + "/"; }
    private static string Url(ServerSettings settings) => $"https://{settings.PublicHost}:{settings.Port}/";
    private void RequireSaved() { if (saved == null || dirty) throw new InvalidOperationException("Guarda la configuración validada antes de iniciar el servicio o abrir el panel."); }
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; actions.ForEach(c => c.Enabled = false); nav.ForEach(c => c.Enabled = false); theme.Enabled = false; startAfterSave.Enabled = false;
        foreach (var page in pages) page.Enabled = false;
        status.Text = "Comprobando…";
        try { if (!preview) await action(); }
        catch (Exception ex) { SetStatus(ConfigOperations.Friendly(ex), false); }
        finally { busy = false; actions.ForEach(c => c.Enabled = true); nav.ForEach(c => c.Enabled = true); foreach (var page in pages) page.Enabled = true; theme.Enabled = true; startAfterSave.Enabled = true; RefreshStatus(); }
    }
    private ServerSettings ReadCertificate(bool required = true)
    {
        var settings = new ServerSettings { PublicHost = host.Text.Trim(), Port = (int)port.Value };
        if (Uri.CheckHostName(settings.PublicHost) != UriHostNameType.Dns) throw new ArgumentException("Introduce un nombre DNS válido para HTTPS.");
        if (!required) return settings;
        var source = pfx.Text.Trim().Trim('"'); if (!Path.IsPathFullyQualified(source) || !File.Exists(source)) throw new ArgumentException("Selecciona un PFX existente con Examinar certificado.");
        settings.ProtectedPfxPassword = SavedPfxPassword(source); return settings;
    }
    private string SavedPfxPassword(string source)
    {
        if (pfxPassword.Text.Length > 0) { if (pfxPassword.Text.Length < 12) throw new ArgumentException("La contraseña del PFX debe tener al menos 12 caracteres."); return Configuration.Protect(pfxPassword.Text); }
        if (saved != null && Path.GetFullPath(source).Equals(Path.Combine(root, "server.pfx"), StringComparison.OrdinalIgnoreCase)) return saved.ProtectedPfxPassword;
        throw new ArgumentException("Introduce la contraseña del certificado PFX. Nunca se envía al chat ni al navegador.");
    }
    private ServerSettings ReadDirectory(ServerSettings settings)
    {
        users.EndEdit(); var list = new List<AllowedUser>();
        foreach (DataGridViewRow row in users.Rows) list.Add(new() { Upn = row.Cells[0].Value?.ToString()?.Trim() ?? "", Role = row.Cells[1].Value?.ToString() ?? "Reader" });
        string secret;
        if (bindPassword.Text.Length > 0) secret = Configuration.Protect(bindPassword.Text);
        else if (saved != null && saved.Ldap.Host.Equals(ldapHost.Text.Trim(), StringComparison.OrdinalIgnoreCase) && saved.Ldap.BindDn.Equals(bindDn.Text.Trim(), StringComparison.OrdinalIgnoreCase)) secret = saved.Ldap.ProtectedPassword;
        else throw new ArgumentException("Introduce la contraseña de la cuenta de consulta de AD.");
        settings.Ldap = new() { Host = ldapHost.Text.Trim(), Port = (int)ldapPort.Value, BaseDn = baseDn.Text.Trim(), BindDn = bindDn.Text.Trim(), ProtectedPassword = secret, Users = list }; settings.Ldap.Validate(); return settings;
    }
    private void CheckPort(ServerSettings settings) => ConfigOperations.CheckPort(settings.Port, !preview && ServerControl.Status() == "En ejecución" ? saved?.Port : null);
    private async Task Save()
    {
        var settings = ReadDirectory(ReadCertificate()); settings.Validate(); CheckPort(settings); var source = pfx.Text.Trim().Trim('"');
        await Task.Run(() => { using var certificate = new Configuration(root, settings).Certificate(source); });
        await ConfigOperations.ValidateDirectory(settings, new ActiveDirectory());
        bool start = startAfterSave.Checked;
        await Task.Run(() => ConfigOperations.Apply(settings, source, start));
        saved = settings; pfx.Text = Path.Combine(root, "server.pfx"); pfxPassword.Clear(); bindPassword.Clear(); dirty = false;
        certInfo.Text = "✓ Certificado guardado y verificado."; ldapInfo.Text = "✓ Active Directory validado."; SetStatus(startAfterSave.Checked ? "Configuración guardada. Servidor iniciado en " + Url(settings) : "Configuración guardada. El servicio permanece detenido.", true);
    }
    private void LoadSettings()
    {
        try { if (File.Exists(Path.Combine(root, "server.json"))) saved = Configuration.Load(root).Current; }
        catch (Exception ex) { SetStatus("No se pudo cargar la configuración: " + ConfigOperations.Friendly(ex), false); }
        var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
        if (saved != null)
        {
            host.Text = saved.PublicHost; port.Value = saved.Port; pfx.Text = Path.Combine(root, "server.pfx"); ldapHost.Text = saved.Ldap.Host; ldapPort.Value = saved.Ldap.Port; baseDn.Text = saved.Ldap.BaseDn; bindDn.Text = saved.Ldap.BindDn; foreach (var user in saved.Ldap.Users) users.Rows.Add(user.Upn, user.Role);
        }
        else
        {
            host.Text = domain.Length > 0 ? "probackup." + domain : "probackup.empresa.local";
            baseDn.Text = domain.Length > 0 ? string.Join(',', domain.Split('.').Select(p => "DC=" + p)) : "";
            for (int value = 8443; value <= 8455; value++) { if (preview) break; try { ConfigOperations.CheckPort(value); port.Value = value; break; } catch (IOException) { } }
            var candidate = Path.Combine(AppContext.BaseDirectory, "certificado", "privado", host.Text + ".pfx"); if (!preview && File.Exists(candidate)) pfx.Text = candidate;
            users.Rows.Add(domain.Length > 0 ? Environment.UserName + "@" + domain : "", "Admin");
        }
        dirty = false;
    }
    private async Task DetectDomain()
    {
        var result = await Task.Run(() => { using var domain = Domain.GetComputerDomain(); using var dc = domain.FindDomainController(); return (domain.Name, dc.Name); });
        ldapHost.Text = result.Item2; baseDn.Text = string.Join(',', result.Item1.Split('.').Select(p => "DC=" + p)); SetStatus("Dominio detectado: " + result.Item1, true);
    }
    internal void PreviewTheme(bool value) { dark = value; Appearance.Apply(this, dark); }
    internal void PreviewPage(int index) => SelectPage(index);
    internal void SetPreviewValues()
    { host.Text = "probackup.empresa.local"; port.Value = 8445; pfx.Text = @"D:\Certificados\probackup.pfx"; ldapHost.Text = "dc.empresa.local"; baseDn.Text = "DC=empresa,DC=local"; bindDn.Text = "consulta@empresa.local"; users.Rows.Clear(); users.Rows.Add("admin@empresa.local", "Admin"); users.Rows.Add("consulta@empresa.local", "Reader"); dirty = false; RefreshStatus(); }
}
