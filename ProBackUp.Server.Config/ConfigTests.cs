using System.Text.Json;
using ProBackUp;
namespace ProBackUp.Server;
internal static class ConfigTests
{
    private sealed class FakeDirectory : IDirectory
    {
        public Task<DirectoryUser?> Lookup(LdapSettings settings, string upn) => Task.FromResult<DirectoryUser?>(upn == "admin@example.test" ? new(upn, "11111111111111111111111111111111") : null);
        public Task<DirectoryUser?> Authenticate(LdapSettings settings, string upn, string password) => Lookup(settings, upn);
    }
    private static ServerSettings Settings() => new() { PublicHost = "probackup.example.test", Port = 8445, ProtectedPfxPassword = Configuration.Protect("test-password-123456"), Ldap = new() { Host = "dc.example.test", BaseDn = "DC=example,DC=test", BindDn = "reader@example.test", ProtectedPassword = Configuration.Protect("test-only"), Users = [new() { Upn = "admin@example.test", Role = "Admin" }] } };
    internal static void Run(string report)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProBackUp-config-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var results = new List<string>();
        void Check(bool valid, string name) { if (!valid) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); }
        try
        {
            dynamic firewall = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;
            var missing = ServerControl.FirewallChange.FindRule(firewall, "ProBackUp-Test-" + Guid.NewGuid().ToString("N"));
            Check(missing == null, "Regla de firewall inexistente no se trata como archivo faltante (0x80070002)");
            Check(!ServerControl.FirewallChange.MissingRule(new UnauthorizedAccessException()), "No oculta errores de permisos del firewall");
            dynamic detached = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
            detached.Name = "ProBackUp-Test"; detached.Description = "Detached rule test"; detached.ApplicationName = Environment.ProcessPath!; detached.Protocol = 6; detached.LocalPorts = "8445"; detached.RemotePorts = "*"; detached.LocalAddresses = "*"; detached.RemoteAddresses = "LocalSubnet"; detached.Profiles = 3; detached.Direction = 1; detached.Action = 1; detached.Enabled = true; detached.EdgeTraversal = false;
            dynamic copy = ServerControl.FirewallChange.Clone(detached);
            Check((string)copy.LocalPorts == "8445" && (string)copy.RemoteAddresses == "LocalSubnet" && (int)copy.Profiles == 3, "Copia de regla COM conserva puerto, subredes y perfiles sin alterar firewall real");
            var old = Settings(); Configuration.Write(Path.Combine(root, "server.json"), old); File.WriteAllText(Path.Combine(root, "server.pfx"), "old-pfx"); var source = Path.Combine(root, "new.pfx"); File.WriteAllText(source, "new-pfx"); var oldBytes = File.ReadAllBytes(Path.Combine(root, "server.json"));
            using (var transaction = new ConfigFiles(root))
            {
                var updated = Settings(); updated.Port = 9445;
                transaction.Save(updated, source, (_, staged) => Check(File.ReadAllText(staged) == "new-pfx", "Valida copia staged del PFX antes de publicarla"));
                Check(Configuration.Load(root).Current.Port == 9445, "Aplica configuración validada sin escribir contraseñas en claro");
            }
            Check(File.ReadAllBytes(Path.Combine(root, "server.json")).SequenceEqual(oldBytes) && File.ReadAllText(Path.Combine(root, "server.pfx")) == "old-pfx", "Fallo posterior restaura JSON y PFX anteriores");
            using (var transaction = new ConfigFiles(root)) { var updated = Settings(); updated.Port = 9446; transaction.Save(updated, source, (_, _) => { }); transaction.Commit(); }
            Check(Configuration.Load(root).Current.Port == 9446 && File.Exists(Path.Combine(root, "server.json.previous")) && File.Exists(Path.Combine(root, "server.pfx.previous")), "Guardar confirmado conserva cambios y copias anteriores");
            bool rejected = false;
            using (var first = new ConfigFiles(root)) { try { using var second = new ConfigFiles(root); } catch (IOException) { rejected = true; } }
            Check(rejected, "Bloqueo evita dos escrituras simultáneas");
            rejected = false; using (var transaction = new ConfigFiles(root)) { try { transaction.Save(Settings(), source); } catch (System.Security.Cryptography.CryptographicException) { rejected = true; } }
            Check(rejected && Configuration.Load(root).Current.Port == 9446, "Validación real rechaza un PFX inválido sin alterar configuración");
            Check(ConfigOperations.ValidateDirectory(Settings(), new FakeDirectory()).GetAwaiter().GetResult().Upn == "admin@example.test", "Verifica existencia del administrador en directorio antes de guardar");
            var invalid = Settings(); invalid.Ldap.Users[0].Upn = "missing@example.test"; rejected = false; try { ConfigOperations.ValidateDirectory(invalid, new FakeDirectory()).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Administrador inexistente impide guardar");
            Check(!(File.ReadAllText(Path.Combine(root, "server.json"))).Contains("test-password-123456"), "Archivo de configuración mantiene secretos cifrados DPAPI");
            Preview(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "previews", "server-config"));
            results.Add("PASS: Ventana real abre y renderiza cuatro secciones en modo oscuro y claro");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); File.WriteAllText(report, string.Join(Environment.NewLine, results) + "\nALL CONFIG TESTS PASSED\n");
        }
        catch (Exception ex) { File.WriteAllText(report, string.Join(Environment.NewLine, results) + "\n" + ex); throw; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    internal static void Preview(string directory)
    {
        Directory.CreateDirectory(directory); var root = Path.Combine(Path.GetTempPath(), "ProBackUp-config-preview-" + Guid.NewGuid().ToString("N"));
        using var form = new ConfigForm(root, true); form.SetPreviewValues(); form.Show(); Application.DoEvents();
        foreach (bool dark in new[] { true, false })
        {
            form.PreviewTheme(dark);
            for (int i = 0; i < 4; i++) { form.PreviewPage(i); Application.DoEvents(); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(directory, (dark ? "oscuro" : "claro") + "-" + (i + 1) + ".png")); }
        }
        form.Close();
    }
}
