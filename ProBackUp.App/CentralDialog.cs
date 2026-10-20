namespace ProBackUp;
internal sealed class CentralDialog : Form
{
    internal CentralDialog(bool dark)
    {
        var original = Store.Load().Central;
        Text = "ProBackUp · Servidor central"; Size = new Size(720, 510); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, AutoScroll = true }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control control) { control.Dock = DockStyle.Top; control.Margin = new Padding(0, 0, 0, 12); table.Controls.Add(control); }
        Add(new Label { Text = "Supervisión centralizada", AutoSize = true, Font = new Font("Segoe UI", 19, FontStyle.Bold) });
        var enabled = new CheckBox { Text = "Conectar al servidor (reportes y órdenes de respaldo)", Checked = original.Enabled, AutoSize = true }; Add(enabled);
        Add(new Label { Text = "Dirección HTTPS del servidor", AutoSize = true }); var url = new TextBox { Text = original.Url, PlaceholderText = "https://probackup.empresa.local:8443/" }; Add(url);
        Add(new Label { Text = "Clave individual del equipo (vacío conserva la actual)", AutoSize = true }); var key = new TextBox { UseSystemPasswordChar = true, MaxLength = 64 }; Add(key);
        Add(new Label { Text = "T.I. genera la clave desde el panel central. El certificado HTTPS debe ser confiable para Windows. Se envían resultados, destinos y cantidades; los archivos permanecen en su destino. El servicio reintenta los reportes pendientes y recibe órdenes para ejecutar las tareas locales. Debe estar activo para recibir solicitudes del servidor.", AutoSize = true, MaximumSize = new Size(640, 0), Tag = "muted" });
        var state = new Label { Text = "", AutoSize = true, MaximumSize = new Size(640, 0) }; Add(state);
        try { if (File.Exists(Path.Combine(Store.Root, "central-status.json"))) { using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Store.Root, "central-status.json"))); state.Text = doc.RootElement.GetProperty("State").GetString(); } } catch { }
        CentralSettings Read()
        {
            if (key.Text.Length == 0 && original.EncryptedToken.Length > 0 && !url.Text.Trim().TrimEnd('/').Equals(original.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Introduce una nueva clave al cambiar de servidor.");
            var settings = new CentralSettings { Enabled = enabled.Checked, Url = url.Text.Trim(), EncryptedToken = key.Text.Length > 0 ? Secrets.Protect(key.Text.Trim()) : original.EncryptedToken };
            if (settings.Enabled) settings.Validate(); return settings;
        }
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var save = new Button { Text = "Guardar", AutoSize = true, Tag = "primary", Padding = new Padding(10, 6, 10, 6) };
        var test = new Button { Text = "Probar conexión y enviar pendientes", AutoSize = true, Padding = new Padding(10, 6, 10, 6) };
        save.Click += (_, _) => { try { var central = Read(); using var gate = Store.Acquire(); var settings = Store.Load(); settings.Central = central; Store.Save(settings); DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Servidor central"); } };
        bool working = false;
        test.Click += async (_, _) => { working = true; save.Enabled = test.Enabled = false; try { await CentralReporter.Flush(Read(), CancellationToken.None); state.Text = "Conexión confirmada por el servidor."; } catch (Exception ex) { state.Text = ex is IOException or ArgumentException ? ex.Message : "No se pudo conectar. Revise DNS, certificado y clave. Llame a T.I. Ext. 233."; } finally { working = false; save.Enabled = test.Enabled = true; } };
        FormClosing += (_, e) => { if (working) e.Cancel = true; }; buttons.Controls.Add(save); buttons.Controls.Add(test); Add(buttons); Controls.Add(table); Appearance.Apply(this, dark);
    }
}
