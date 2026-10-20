using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
namespace ProBackUp;
public static class MachineStorage
{
    public static void Initialize()
    {
        var dir = new DirectoryInfo(Store.Root); BackupEngine.NoLinks(dir.FullName); dir.Create();
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(acl);
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProBackUp");
        if (File.Exists(Store.SettingsPath))
        {
            using var gate = Store.Acquire(); var existing = Store.Load();
            if (UserSession.Migrate(existing)) { Store.Save(existing); if (File.Exists(Store.SettingsPath + ".bak")) Store.Write(Store.SettingsPath + ".bak", existing); }
            return;
        }
        if (!File.Exists(Path.Combine(legacy, "settings.json"))) return;
        var old = Store.Root;
        AppSettings settings; List<BackupRecord> records;
        try { Store.Root = legacy; settings = Store.Load(); records = Store.History(); }
        finally { Store.Root = old; }
        settings.Mail.SetPassword(settings.Mail.Password());
        foreach (var job in settings.Jobs)
        {
            job.Destination = NetworkAccess.Normalize(job.Destination); job.Sources = job.Sources.Select(NetworkAccess.Normalize).ToList();
            var info = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/Delete", "/TN", "ProBackUp-" + job.Id, "/F" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.WaitForExit(); Task.WaitAll(stdout, stderr);
        }
        UserSession.Migrate(settings); Store.Save(settings); foreach (var record in records) Store.Record(record);
    }
}
