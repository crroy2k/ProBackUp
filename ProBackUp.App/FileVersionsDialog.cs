namespace ProBackUp;
public sealed class FileVersionsDialog : Form
{
    private readonly ThemeComboBox versions = new();
    private readonly DataGridView grid = new();
    private readonly TextBox search = new() { PlaceholderText = "Buscar por nombre o ruta…" };
    private readonly TextBox extensions = new() { PlaceholderText = ".pdf, .docx, .xlsx" };
    private readonly Label count = new() { AutoSize = true, MaximumSize = new Size(850, 0), Tag = "muted" };
    private List<FileEntry> snapshotFiles = [];
    private Action? filterUpdated;
    private List<BackupRecord> records = [];
    private bool working;
    private readonly FileVersionSelection selection = new();
    private Action? selectionUpdated;
    internal bool Loading => working;
    internal string? SelectedArtifact => versions.SelectedIndex >= 0 ? records[versions.SelectedIndex].Artifact : null;
    internal string? SelectedFile => selection.Selected;
    internal bool CanRestore => !working && SelectedFile != null && VisibleFiles.Contains(SelectedFile, StringComparer.OrdinalIgnoreCase);
    internal List<string> VisibleFiles => grid.Rows.Cast<DataGridViewRow>().Select(r => r.Cells[0].Value!.ToString()!).ToList();
    internal void SetFilter(string query, string types) { search.Text = query; extensions.Text = types; }
    internal void SelectVersion(string id) => versions.SelectedIndex = records.FindIndex(r => r.Id == id);
    internal void SelectFile(string path)
    {
        selection.Choose(path); grid.ClearSelection(); grid.CurrentCell = null;
        foreach (DataGridViewRow row in grid.Rows) if (string.Equals(row.Cells[0].Value?.ToString(), path, StringComparison.OrdinalIgnoreCase)) { grid.CurrentCell = row.Cells[0]; row.Selected = true; break; }
        selectionUpdated?.Invoke();
    }
    public FileVersionsDialog(BackupRecord selected, AppSettings settings)
    {
        Text = "ProBackUp · Versiones de archivos por día"; Size = new Size(940, 700); MinimumSize = new Size(820, 600); StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var icon = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"); if (icon != null) Icon = new Icon(icon);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 }; shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) shell.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : SizeType.AutoSize, i == 6 ? 100 : 0));
        shell.Controls.Add(new Label { Text = "Versiones de «" + selected.JobName + "»", AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold) }, 0, 0);
        var date = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = selected.Started.LocalDateTime.Date, Width = 150 }; shell.Controls.Add(date, 0, 1);
        versions.Dock = DockStyle.Top; versions.Margin = new Padding(0, 12, 0, 12); shell.Controls.Add(versions, 0, 2);
        var info = new Label { AutoSize = true, MaximumSize = new Size(850, 0), Tag = "muted", Margin = new Padding(0, 0, 0, 12) }; shell.Controls.Add(info, 0, 3);
        grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.RowHeadersVisible = false; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false; grid.EnableHeadersVisualStyles = false;
        var filters = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.Controls.Add(new Label { Text = "Buscar archivo", AutoSize = true }, 0, 0); filters.Controls.Add(new Label { Text = "Extensiones (separadas por coma)", AutoSize = true }, 1, 0);
        search.Dock = extensions.Dock = DockStyle.Fill; search.Margin = new Padding(0, 4, 14, 0); extensions.Margin = new Padding(0, 4, 12, 0); filters.Controls.Add(search, 0, 1); filters.Controls.Add(extensions, 1, 1);
        var clear = new Button { Text = "Limpiar", AutoSize = true, Padding = new Padding(8, 2, 8, 2) }; clear.Click += (_, _) => SetFilter("", ""); filters.Controls.Add(clear, 2, 1); shell.Controls.Add(filters, 0, 4);
        count.Margin = new Padding(0, 0, 0, 10); shell.Controls.Add(count, 0, 5);
        grid.Columns.Add("path", "Archivo"); grid.Columns.Add("bytes", "Bytes"); grid.Columns.Add("hash", "Huella de la versión (SHA-256)"); grid.Columns[0].FillWeight = 50; grid.Columns[1].FillWeight = 15; grid.Columns[2].FillWeight = 35; shell.Controls.Add(grid, 0, 6);
        var restore = new Button { Text = "Restaurar esta versión del archivo", AutoSize = true, Tag = "primary", Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 15, 0, 0), Enabled = false }; shell.Controls.Add(restore, 0, 7); Controls.Add(shell);
        void UpdateCount()
        {
            int matches = grid.Rows.Count;
            count.Text = snapshotFiles.Count == 0 ? "Esta versión no contiene archivos." : matches == 0 ? $"Sin coincidencias · {snapshotFiles.Count} archivos en esta versión. Prueba otro texto o pulsa Limpiar." : $"{matches} de {snapshotFiles.Count} archivos · Los filtros se mantienen al cambiar de día u hora.";
            if (SelectedFile != null && !VisibleFiles.Contains(SelectedFile, StringComparer.OrdinalIgnoreCase)) count.Text += " El archivo seleccionado está oculto por los filtros.";
        }
        filterUpdated = () =>
        {
            grid.Rows.Clear(); var matches = BackupFileFilter.Apply(snapshotFiles, search.Text, extensions.Text);
            foreach (var f in matches) grid.Rows.Add(f.Path, f.Bytes, f.Sha256);
            if (selection.Wanted != null) SelectFile(selection.Wanted);
            UpdateCount();
            restore.Enabled = CanRestore; Appearance.Apply(this, settings.DarkMode);
        };
        search.TextChanged += (_, _) => filterUpdated(); extensions.TextChanged += (_, _) => filterUpdated();
        FormClosing += (_, e) => { if (working) e.Cancel = true; };
        NetworkSettings Credentials() => settings.Jobs.FirstOrDefault(j => j.Id == selected.JobId)?.Network ?? new();
        void RefreshVersions()
        {
            records = Store.History().Where(r => r.JobId == selected.JobId && r.Status == "Correcto" && r.Started.LocalDateTime.Date == date.Value.Date).ToList();
            versions.Items.Clear(); foreach (var r in records) versions.Items.Add($"{r.Started.LocalDateTime:HH:mm:ss.fff} · {BackupArchive.Label(BackupArchive.FromRecord(r))} · {r.Files} archivos · ID {r.Id[..Math.Min(6, r.Id.Length)]}");
            if (records.Count > 0) { int index = records.FindIndex(r => r.Id == selected.Id); versions.SelectedIndex = index >= 0 ? index : 0; }
            else { snapshotFiles = []; filterUpdated(); restore.Enabled = false; info.Text = "No hay versiones correctas para este día."; }
        }
        versions.SelectedIndexChanged += async (_, _) =>
        {
            working = true; restore.Enabled = false; grid.Rows.Clear(); snapshotFiles = []; selection.ClearSnapshot(); date.Enabled = versions.Enabled = search.Enabled = extensions.Enabled = clear.Enabled = false;
            try
            {
                if (versions.SelectedIndex < 0) return;
                var record = records[versions.SelectedIndex]; var access = Credentials();
                var backup = await Task.Run(() => NetworkAccess.Run(access, () => BackupArchive.Inspect(record.Artifact, default)));
                snapshotFiles = backup.Manifest.Files; selection.Load(snapshotFiles);
                filterUpdated();
                info.Text = $"Versión elegida: {record.Started.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} · {BackupArchive.Label(backup.Kind)}\r\n" + (selection.Wanted != null && SelectedFile == null ? "El archivo «" + selection.Wanted + "» no existe en esta versión. Selecciona otra hora o un archivo distinto." : "Selecciona el archivo que deseas recuperar de esta versión.");
                Appearance.Apply(this, settings.DarkMode);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "No se pudo leer la versión"); }
            finally { working = false; date.Enabled = versions.Enabled = search.Enabled = extensions.Enabled = clear.Enabled = true; restore.Enabled = CanRestore; }
        };
        selectionUpdated = () => { restore.Enabled = CanRestore; UpdateCount(); if (!working && SelectedFile is { } path && versions.SelectedIndex >= 0) info.Text = $"Versión elegida: {records[versions.SelectedIndex].Started.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}\r\nArchivo seleccionado: {path}"; };
        grid.CellClick += (_, e) => { if (!working && e.RowIndex >= 0 && grid.Rows[e.RowIndex].Cells[0].Value?.ToString() is { } path) SelectFile(path); };
        grid.KeyUp += (_, e) => { if (!working && e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown && grid.CurrentRow?.Cells[0].Value?.ToString() is { } path) SelectFile(path); };
        date.ValueChanged += (_, _) => RefreshVersions();
        restore.Click += async (_, _) =>
        {
            if (grid.CurrentRow == null || versions.SelectedIndex < 0) return;
            var record = records[versions.SelectedIndex]; var file = SelectedFile; if (file == null) return;
            using var target = new RestoreDialog(settings.DarkMode, record.Artifact, Credentials(), BackupArchive.FromRecord(record), file); if (target.ShowDialog(this) != DialogResult.OK) return;
            restore.Enabled = false; date.Enabled = versions.Enabled = search.Enabled = extensions.Enabled = clear.Enabled = false; working = true;
            try { var result = await Task.Run(() => RestoreOperations.Run(target.Request!, null, default)); MessageBox.Show(this, "Archivo recuperado en:\r\n" + result, "Versión restaurada"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "No se pudo restaurar"); }
            finally { working = false; date.Enabled = versions.Enabled = search.Enabled = extensions.Enabled = clear.Enabled = true; restore.Enabled = CanRestore; }
        };
        Appearance.Apply(this, settings.DarkMode); RefreshVersions();
    }
}

