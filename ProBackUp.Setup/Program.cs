using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ProBackUp.Setup;
internal static class Program
{
    public static readonly string InstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProBackUp");
    public const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ProBackUp";
    public static readonly string[] OwnedFiles = ["ProBackUp.exe", "Uninstall.exe", "LEEME.txt", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", ".probackup-install.json"];
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length == 3 && args[0] == "--self-test") { SelfTest(args[1], args[2]); return 0; }
            if (args.SequenceEqual(["--uninstall"])) { Uninstall(); return 0; }
            if (args.Length > 0) throw new ArgumentException("Argumentos no reconocidos.");
            Application.Run(new InstallerForm()); return 0;
        }
        catch (Exception ex)
        {
            if (args.Length >= 2 && args[0] == "--self-test") File.WriteAllText(args[1], ex.ToString());
            else MessageBox.Show(ex.Message, "Instalador ProBackUp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    public static void CheckPath(string target)
    {
        if (!Path.IsPathFullyQualified(target)) throw new IOException("Ruta de instalación inválida.");
        for (var p = Path.GetFullPath(target); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("La instalación no admite enlaces ni redirecciones: " + p);
    }
    public static void Install(string target, bool desktop, bool test = false, string? testInstaller = null)
    {
        CheckPath(target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !ValidMarker(target)) throw new IOException("La carpeta de destino contiene archivos de otra instalación. No se modificó.");
        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".ProBackUp-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool restart = false, preserveRecovery = false;
        Dictionary<string, (object value, RegistryValueKind kind)>? registration = null;
        if (!test) { using var existingRegistration = Registry.LocalMachine.OpenSubKey(RegistryPath); if (existingRegistration != null) registration = existingRegistration.GetValueNames().ToDictionary(n => n, n => (existingRegistration.GetValue(n)!, existingRegistration.GetValueKind(n))); }
        try
        {
            restart = !test && ServiceSetup.Stop(target);
            var assembly = Assembly.GetExecutingAssembly();
            using var payload = assembly.GetManifestResourceStream("payload.zip") ?? throw new IOException("Falta el contenido del instalador.");
            using var expectedReader = new StreamReader(assembly.GetManifestResourceStream("payload.sha256")!); var expected = expectedReader.ReadToEnd().Trim();
            var actual = Convert.ToHexString(SHA256.HashData(payload)); if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("El contenido del instalador no pasó la verificación SHA-256."); payload.Position = 0;
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read, true))
            {
                foreach (var entry in zip.Entries)
                {
                    if (!OwnedFiles.Contains(entry.FullName) || entry.FullName is "Uninstall.exe" or ".probackup-install.json") throw new IOException("Archivo inesperado en el instalador.");
                    entry.ExtractToFile(Path.Combine(stage, entry.FullName));
                }
            }
            if (!File.Exists(Path.Combine(stage, "ProBackUp.exe"))) throw new IOException("Falta ProBackUp.exe.");
            File.Copy(test ? testInstaller ?? throw new ArgumentException("Falta el instalador de prueba.") : Environment.ProcessPath!, Path.Combine(stage, "Uninstall.exe"));
            File.WriteAllText(Path.Combine(stage, ".probackup-install.json"), JsonSerializer.Serialize(new { product = "ProBackUp", version = "3.3.1", path = Path.GetFullPath(target) }));
            // Fail before touching an existing installation if a binary is in use.
            foreach (var name in OwnedFiles)
            {
                var path = Path.Combine(target, name);
                if (File.Exists(path)) { using var check = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            }
            Directory.CreateDirectory(target);
            // Keep exact previous owned files until registration succeeds.
            var previous = Path.Combine(stage, "previous"); Directory.CreateDirectory(previous);
            var installed = new List<string>();
            try
            {
                foreach (var name in OwnedFiles)
                {
                    var path = Path.Combine(target, name); if (File.Exists(path)) File.Move(path, Path.Combine(previous, name));
                    installed.Add(name); File.Move(Path.Combine(stage, name), path);
                }
                if (!test) { Register(target, desktop); ServiceSetup.Configure(target); }
            }
            catch
            {
                try
                {
                    if (!test) ServiceSetup.Stop(target);
                    foreach (var name in installed) { var path = Path.Combine(target, name); if (File.Exists(path)) File.Delete(path); var old = Path.Combine(previous, name); if (File.Exists(old)) File.Move(old, path); }
                    if (!test)
                    {
                        Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, false);
                        if (registration != null) { using var restored = Registry.LocalMachine.CreateSubKey(RegistryPath); foreach (var item in registration) restored.SetValue(item.Key, item.Value.value, item.Value.kind); }
                        else
                        {
                            foreach (var shortcut in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ProBackUp", "ProBackUp.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ProBackUp", "Desinstalar ProBackUp.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ProBackUp.lnk") }) if (File.Exists(shortcut)) File.Delete(shortcut);
                        }
                    }
                }
                catch (Exception recovery) { preserveRecovery = true; throw new IOException("No se pudo revertir la instalación. Se conservaron los archivos anteriores en " + previous, recovery); }
                throw;
            }
        }
        finally { if (!preserveRecovery && Directory.Exists(stage)) Directory.Delete(stage, true); if (restart) ServiceSetup.StartExisting(); }
    }
    private static bool ValidMarker(string target)
    {
        try { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, ".probackup-install.json"))); return doc.RootElement.GetProperty("product").GetString() == "ProBackUp" && string.Equals(doc.RootElement.GetProperty("path").GetString(), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }
    private static void Shortcut(string shortcut, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        var comType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows no permite crear accesos directos.");
        dynamic shell = Activator.CreateInstance(comType)!; dynamic link = shell.CreateShortcut(shortcut); link.TargetPath = target; link.WorkingDirectory = Path.GetDirectoryName(target); link.IconLocation = target + ",0"; link.Description = "ProBackUp · Respaldos y notificaciones por correo"; link.Save();
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
    }
    public static void Register(string target, bool desktop)
    {
        using var registry = Registry.LocalMachine.CreateSubKey(RegistryPath);
        registry.SetValue("DisplayName", "ProBackUp"); registry.SetValue("DisplayVersion", "3.3.1"); registry.SetValue("Publisher", "ProBackUp"); registry.SetValue("InstallLocation", target); registry.SetValue("DisplayIcon", Path.Combine(target, "ProBackUp.exe")); registry.SetValue("UninstallString", $"\"{Path.Combine(target, "Uninstall.exe")}\" --uninstall"); registry.SetValue("NoModify", 1, RegistryValueKind.DWord); registry.SetValue("NoRepair", 1, RegistryValueKind.DWord); registry.SetValue("EstimatedSize", (int)(OwnedFiles.Where(n => File.Exists(Path.Combine(target, n))).Sum(n => new FileInfo(Path.Combine(target, n)).Length) / 1024), RegistryValueKind.DWord);
        Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ProBackUp", "ProBackUp.lnk"), Path.Combine(target, "ProBackUp.exe"));
        Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ProBackUp", "Desinstalar ProBackUp.lnk"), Path.Combine(target, "Uninstall.exe"));
        // The uninstaller shortcut must carry its mode argument.
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!; dynamic shell = Activator.CreateInstance(shellType)!; dynamic link = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ProBackUp", "Desinstalar ProBackUp.lnk")); link.Arguments = "--uninstall"; link.Save(); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        if (desktop) Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ProBackUp.lnk"), Path.Combine(target, "ProBackUp.exe"));
    }
    private static void Uninstall()
    {
        var target = Path.GetDirectoryName(Environment.ProcessPath!)!; CheckPath(target);
        if (!target.Equals(InstallPath, StringComparison.OrdinalIgnoreCase) || !ValidMarker(target)) throw new IOException("Ejecuta el desinstalador desde la instalación de ProBackUp.");
        if (MessageBox.Show("¿Desinstalar ProBackUp? Se conservarán los respaldos, la configuración y el historial.", "ProBackUp", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        ServiceSetup.Stop(target);
        foreach (var name in OwnedFiles.Where(n => n != "Uninstall.exe")) { var p = Path.Combine(target, name); if (File.Exists(p)) { using var file = File.Open(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None); } }
        ServiceSetup.Remove(target);
        Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, false);
        var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ProBackUp");
        foreach (var p in new[] { Path.Combine(menu, "ProBackUp.lnk"), Path.Combine(menu, "Desinstalar ProBackUp.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ProBackUp.lnk") }) if (File.Exists(p)) File.Delete(p);
        if (Directory.Exists(menu) && !Directory.EnumerateFileSystemEntries(menu).Any()) Directory.Delete(menu);
        foreach (var n in OwnedFiles.Where(n => n != "Uninstall.exe")) { var p = Path.Combine(target, n); if (File.Exists(p)) File.Delete(p); }
        // Delete only the explicitly owned executable after this process exits; never recurse.
        string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; Remove-Item -LiteralPath {Quote(Path.Combine(target, "Uninstall.exe"))} -Force; if ((Test-Path -LiteralPath {Quote(target)}) -and -not (Get-ChildItem -LiteralPath {Quote(target)} -Force)) {{ Remove-Item -LiteralPath {Quote(target)} }}";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        Process.Start(start); MessageBox.Show("ProBackUp se desinstaló. Tus respaldos y datos personales se conservaron.", "ProBackUp");
    }
    private static void SelfTest(string report, string installer)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProBackUp-setup-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var target = Path.Combine(root, "installed"); var results = new List<string>();
        try
        {
            Install(target, false, true, installer); if (!ValidMarker(target) || !OwnedFiles.All(n => File.Exists(Path.Combine(target, n)))) throw new Exception("Instalación incompleta."); results.Add("PASS: Extrae payload verificado, aplicación, licencia y desinstalador");
            using (var original = File.OpenRead(installer)) using (var copied = File.OpenRead(Path.Combine(target, "Uninstall.exe"))) if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(copied))) throw new Exception("El desinstalador no coincide con el instalador autónomo.");
            Install(target, false, true, installer); results.Add("PASS: Actualización idempotente de instalación propia");
            var app = new ProcessStartInfo(Path.Combine(target, "ProBackUp.exe")) { UseShellExecute = false, CreateNoWindow = true }; app.ArgumentList.Add("--self-test"); app.ArgumentList.Add(Path.Combine(root, "application-tests.txt")); using var process = Process.Start(app)!; process.WaitForExit(); if (process.ExitCode != 0) throw new Exception(File.ReadAllText(Path.Combine(root, "application-tests.txt"))); results.Add("PASS: Aplicación instalada ejecuta pruebas completas");
            var outsider = Path.Combine(root, "foreign"); Directory.CreateDirectory(outsider); File.WriteAllText(Path.Combine(outsider, "keep.txt"), "keep"); bool rejected = false; try { Install(outsider, false, true, installer); } catch (IOException) { rejected = true; } if (!rejected || File.ReadAllText(Path.Combine(outsider, "keep.txt")) != "keep") throw new Exception("Se alteró una carpeta ajena."); results.Add("PASS: Rechaza sobrescribir carpetas ajenas");
            var shortcut = Path.Combine(root, "ProBackUp.lnk"); Shortcut(shortcut, Path.Combine(target, "ProBackUp.exe")); if (!File.Exists(shortcut)) throw new Exception("No se creó el acceso directo."); results.Add("PASS: Creación de acceso directo de Windows");
            results.Add(File.ReadAllText(Path.Combine(root, "application-tests.txt"))); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); File.WriteAllText(report, string.Join(Environment.NewLine, results));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

