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
    private static void Grant(string path, string ownerSid)
    {
        Program.CheckPath(path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Los datos no admiten enlaces: " + path);
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path); var access = info.GetAccessControl(); access.SetAccessRuleProtection(false, false); info.SetAccessControl(access);
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) Grant(child, ownerSid);
        }
        else { var info = new FileInfo(path); var access = info.GetAccessControl(); access.SetAccessRuleProtection(false, false); info.SetAccessControl(access); }
    }
    private static void PrepareData()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProBackUp"); Program.CheckPath(target);
        var directory = new DirectoryInfo(target); directory.Create();
        var ownerSid = ProBackUp.Installation.ClientInstallation.DesktopSid();
        using var registration = Registry.LocalMachine.CreateSubKey(ProBackUp.Installation.ClientInstallation.RegistryPath);
        var existingOwner = registration.GetValue("OwnerSid") as string;
        if (!string.IsNullOrEmpty(existingOwner) && existingOwner != ownerSid) throw new IOException("Este cliente pertenece a otro usuario. Ejecuta la actualización desde la sesión del propietario de los respaldos.");
        directory.SetAccessControl(DataAcl(new SecurityIdentifier(ownerSid)));
        // Existing protected files must also become writable by the desktop owner.
        foreach (var path in Directory.EnumerateFileSystemEntries(target)) Grant(path, ownerSid);
        registration.SetValue("OwnerSid", ownerSid, RegistryValueKind.String);
    }
    private static DirectorySecurity DataAcl(SecurityIdentifier sid)
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var principal in new[] { "S-1-5-18", "S-1-5-32-544" }) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }
    internal static void TestDataAcl(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var path = Path.Combine(directory, "user-data-acl"); Directory.CreateDirectory(path);
        new DirectoryInfo(path).SetAccessControl(DataAcl(identity.User!));
        File.WriteAllText(Path.Combine(path, "settings-check.txt"), "usuario sin elevación");
        var rules = new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        if (rules.Count != 3 || rules.Any(r => r.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545")) throw new IOException("Permisos de datos demasiado amplios.");
        if (File.ReadAllText(Path.Combine(path, "settings-check.txt")) != "usuario sin elevación") throw new IOException("El usuario no puede leer su configuración.");
        File.Delete(Path.Combine(path, "settings-check.txt")); Directory.Delete(path);
    }
}
