namespace ProBackUp;

internal sealed class SystemTray : IDisposable
{
    private readonly MainForm window;
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem service = new() { Enabled = false };
    private readonly ToolStripMenuItem backups = new("Respaldar ahora");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private readonly HashSet<string> notified = [];
    private bool hiddenHint, disposed;
    private readonly Queue<BackupRecord> pending = new();
    internal ResultNotification? CurrentNotification { get; private set; }
    internal int PendingNotifications => pending.Count;
    private static bool Finished(BackupRecord r) => r.Status is "Correcto" or "Error" or "Cancelado";
    internal bool Visible => icon.Visible;

    public SystemTray(MainForm window)
    {
        this.window = window;
        icon = new NotifyIcon { Icon = window.Icon, Text = "ProBackUp", ContextMenuStrip = menu, Visible = true };
        menu.Items.Add("Abrir ProBackUp", null, (_, _) => window.OpenFromTray());
        menu.Items.Add("Ver historial", null, (_, _) => window.OpenFromTray(true));
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(service); menu.Items.Add(backups);
        menu.Items.Add("Actualizar estado", null, (_, _) => Poll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir del icono (el servicio continúa)", null, (_, _) => window.ExitFromTray());
        menu.Opening += (_, _) => RefreshMenu();
        icon.DoubleClick += (_, _) => window.OpenFromTray();
        icon.BalloonTipClicked += (_, _) => window.OpenFromTray(true);
        try { foreach (var record in Store.History().Where(r => Finished(r))) notified.Add(record.Id); } catch { }
        timer.Tick += (_, _) => Poll(); timer.Start(); Poll();
    }
    private void RefreshMenu()
    {
        service.Text = Scheduler.Status();
        while (backups.DropDownItems.Count > 0) { var old = backups.DropDownItems[0]; backups.DropDownItems.RemoveAt(0); old.Dispose(); }
        backups.Enabled = !window.OperationRunning;
        try
        {
            var jobs = Store.Load().Jobs;
            if (jobs.Count == 0) backups.DropDownItems.Add(new ToolStripMenuItem("No hay tareas configuradas") { Enabled = false });
            foreach (var job in jobs)
            {
                string id = job.Id;
                var item = new ToolStripMenuItem(job.Name) { ToolTipText = job.Destination };
                item.Click += async (_, _) => await window.RunFromTray(id);
                backups.DropDownItems.Add(item);
            }
        }
        catch { backups.Enabled = false; }
    }
    private void Poll()
    {
        try
        {
            string state = window.OperationRunning ? "Operación en curso" : Scheduler.Status();
            string text = "ProBackUp · " + state; icon.Text = text[..Math.Min(63, text.Length)]; service.Text = state;
            var finished = Store.History().Where(r => Finished(r) && notified.Add(r.Id)).ToList();
            if (finished.Count == 0) return;
            foreach (var record in finished.OrderBy(r => r.Finished)) pending.Enqueue(record);
            ShowNext();
        }
        catch { icon.Text = "ProBackUp · No se pudo consultar el estado"; }
    }
    private void ShowNext()
    {
        if (disposed || CurrentNotification != null || pending.Count == 0) return;
        var record = pending.Dequeue(); var popup = new ResultNotification(record, () => window.OpenFromTray(true));
        CurrentNotification = popup;
        popup.FormClosed += (_, _) =>
        {
            CurrentNotification = null;
            if (!disposed && !window.IsDisposed && window.IsHandleCreated) window.BeginInvoke((Action)ShowNext);
        };
        popup.Show();
        Notify((popup.Success ? "✅ " : "❌ ") + popup.Heading, popup.Success ? record.JobName + " · Respaldo completado correctamente. Pulse para ver el historial." : record.JobName + " · " + ResultNotification.Support + ". Pulse para ver el historial.", popup.Success ? ToolTipIcon.Info : ToolTipIcon.Error);
    }
    public void HiddenHint()
    {
        if (hiddenHint) return; hiddenHint = true;
        Notify("ProBackUp sigue disponible", "Abre ProBackUp desde este icono. El servicio mantiene la programación activa.", ToolTipIcon.Info);
    }
    public void Notify(string title, string text, ToolTipIcon type)
    {
        icon.BalloonTipTitle = title.Length > 63 ? title[..63] : title;
        icon.BalloonTipText = text; icon.BalloonTipIcon = type; icon.ShowBalloonTip(5000);
    }
    public void CheckNow() => Poll();
    public void Dispose() { if (disposed) return; disposed = true; pending.Clear(); CurrentNotification?.Close(); timer.Stop(); timer.Dispose(); icon.Visible = false; icon.Dispose(); menu.Dispose(); }
}
