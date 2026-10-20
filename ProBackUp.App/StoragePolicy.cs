using System.ComponentModel;
using System.Runtime.InteropServices;
namespace ProBackUp;

// Called under the backup operation lock and the configured network identity.
internal static class StoragePolicy
{
    internal const long Reserve = 64L * 1024 * 1024;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    internal static long Available(string destination)
    {
        if (!GetDiskFreeSpaceEx(Path.GetFullPath(destination).TrimEnd('\\') + "\\", out var available, out _, out _))
            throw new IOException("No se pudo consultar el espacio disponible para la cuenta del destino.", new Win32Exception(Marshal.GetLastWin32Error()));
        return available > long.MaxValue ? long.MaxValue : (long)available;
    }
    private sealed record Snapshot(string Path, BackupManifest Manifest);
    private static async Task<List<Snapshot>> Snapshots(string repo, CancellationToken token)
    {
        var result = new List<Snapshot>(); var versions = Path.Combine(repo, "versions");
        if (!Directory.Exists(versions)) return result;
        // Do not follow directory links while enumerating a repository.
        foreach (var day in Directory.EnumerateDirectories(versions))
        {
            BackupEngine.NoLinks(day);
            foreach (var path in Directory.EnumerateFiles(day, "*.pbi"))
            {
                var manifest = await BackupArchive.ReadManifest(path, token);
                if (!string.Equals(IncrementalStore.Repository(path, manifest), repo, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Repositorio de versión incorrecto.");
                result.Add(new(path, manifest));
            }
        }
        return result.OrderBy(s => s.Manifest.Created).ThenBy(s => s.Path, StringComparer.Ordinal).ToList();
    }
    private static long Used(string repo)
    {
        long total = 0;
        foreach (var directory in new[] { Path.Combine(repo, "objects"), Path.Combine(repo, "versions") })
            if (Directory.Exists(directory)) total = checked(total + Size(directory));
        return total;
        static long Size(string directory)
        {
            BackupEngine.NoLinks(directory); long bytes = 0;
            foreach (var path in Directory.EnumerateFiles(directory)) { BackupEngine.NoLinks(path); bytes = checked(bytes + new FileInfo(path).Length); }
            foreach (var child in Directory.EnumerateDirectories(directory)) bytes = checked(bytes + Size(child));
            return bytes;
        }
    }
    internal static async Task EnsureCapacity(BackupJob job, string repo, BackupManifest pending, long incoming, IProgress<string>? progress, CancellationToken token, Func<long>? available = null)
    {
        available ??= () => Available(job.Destination);
        bool Fits() => available() >= checked(incoming + Reserve) && (job.StorageLimitBytes == 0 || Used(repo) <= job.StorageLimitBytes - incoming);
        if (Fits()) return;
        var snapshots = await Snapshots(repo, token);
        if (job.CleanWhenFull) await Collect(repo, snapshots.SelectMany(s => s.Manifest.Files).Concat(pending.Files), token);
        while (job.CleanWhenFull && snapshots.Count > 1 && !Fits())
            await RemoveOldest(repo, snapshots, pending, progress, token);
        if (!Fits()) throw new IOException("Espacio o cuota insuficiente. Se conserva la última versión válida; aumenta el espacio disponible o reduce el origen. No se elimina la última versión para intentar el respaldo.");
    }
    internal static async Task AfterPublish(BackupJob job, string repo, BackupManifest pending, IProgress<string>? progress, CancellationToken token)
    {
        var snapshots = await Snapshots(repo, token);
        while (snapshots.Count > 1 && ((job.RetentionDays > 0 && snapshots[0].Manifest.Created < DateTimeOffset.Now.AddDays(-job.RetentionDays)) ||
            (job.CleanWhenFull && job.StorageLimitBytes > 0 && Used(repo) > job.StorageLimitBytes)))
            await RemoveOldest(repo, snapshots, pending, progress, token);
        if (job.StorageLimitBytes > 0 && Used(repo) > job.StorageLimitBytes)
            throw new IOException("La versión fue guardada, pero supera el límite de almacenamiento. Se ha conservado íntegra en: " + snapshots[^1].Path);
    }
    private static async Task RemoveOldest(string repo, List<Snapshot> snapshots, BackupManifest pending, IProgress<string>? progress, CancellationToken token)
    {
        if (snapshots.Count < 2) return;
        var keep = snapshots.Skip(1).SelectMany(s => s.Manifest.Files).Concat(pending.Files).GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        // Verify every referenced object before deleting any version. A corrupt repository is never pruned.
        foreach (var file in keep)
        {
            token.ThrowIfCancellationRequested(); var path = IncrementalStore.ObjectPath(repo, file); BackupEngine.NoLinks(path);
            await using var input = File.OpenRead(path); await BackupEngine.Check(input, file, token);
        }
        token.ThrowIfCancellationRequested(); var oldest = snapshots[0];
        progress?.Report("Liberando espacio: versión " + oldest.Manifest.Created.ToString("g"));
        BackupEngine.NoLinks(oldest.Path); File.Delete(oldest.Path); snapshots.RemoveAt(0);
        foreach (var record in Store.History().Where(r => r.JobId == oldest.Manifest.JobId && string.Equals(r.Artifact, oldest.Path, StringComparison.OrdinalIgnoreCase)))
        { record.Status = "Eliminado por retención"; Store.Record(record); }
        // If interrupted here, only unused objects remain; all retained versions are still complete.
        await Collect(repo, keep, token);
    }
    private static async Task Collect(string repo, IEnumerable<FileEntry> files, CancellationToken token)
    {
        var keep = files.GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        foreach (var file in keep)
        {
            token.ThrowIfCancellationRequested(); var path = IncrementalStore.ObjectPath(repo, file); BackupEngine.NoLinks(path);
            await using var input = File.OpenRead(path); await BackupEngine.Check(input, file, token);
        }
        var references = keep.Select(f => f.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repo, "objects")))
        {
            token.ThrowIfCancellationRequested(); var name = Path.GetFileName(path);
            if (name.Length != 64 || !name.All(Uri.IsHexDigit) || references.Contains(name)) continue;
            BackupEngine.NoLinks(path); File.Delete(path);
        }
    }
}
