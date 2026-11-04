using System.Diagnostics;
using ProBackUp.Security;

namespace ProBackUp.Setup;
internal sealed class UpdateForm : Form
{
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(540, 0), Text = "Buscando una versión nueva…" };
    private readonly Button cancel = new() { Text = "Cancelar", AutoSize = true };
    private readonly CancellationTokenSource stop = new();
    private bool busy;
    internal UpdateForm()
    {
        Text = "Actualizar ProBackUp";
        ClientSize = new Size(620, 220);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 11);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), FlowDirection = FlowDirection.TopDown };
        layout.Controls.Add(new Label { Text = "Actualización protegida", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 20) });
        layout.Controls.Add(status); layout.Controls.Add(cancel); Controls.Add(layout);
        cancel.Click += (_, _) => { if (busy) stop.Cancel(); else Close(); };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; stop.Cancel(); } };
        Shown += async (_, _) => await DownloadUpdate();
        Appearance.MarkMuted(this); Appearance.Apply(this, true);
    }
    private async Task DownloadUpdate()
    {
        busy = true;
        bool launched = false;
        try
        {
            WindowsFileSecurity.RequireAdministrator();
            if (!string.Equals(Environment.ProcessPath, Path.Combine(Program.InstallPath, "Uninstall.exe"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Abre las actualizaciones desde el cliente instalado.");
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProBackUpUpdates");
            WindowsFileSecurity.SecureTree(cache, []);
            using var client = ClientUpdates.Client();
            var release = await ClientUpdates.Latest(client, stop.Token);
            if (release == null || release.Version <= ClientUpdates.Current) { status.Text = "ProBackUp ya está actualizado."; return; }
            var progress = new Progress<string>(text => status.Text = text);
            var installer = await ClientUpdates.Download(client, release, cache, progress, stop.Token);
            // Both the package and its ancestors remain in an administrator-only tree.
            WindowsFileSecurity.SecureTree(cache, []);
            stop.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer) };
            start.ArgumentList.Add("--wait-for-updater"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            using var process = Process.Start(start) ?? throw new IOException("No se pudo abrir el instalador verificado.");
            launched = true;
        }
        catch (OperationCanceledException) { status.Text = "Actualización cancelada."; }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; cancel.Text = "Cerrar"; }
        if (launched) Close();
    }
    protected override void Dispose(bool disposing) { if (disposing) stop.Dispose(); base.Dispose(disposing); }
}
