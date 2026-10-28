using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
namespace ProBackUp;
public static class MachineStorage
{
    public static void Initialize()
    {
        var dir = new DirectoryInfo(Store.Root); BackupEngine.NoLinks(dir.FullName); dir.Create();
        // The elevated installer grants access only to the desktop owner.
        // Opening the UI never changes ACLs or requests elevation.
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProBackUp");
        if (File.Exists(Store.SettingsPath))
        {
            var existing = Store.Load();
            var changed = UserSession.Migrate(existing);
            using var owner = System.Security.Principal.WindowsIdentity.GetCurrent();
            foreach (var job in existing.Jobs)
                if (job.Network.Mode == "Session" && job.Network.UserSid.Length == 0 && job.Network.User.Equals(owner.Name, StringComparison.OrdinalIgnoreCase))
                { job.Network.UserSid = owner.User!.Value; changed = true; }
            if (changed) { using var gate = Store.Acquire(); Store.Save(existing); if (File.Exists(Store.SettingsPath + ".bak")) Store.Write(Store.SettingsPath + ".bak", existing); }
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
