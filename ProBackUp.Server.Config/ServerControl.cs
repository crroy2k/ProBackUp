using System.Diagnostics;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace ProBackUp.Server;
internal static class ServerControl
{
    internal const string ServiceName = "ProBackUpServer";
    internal static string? Binary()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        if (key == null) return null;
        var command = key.GetValue("ImagePath") as string ?? "";
        if (command.Length < 3 || command[0] != '"' || command[^1] != '"' || command[1..^1].Contains('"')) throw new IOException("La ruta del servicio no pertenece a una instalación compatible. No se modificó.");
        var path = Path.GetFullPath(command[1..^1]); var directory = Path.GetDirectoryName(path)!; Configuration.NoLinks(path);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
        if (!path.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != "ProBackUp.Server.exe" || !File.Exists(path) || !File.Exists(Path.Combine(directory, ".probackup-server")) || File.ReadAllText(Path.Combine(directory, ".probackup-server")).Trim() != "ProBackUpServer")
            throw new IOException("El servicio no coincide con una instalación propia de ProBackUp Server.");
        return path;
    }
    internal static string Status()
    {
        try { if (Binary() == null) return "No instalado"; using var service = new ServiceController(ServiceName); return service.Status switch { ServiceControllerStatus.Running => "En ejecución", ServiceControllerStatus.Stopped => "Detenido", _ => service.Status.ToString() }; }
        catch (Exception ex) { return "No disponible · " + ex.Message; }
    }
    internal static bool Stop()
    {
        if (Binary() == null) return false;
        using var service = new ServiceController(ServiceName); bool running = service.Status != ServiceControllerStatus.Stopped;
        if (running) { if (service.Status != ServiceControllerStatus.StopPending) service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); } return running;
    }
    internal static void Start()
    {
        if (File.Exists(Path.Combine(Configuration.DefaultRoot, "iis-hosting.json"))) throw new IOException("El servidor se aloja en IIS. No se inicia una segunda instancia como servicio.");
        if (Binary() == null) throw new IOException("Primero instala el servicio.");
        using var service = new ServiceController(ServiceName); if (service.Status != ServiceControllerStatus.Running) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
    }
    internal static async Task Install()
    {
        if (Binary() != null) return;
        var script = Path.Combine(AppContext.BaseDirectory, "Install-ProBackUpServer.ps1"); Configuration.NoLinks(script);
        if (!File.Exists(script) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "ProBackUp.Server.exe"))) throw new IOException("Coloca el configurador junto a ProBackUp.Server.exe y su instalador, dentro de la carpeta server.");
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-PrepareOnly" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("No se pudo preparar el servicio.\n" + await output + "\n" + await error);
        if (Binary() == null) throw new IOException("No se encontró el servicio tras instalarlo.");
    }
    internal static void PrepareRuntime()
    {
        Configuration.SecureDirectory(Configuration.DefaultRoot);
        if (!EventLog.SourceExists("ProBackUp.Server")) EventLog.CreateEventSource("ProBackUp.Server", "Application");
    }
    internal sealed class FirewallChange : IDisposable
    {
        private dynamic? policy;
        private dynamic? original;
        private bool changed, committed;
        private const string RuleName = "ProBackUpServer-HTTPS";
        internal FirewallChange(string binary, int port)
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;
            dynamic? existing = FindRule(policy!, RuleName) ?? FindRule(policy!, "ProBackUp Server HTTPS (red interna)");
            string? existingName = existing == null ? null : (string)existing.Name;
            if (existing != null)
            {
                if (!string.Equals((string)existing.ApplicationName, binary, StringComparison.OrdinalIgnoreCase)) throw new IOException("La regla de firewall existente pertenece a otra ruta. No se modificó.");
                original = Clone(existing);
            }
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
            rule.Name = RuleName; rule.Description = "ProBackUp Server HTTPS · red interna"; rule.ApplicationName = binary; rule.Protocol = 6; rule.LocalPorts = port.ToString(); rule.RemoteAddresses = original == null ? "LocalSubnet" : original.RemoteAddresses;
            rule.Profiles = 3; rule.Direction = 1; rule.Action = 1; rule.Enabled = true; rule.EdgeTraversal = false;
            try { if (existing != null) policy!.Rules.Remove(existingName); changed = true; policy!.Rules.Add(rule); }
            catch { Dispose(); throw; }
        }
        internal static bool MissingRule(Exception ex) => ex.HResult == unchecked((int)0x80070002);
        internal static dynamic? FindRule(dynamic firewall, string name)
        {
            try { return firewall.Rules.Item(name); }
            catch (Exception ex) when (MissingRule(ex)) { return null; }
        }
        internal static dynamic Clone(dynamic value)
        {
            dynamic copy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
            copy.Name = value.Name; copy.Description = value.Description; copy.ApplicationName = value.ApplicationName; copy.Protocol = value.Protocol; copy.LocalPorts = value.LocalPorts; copy.RemotePorts = value.RemotePorts; copy.LocalAddresses = value.LocalAddresses; copy.RemoteAddresses = value.RemoteAddresses;
            copy.Profiles = value.Profiles; copy.Direction = value.Direction; copy.Action = value.Action; copy.Enabled = value.Enabled; copy.EdgeTraversal = value.EdgeTraversal; return copy;
        }
        internal void Commit() => committed = true;
        public void Dispose()
        {
            if (changed && !committed && policy != null) { try { policy!.Rules.Remove(RuleName); } catch (Exception ex) when (MissingRule(ex)) { } if (original != null) policy!.Rules.Add(original); }
            changed = false;
        }
    }
}