internal sealed class InstallerForm : Form
{
    private readonly Button install = new() { Text = "Instalar ProBackUp", AutoSize = true, BackColor = Color.FromArgb(0, 112, 103), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(14, 10, 14, 10) };
    private readonly CheckBox desktop = new() { Text = "Crear un acceso directo en el escritorio", Checked = true, AutoSize = true };
    private readonly CheckBox launch = new() { Text = "Abrir ProBackUp al terminar", Checked = true, AutoSize = true };
    private readonly Label status = new() { Text = "Instalación para el equipo · Requiere administrador · Servicio automático", AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DimGray };
    private bool busy;
    public InstallerForm()
    {
        Text = "Instalar ProBackUp 3.3"; Size = new Size(740, 610); MinimumSize = new Size(700, 610); StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.White; AutoScaleMode = AutoScaleMode.Dpi;
        var iconResource = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"); if (iconResource != null) Icon = new Icon(iconResource);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32), ColumnCount = 1, RowCount = 10 }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); for (int i = 0; i < 10; i++) table.RowStyles.Add(new RowStyle(i == 8 ? SizeType.Percent : SizeType.AutoSize, i == 8 ? 100 : 0));
        var brand = new Panel { Dock = DockStyle.Top, Height = 82 }; var logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
        if (logoStream != null) brand.Controls.Add(new PictureBox { Image = Image.FromStream(logoStream), SizeMode = PictureBoxSizeMode.Zoom, Width = 76, Dock = DockStyle.Left });
        brand.Controls.Add(new Label { Text = "ProBackUp", Font = new Font("Segoe UI", 30, FontStyle.Bold), ForeColor = Color.FromArgb(22, 37, 61), AutoSize = true, Location = new Point(92, 12) }); table.Controls.Add(brand, 0, 0);
        table.Controls.Add(new Label { Text = "Protege tus archivos. Mantente informado.", Font = new Font("Segoe UI", 14), AutoSize = true, Margin = new Padding(0, 0, 0, 24) }, 0, 1);
        table.Controls.Add(new Label { Text = "• Respaldos completos, incrementales y versiones por día\r\n• Servicio automático con días y múltiples horarios\r\n• Historial y restauración con verificación SHA-256\r\n• Notificaciones SMTP con puertos y TLS configurables", AutoSize = true, Margin = new Padding(0, 0, 0, 22) }, 0, 2);
        table.Controls.Add(new Label { Text = "Destino: " + Program.InstallPath, AutoSize = true, MaximumSize = new Size(630, 0), Margin = new Padding(0, 0, 0, 16) }, 0, 3); table.Controls.Add(desktop, 0, 4); table.Controls.Add(launch, 0, 5);
        table.Controls.Add(new Label { Text = "Windows 10/11 de 64 bits · Incluye el entorno de ejecución.\r\nInstala e inicia ProBackUpService automáticamente. La configuración queda protegida para administradores y el servicio.", AutoSize = true, MaximumSize = new Size(630, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 18, 0, 12) }, 0, 6); table.Controls.Add(status, 0, 7); table.Controls.Add(install, 0, 9); Controls.Add(table);
        install.Click += async (_, _) =>
        {
            busy = true; install.Enabled = false; desktop.Enabled = false; launch.Enabled = false; status.Text = "Verificando e instalando…";
            try { bool shortcut = desktop.Checked; await Task.Run(() => Program.Install(Program.InstallPath, shortcut)); status.Text = "Instalación completada"; if (launch.Checked) Process.Start(new ProcessStartInfo(Path.Combine(Program.InstallPath, "ProBackUp.exe")) { UseShellExecute = true }); busy = false; Close(); }
            catch (Exception ex) { busy = false; status.Text = "No se completó la instalación"; MessageBox.Show(this, ex.Message + "\r\nCierra ProBackUp antes de actualizar.", "ProBackUp", MessageBoxButtons.OK, MessageBoxIcon.Error); install.Enabled = true; desktop.Enabled = true; launch.Enabled = true; }
        };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        Appearance.MarkMuted(this); Appearance.Apply(this, true);
    }
}






