using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProBackUp;

public sealed class BackupJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Mi respaldo";
    public List<string> Sources { get; set; } = [];
    public string Destination { get; set; } = "";
    public string Owner { get; set; } = "";
    public bool Zip { get; set; } = true;
    public bool Daily { get; set; }
    public string Time { get; set; } = "18:00";
    public bool Incremental { get; set; }
    public int RetentionDays { get; set; }
    public bool CleanWhenFull { get; set; } = true;
    public long StorageLimitBytes { get; set; }
    public List<DayOfWeek> Days { get; set; } = [];
    public List<string> Times { get; set; } = [];
    public bool RunMissed { get; set; } = true;
    public DateTime ScheduledFrom { get; set; } = DateTime.Now;
    public NetworkSettings Network { get; set; } = new();
}
public sealed class NetworkSettings
{
    public string Mode { get; set; } = "Service";
    public string UserSid { get; set; } = "";
    public string User { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    public string Password() => Secrets.Unprotect(EncryptedPassword);
    public void SetPassword(string value) => EncryptedPassword = Secrets.Protect(value);
}
public static class Secrets
{
    public static string Protect(string value) => value.Length == 0 ? "" : "machine:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine));
    public static string Unprotect(string value)
    {
        if (value.Length == 0) return "";
        bool machine = value.StartsWith("machine:", StringComparison.Ordinal);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(machine ? value[8..] : value), null, machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser));
    }
}
public sealed class MailSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "STARTTLS";
    public string User { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    public string From { get; set; } = "";
    public string Recipients { get; set; } = "";
    public bool OnSuccess { get; set; } = true;
    public bool OnFailure { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public string Password()
    {
        return Secrets.Unprotect(EncryptedPassword);
    }
    public void SetPassword(string value) => EncryptedPassword = Secrets.Protect(value);
}
public sealed class AppSettings
{
    public int SessionExecutionVersion { get; set; }
    public CentralSettings Central { get; set; } = new();
    public bool DarkMode { get; set; } = true;
    public List<BackupJob> Jobs { get; set; } = [];
    public MailSettings Mail { get; set; } = new();
}
public sealed class BackupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = "";
    public string Destination { get; set; } = "";
    public string User { get; set; } = "";
    public long CopiedBytes { get; set; }
    public string JobName { get; set; } = "";
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Finished { get; set; }
    public string Status { get; set; } = "En curso";
    public string Artifact { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public string Error { get; set; } = "";
    public string EmailStatus { get; set; } = "No solicitado";
    public string ScheduleKey { get; set; } = "";
    public string Format { get; set; } = "";
    public int ChangedFiles { get; set; }
    public int DeletedFiles { get; set; }
}
public sealed record FileEntry(string Path, long Bytes, string Sha256);
public sealed class BackupManifest
{
    public string Product { get; set; } = "ProBackUp";
    public int Version { get; set; } = 1;
    public string JobName { get; set; } = "";
    public List<FileEntry> Files { get; set; } = [];
    public List<string> Directories { get; set; } = [];
    public string JobId { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public long NewDataBytes { get; set; }
    public int ChangedFiles { get; set; }
    public int DeletedFiles { get; set; }
}
public static class Store
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProBackUp");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value, Json); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static T Read<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("Archivo de datos vacío: " + path);
    }
    public static AppSettings Load() => UserSession.AsProcess(() => File.Exists(SettingsPath) ? Read<AppSettings>(SettingsPath) : new());
    public static void Save(AppSettings settings)
    {
        if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".bak", true);
        Write(SettingsPath, settings);
    }
    public static void Record(BackupRecord record) => UserSession.AsProcess(() => { Write(Path.Combine(Root, "history", record.Id + ".json"), record); CentralReporter.Enqueue(record); });
    public static List<BackupRecord> History() => UserSession.AsProcess(() =>
    {
        var dir = Path.Combine(Root, "history");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.json").Select(Read<BackupRecord>).OrderByDescending(r => r.Started).ToList();
    });
    public static FileStream Acquire() => UserSession.AsProcess(() => { Directory.CreateDirectory(Root); return new FileStream(Path.Combine(Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); });
}
