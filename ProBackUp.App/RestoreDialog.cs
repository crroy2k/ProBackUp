namespace ProBackUp;
public sealed record RestoreRequest(string Artifact, string Destination, NetworkSettings Network, string? File);
public static class RestoreOperations
{
    public static Task<string> Run(RestoreRequest request, IProgress<string>? progress, CancellationToken token) => NetworkAccess.Run(request.Network, () => request.File == null ? BackupEngine.Restore(request.Artifact, request.Destination, progress, token) : IncrementalStore.RestoreFile(request.Artifact, request.File, request.Destination, token));
}
public sealed class RestoreDialog : Form
{
    public RestoreRequest? Request { get; private set; }
    private readonly TextBox source = new(), destination = new();
    private readonly ThemeComboBox kind = new();
    private readonly NetworkCredentialsEditor credentials = new(false);
    private readonly DataGridView files = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false };
    private readonly Label summary = new() { AutoSize = true, Tag = "muted", MaximumSize = new Size(740, 0) };
    private readonly RadioButton all = new() { Text = "Restaurar todos los archivos", AutoSize = true, Checked = true }, one = new() { Text = "Restaurar el archivo seleccionado", AutoSize = true };
    private readonly Button restore = new() { Text = "Restaurar", AutoSize = true, Padding = new Padding(14, 8, 14, 8), Tag = "primary", Enabled = false };
    private BackupInfo? loaded;
    private readonly FileVersionSelection selection = new();
    private bool working;
    internal bool Loading => working;
    internal BackupKind? DetectedKind => loaded?.Kind;
    internal string? SelectedFile => selection.Selected;
    internal bool CanRestore => restore.Enabled;
    public RestoreDialog(bool dark, string artifact = "", NetworkSettings? network = null, BackupKind initialKind = BackupKind.FullZip, string? selectedFile = null)
    {
        Text = "ProBackUp · Restaurar respaldo"; Size = new Size(900, 850); MinimumSize = new Size(840, 760); StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var icon = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"); if (icon != null) Icon = new Icon(icon);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 }; shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(new Label { Text = "Recupera tus archivos", Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 14) }, 0, 0);
        var tabs = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0) }; center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); center.RowStyles.Add(new RowStyle(SizeType.AutoSize)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); center.Controls.Add(tabs, 0, 0); shell.Controls.Add(center, 0, 1);
        var content = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }; center.Controls.Add(content, 0, 1);
        var pages = new List<Panel>(); var navigation = new List<Button>();
        TableLayoutPanel Page(string title)
        {
            int index = pages.Count; var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16), Tag = "surface", Visible = index == 0 }; pages.Add(page); content.Controls.Add(page);
            var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1 }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); page.Controls.Add(table);
            var b = new Button { Text = title, AutoSize = true, Padding = new Padding(12, 8, 12, 8), Tag = index == 0 ? "primary" : "secondary" }; tabs.Controls.Add(b); navigation.Add(b);
            b.Click += (_, _) => { for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; navigation[i].Tag = i == index ? "primary" : "secondary"; } pages[index].BringToFront(); Appearance.Apply(this, dark); }; return table;
        }
        void Label(TableLayoutPanel t, string text, bool hint = false) => t.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(740, 0), Tag = hint ? "muted" : "", Margin = new Padding(0, 8, 0, 8) });
        void Field(TableLayoutPanel t, Control c) { c.Dock = DockStyle.Top; c.Margin = new Padding(0, 0, 0, 10); t.Controls.Add(c); }
        var backupPage = Page("1  Respaldo y acceso"); Label(backupPage, "Tipo de respaldo (se comprobará al leerlo)"); kind.Items.AddRange(BackupArchive.Labels); kind.SelectedIndex = (int)initialKind; Field(backupPage, kind);
        Label(backupPage, "Archivo o carpeta del respaldo"); source.PlaceholderText = "ZIP, carpeta de una versión completa o archivo .pbi de una versión incremental"; Field(backupPage, source);
        var browse = new Button { Text = "Examinar respaldo…", AutoSize = true, Padding = new Padding(10, 6, 10, 6) };
        browse.Click += (_, _) =>
        {
            if (kind.SelectedIndex == (int)BackupKind.FullFolder) { using var folder = new FolderBrowserDialog { Description = "Selecciona la carpeta de una versión que contiene data y probackup-manifest.json" }; if (folder.ShowDialog(this) == DialogResult.OK) source.Text = NetworkAccess.Normalize(folder.SelectedPath); }
            else { using var file = new OpenFileDialog { Filter = "Respaldos ProBackUp (*.zip;*.pbi)|*.zip;*.pbi|Todos los archivos|*.*" }; if (file.ShowDialog(this) == DialogResult.OK) source.Text = NetworkAccess.Normalize(file.FileName); }
        }; backupPage.Controls.Add(browse);
        Label(backupPage, "Acceso a carpetas de red del respaldo y del destino", true); credentials.LoadSettings(network ?? new()); Field(backupPage, credentials);
        var read = new Button { Text = "Leer respaldo y mostrar archivos", AutoSize = true, Padding = new Padding(10, 6, 10, 6), Tag = "primary" }; backupPage.Controls.Add(read);
        var filesPage = Page("2  Archivos y destino"); Field(filesPage, summary);
        files.Columns.Add("path", "Archivo"); files.Columns.Add("bytes", "Bytes"); files.Columns[0].FillWeight = 80; files.Columns[1].FillWeight = 20; files.Height = 235; Field(filesPage, files);
        var choice = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top }; choice.Controls.Add(all); choice.Controls.Add(one); filesPage.Controls.Add(choice);
        Label(filesPage, "Carpeta de destino para los archivos recuperados"); destination.PlaceholderText = @"D:\Recuperados o \\servidor\compartida\Recuperados"; Field(filesPage, destination);
        var target = new Button { Text = "Examinar destino…", AutoSize = true, Padding = new Padding(10, 6, 10, 6) }; target.Click += (_, _) => { using var folder = new FolderBrowserDialog(); if (folder.ShowDialog(this) == DialogResult.OK) destination.Text = NetworkAccess.Normalize(folder.SelectedPath); }; filesPage.Controls.Add(target);
        Label(filesPage, "Se crea una carpeta nueva y se verifican los datos. No se sobrescriben los originales. Para un incremental debes conservar todo el repositorio con sus objetos; el archivo .pbi no contiene por sí solo los datos.", true);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 14, 0, 0) }; var cancel = new Button { Text = "Cancelar", AutoSize = true, Padding = new Padding(12, 8, 12, 8), DialogResult = DialogResult.Cancel }; footer.Controls.Add(restore); footer.Controls.Add(cancel); shell.Controls.Add(footer, 0, 2); Controls.Add(shell); CancelButton = cancel;
        source.TextChanged += (_, _) => { loaded = null; selection.ClearSnapshot(); files.Rows.Clear(); restore.Enabled = false; summary.Text = "Lee el respaldo para ver sus archivos y su tipo real."; };
        kind.SelectedIndexChanged += (_, _) => { if (loaded != null && kind.SelectedIndex != (int)loaded.Kind) { loaded = null; selection.ClearSnapshot(); files.Rows.Clear(); restore.Enabled = false; summary.Text = "Vuelve a leer el respaldo para comprobar su tipo y sus archivos."; } };
        read.Click += async (_, _) =>
        {
            try
            {
                var path = NetworkAccess.Normalize(source.Text); var access = credentials.ReadSettings(); working = true; read.Enabled = false; tabs.Enabled = false; backupPage.Enabled = false;
                var info = await Task.Run(() => NetworkAccess.Run(access, () => BackupArchive.Inspect(path, default)));
                source.Text = path; loaded = null; kind.SelectedIndex = (int)info.Kind; loaded = info; files.Rows.Clear();
                selection.Load(info.Manifest.Files); if (selectedFile != null) selection.Choose(selectedFile);
                foreach (var f in info.Manifest.Files) files.Rows.Add(f.Path, f.Bytes);
                if (selectedFile != null) { files.ClearSelection(); files.CurrentCell = null; foreach (DataGridViewRow row in files.Rows) if (row.Cells[0].Value?.ToString() == selectedFile) { files.CurrentCell = row.Cells[0]; row.Selected = true; } }
                if (selectedFile != null) { one.Checked = true; if (!info.Manifest.Files.Any(f => f.Path == selectedFile)) { files.ClearSelection(); files.CurrentCell = null; } }
                var dateText = info.Manifest.Created == default ? "Fecha no registrada en este respaldo antiguo" : info.Manifest.Created.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
                summary.Text = $"{BackupArchive.Label(info.Kind)}\r\n{info.Manifest.JobName} · {info.Manifest.Files.Count} archivos · Creado: {dateText}";
                if (one.Checked && selection.Selected == null) summary.Text += "\r\nEl archivo solicitado no existe en esta versión. Selecciona otra versión o un archivo distinto.";
                restore.Enabled = all.Checked || selection.Selected != null; tabs.Enabled = true; navigation[1].PerformClick(); Appearance.Apply(this, dark);
            }
            catch (Exception ex) { loaded = null; restore.Enabled = false; MessageBox.Show(this, ex.Message, "No se pudo leer el respaldo", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { working = false; read.Enabled = true; tabs.Enabled = true; backupPage.Enabled = true; }
        };
        void ChooseFile(string path) { selection.Choose(path); restore.Enabled = loaded != null && (all.Checked || selection.Selected != null); }
        files.CellClick += (_, e) => { if (e.RowIndex >= 0 && files.Rows[e.RowIndex].Cells[0].Value?.ToString() is { } path) ChooseFile(path); };
        files.KeyUp += (_, e) => { if (e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown && files.CurrentRow?.Cells[0].Value?.ToString() is { } path) ChooseFile(path); };
        all.CheckedChanged += (_, _) => restore.Enabled = loaded != null && (all.Checked || selection.Selected != null);
        restore.Click += (_, _) =>
        {
            try
            {
                if (loaded == null) throw new InvalidOperationException("Lee primero el respaldo.");
                string output = NetworkAccess.Normalize(destination.Text); if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Elige una carpeta de destino absoluta o UNC.");
                string? file = one.Checked ? selection.Selected ?? throw new ArgumentException("El archivo no existe en esta versión. Selecciona otro archivo o restaura todos.") : null;
                Request = new(source.Text, output, credentials.ReadSettings(), file); DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Revisa la restauración"); }
        };
        FormClosing += (_, e) => { if (working) e.Cancel = true; };
        source.Text = artifact; Appearance.Apply(this, dark);
        if (artifact.Length > 0) Shown += (_, _) => read.PerformClick();
    }
}
