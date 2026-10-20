using System.Text.Json;
using System.Security.Principal;
namespace ProBackUp;
public sealed class JobDialog : Form
{
    public BackupJob Job { get; private set; }
    private readonly TextBox name = new(), destination = new(), hours = new();
    private readonly ListBox sources = new() { Height = 175, Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly ThemeComboBox kind = new();
    private readonly NumericUpDown retention = new() { Minimum = 0, Maximum = 36500, Width = 180 }, limit = new() { Minimum = 0, Maximum = 1000000, DecimalPlaces = 2, Width = 180 };
    private readonly CheckBox clean = new() { Text = "Liberar espacio eliminando las versiones más antiguas", AutoSize = true }, manualLimit = new() { Text = "Establecer un límite manual (opcional)", AutoSize = true };
    private readonly NetworkCredentialsEditor credentials = new();
    private readonly CheckBox scheduled = new() { Text = "Programar respaldo mientras el usuario tenga sesión iniciada", AutoSize = true }, missed = new() { Text = "Ejecutar el último respaldo vencido al iniciar sesión", AutoSize = true };
    private readonly List<(DayOfWeek day, CheckBox control)> days = [];
    public JobDialog(BackupJob? existing, bool dark = true)
    {
        Job = existing == null ? new() : JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(existing))!;
        Text = "ProBackUp · Configurar respaldo"; Size = new Size(860, 810); MinimumSize = new Size(800, 740); Font = new Font("Segoe UI", 10); StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var iconResource = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"); if (iconResource != null) Icon = new Icon(iconResource);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 4 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(new Label { Text = "Tu respaldo, a tu medida", Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 15) }, 0, 0);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true }; var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 12), Tag = "surface" }; shell.Controls.Add(nav, 0, 1); shell.Controls.Add(host, 0, 2);
        var panels = new List<Panel>(); var buttons = new List<Button>();
        TableLayoutPanel Section(string title)
        {
            int index = panels.Count; var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = index == 0, Padding = new Padding(16), Tag = "surface" }; panels.Add(page); host.Controls.Add(page);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); page.Controls.Add(table);
            var button = new Button { Text = title, AutoSize = true, Padding = new Padding(12, 8, 12, 8), Tag = index == 0 ? "primary" : "secondary" }; buttons.Add(button); nav.Controls.Add(button);
            button.Click += (_, _) => { for (int i = 0; i < panels.Count; i++) { panels[i].Visible = i == index; buttons[i].Tag = i == index ? "primary" : "secondary"; } panels[index].BringToFront(); Appearance.Apply(this, dark); }; return table;
        }
        void Label(TableLayoutPanel table, string text, bool hint = false) => table.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(730, 0), Tag = hint ? "muted" : "", Margin = new Padding(0, 8, 0, 8) });
        void Field(TableLayoutPanel table, Control control) { control.Dock = DockStyle.Top; control.Margin = new Padding(0, 0, 0, 10); table.Controls.Add(control); }
        Button Button(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true, Padding = new Padding(10, 6, 10, 6) }; b.Click += click; return b; }
        var files = Section("1  Archivos y tipo"); Label(files, "Nombre del respaldo"); Field(files, name); Label(files, "Archivos y carpetas que quieres proteger"); Field(files, sources);
        var add = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        void Add(string p) { p = NetworkAccess.Normalize(p); if (!sources.Items.Contains(p)) sources.Items.Add(p); }
        add.Controls.Add(Button("+ Carpeta", (_, _) => { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK) Add(d.SelectedPath); }));
        add.Controls.Add(Button("+ Archivos", (_, _) => { using var d = new OpenFileDialog { Multiselect = true }; if (d.ShowDialog(this) == DialogResult.OK) foreach (var p in d.FileNames) Add(p); }));
        add.Controls.Add(Button("Quitar", (_, _) => { if (sources.SelectedIndex >= 0) sources.Items.RemoveAt(sources.SelectedIndex); })); files.Controls.Add(add);
        Label(files, "Tipo de respaldo"); kind.Items.AddRange(BackupArchive.Labels); Field(files, kind);
        Label(files, "Completo copia todos los archivos. Incremental reutiliza los datos sin cambios y conserva una versión recuperable en cada ejecución. La conservación de versiones se configura en Almacenamiento.", true);
        var network = Section("2  Destino y acceso"); Label(network, "Carpeta local, unidad de red o carpeta compartida"); destination.PlaceholderText = @"D:\Respaldos o \\servidor\compartida\Respaldos"; Field(network, destination);
        network.Controls.Add(Button("Examinar destino…", (_, _) => { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK) destination.Text = NetworkAccess.Normalize(d.SelectedPath); }));
        credentials.LoadSettings(Job.Network); credentials.SuggestPersonalAccount(Job.Owner); Field(network, credentials);
        destination.TextChanged += (_, _) => { if (NetworkAccess.IsNetworkPath(destination.Text.Trim())) credentials.SuggestPersonalAccount(Job.Owner); };
        network.Controls.Add(Button("Probar conexión de lectura y escritura", async (_, _) => { try { ReadFields(false); await NetworkAccess.Test(Job); MessageBox.Show(this, "El destino permite leer y escribir con la cuenta seleccionada. Espacio disponible informado: " + (await NetworkAccess.RunJob(Job, () => Task.FromResult(StoragePolicy.Available(Job.Destination))) / (1024d * 1024 * 1024)).ToString("N2") + " GB. El servidor puede informar la cuota individual o el disco completo.", "Conexión correcta"); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "No se pudo conectar"); } }));
        var schedule = Section("3  Días y horarios"); Field(schedule, scheduled); Label(schedule, "Días de la semana");
        var week = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        foreach (var pair in new[] { (DayOfWeek.Monday, "Lun"), (DayOfWeek.Tuesday, "Mar"), (DayOfWeek.Wednesday, "Mié"), (DayOfWeek.Thursday, "Jue"), (DayOfWeek.Friday, "Vie"), (DayOfWeek.Saturday, "Sáb"), (DayOfWeek.Sunday, "Dom") })
        { var check = new CheckBox { Text = pair.Item2, AutoSize = true, Checked = Scheduler.WeekDays(Job).Contains(pair.Item1) }; days.Add((pair.Item1, check)); week.Controls.Add(check); } schedule.Controls.Add(week);
        Label(schedule, "Horas del día (HH:mm), separadas por coma"); hours.PlaceholderText = "08:00, 12:30, 18:00"; Field(schedule, hours); Field(schedule, missed);
        Label(schedule, "El servicio se inicia con Windows y espera la sesión del usuario de esta tarea. No necesita abrir ProBackUp. Al iniciar sesión recupera el último horario vencido, sin acumular una copia por cada horario perdido. Usa la hora local del equipo.", true);
        Label(schedule, "Para copiar aplicaciones abiertas de forma consistente, exporta sus datos o ciérralas antes del respaldo.", true);
        var storage = Section("4  Almacenamiento");
        Label(storage, "Gestión de versiones incrementales");
        Label(storage, "Se consulta el espacio disponible con la cuenta del destino. Algunos servidores informan la cuota del usuario y otros el espacio del disco completo. Estas opciones se aplican al modo incremental.", true);
        Label(storage, "Conservar los últimos días (0 = sin vencimiento)"); Field(storage, retention); Field(storage, clean);
        Field(storage, manualLimit); Label(storage, "Límite del repositorio de esta tarea en GB (1 GB = 1024 MB)"); Field(storage, limit);
        Label(storage, "Siempre se conserva al menos la última versión verificada. La limpieza solo elimina datos propios de este repositorio que ninguna versión conservada utiliza. Si no hay espacio para los cambios, se detiene y avisa; nunca se borra la última versión para intentar una copia nueva. Se reserva un margen de 64 MB. Los respaldos completos no se depuran automáticamente.", true);
        retention.Value = Math.Min(retention.Maximum, Job.RetentionDays); clean.Checked = Job.CleanWhenFull; manualLimit.Checked = Job.StorageLimitBytes > 0; limit.Value = Math.Min(limit.Maximum, Job.StorageLimitBytes / (1024m * 1024 * 1024));
        void StorageEnabled() { bool enabled = kind.SelectedIndex == 2; retention.Enabled = clean.Enabled = manualLimit.Enabled = enabled; limit.Enabled = enabled && manualLimit.Checked; }
        kind.SelectedIndexChanged += (_, _) => StorageEnabled(); manualLimit.CheckedChanged += (_, _) => StorageEnabled();
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = Button("Guardar respaldo", async (_, _) => { try { ReadFields(true); BackupEngine.ValidateDefinition(Job); await Scheduler.Apply(Job); DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Revisa el respaldo", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }); save.Tag = "primary";
        var close = Button("Cancelar", (_, _) => DialogResult = DialogResult.Cancel); footer.Controls.Add(save); footer.Controls.Add(close); shell.Controls.Add(footer, 0, 3); Controls.Add(shell); CancelButton = close;
        name.Text = Job.Name; destination.Text = Job.Destination; foreach (var s in Job.Sources) sources.Items.Add(s); kind.SelectedIndex = Job.Incremental ? 2 : Job.Zip ? 0 : 1;
        scheduled.Checked = Job.Daily; hours.Text = string.Join(", ", Scheduler.Hours(Job)); missed.Checked = true; missed.Enabled = false;
        Appearance.Apply(this, dark);
    }
    private void ReadFields(bool validateSchedule)
    {
        if (Job.Owner.Length == 0) Job.Owner = WindowsIdentity.GetCurrent().Name;
        Job.Name = name.Text.Trim(); Job.Sources = sources.Items.Cast<string>().Select(NetworkAccess.Normalize).ToList(); Job.Destination = NetworkAccess.Normalize(destination.Text); Job.Zip = kind.SelectedIndex == 0; Job.Incremental = kind.SelectedIndex == 2;
        credentials.SuggestPersonalAccount(Job.Owner);
        Job.Network = credentials.ReadSettings();
        NetworkAccess.ValidateJob(Job);
        Job.Owner = Job.Network.User;
        Job.RetentionDays = (int)retention.Value; Job.CleanWhenFull = clean.Checked;
        if (manualLimit.Checked && limit.Value <= 0) throw new ArgumentException("Indica un límite mayor que cero o desactiva el límite manual.");
        Job.StorageLimitBytes = manualLimit.Checked ? (long)(limit.Value * 1024m * 1024 * 1024) : 0;
        Job.Daily = scheduled.Checked; Job.Days = days.Where(p => p.control.Checked).Select(p => p.day).ToList(); Job.Times = hours.Text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().Order().ToList();
        if (validateSchedule && Job.Daily && (Job.Days.Count == 0 || Job.Times.Count == 0)) throw new ArgumentException("Selecciona al menos un día y una hora.");
        Job.Time = Job.Times.FirstOrDefault() ?? "18:00"; Job.RunMissed = true;
        if (validateSchedule) Job.ScheduledFrom = DateTime.Now;
    }
}
