using System.Diagnostics;
using System.ServiceProcess;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
namespace ProBackUp.Setup;
internal static class ServiceSetup
{
    public const string Name = "ProBackUpService";
    private static bool Exists() { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name); return key != null; }
    private static void Own(string target)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name);
        if (key == null) return;
        var expected = $"\"{Path.Combine(target, "ProBackUp.exe")}\" --service";
        if (!string.Equals(key.GetValue("ImagePath") as string, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Ya existe un servicio ProBackUp con otra ruta. No se modificó.");
    }
    private static void Command(params string[] args)
    {
        var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); process.WaitForExit(); Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new IOException("No se pudo configurar el servicio: " + output.Result + error.Result);
    }
    public static bool Stop(string target)
    {
        Own(target); if (!Exists()) return false;
        using var service = new ServiceController(Name); bool wasRunning = service.Status != ServiceControllerStatus.Stopped;
        if (wasRunning) { if (service.Status != ServiceControllerStatus.StopPending) service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); }
        return wasRunning;
    }
    public static void StartExisting()
    {
        if (!Exists()) return; using var service = new ServiceController(Name);
        if (service.Status == ServiceControllerStatus.Stopped) service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }
    public static void Configure(string target)
    {
        Own(target); bool existed = Exists();
        PrepareData();
        var binary = $"\"{Path.Combine(target, "ProBackUp.exe")}\" --service";
        try
        {
            if (existed) Command("config", Name, "binPath=", binary, "start=", "auto", "obj=", "LocalSystem");
            else Command("create", Name, "binPath=", binary, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "ProBackUp · respaldos automáticos");
            Command("description", Name, "Respaldos completos e incrementales con la sesión del usuario; recupera el último horario vencido al iniciar sesión.");
            Command("failure", Name, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");
            StartExisting();
        }
        catch { if (!existed && Exists()) { Stop(target); Command("delete", Name); } throw; }
    }
    public static void Remove(string target) { Stop(target); if (Exists()) Command("delete", Name); }
    private static void PrepareData()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProBackUp"); Program.CheckPath(target);
        var directory = new DirectoryInfo(target); directory.Create(); var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
}
