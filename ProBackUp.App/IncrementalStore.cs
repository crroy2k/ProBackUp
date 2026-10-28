using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProBackUp;
public static class IncrementalStore
{
    public static async Task<(string path, BackupManifest manifest)> Create(BackupJob job, IProgress<string>? progress, CancellationToken token)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _)) throw new ArgumentException("ID de tarea inválido.");
        var bandwidth = new UploadBandwidth(job);
        var repository = Path.Combine(job.Destination, "ProBackUp-Incremental", job.Id);
        BackupEngine.NoLinks(repository); Directory.CreateDirectory(repository);
        var objects = Path.Combine(repository, "objects"); BackupEngine.NoLinks(objects); Directory.CreateDirectory(objects);
        var manifest = new BackupManifest { Version = 2, JobId = job.Id, JobName = job.Name, Created = DateTimeOffset.Now };
        var previousPath = Directory.Exists(Path.Combine(repository, "versions")) ? Directory.EnumerateFiles(Path.Combine(repository, "versions"), "*.pbi", SearchOption.AllDirectories).OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null;
        var previous = previousPath == null ? null : await BackupArchive.ReadManifest(previousPath, token);
        var oldFiles = previous?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? [];
        async Task Visit(string source, string relative)
        {
            token.ThrowIfCancellationRequested(); BackupEngine.NoLinks(source);
            if (Directory.Exists(source))
            {
                manifest.Directories.Add(relative.Replace('\\', '/'));
                foreach (var entry in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.OrdinalIgnoreCase)) await Visit(entry, Path.Combine(relative, Path.GetFileName(entry)));
                return;
            }
            progress?.Report("Comprobando cambios: " + source);
            var info = new FileInfo(source); var length = info.Length; var modified = info.LastWriteTimeUtc;
            string temp = Path.Combine(objects, ".partial-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Hash on a shared-read handle; write only a missing content object.
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                var entry = new FileEntry(relative.Replace('\\', '/'), length, hash);
                var obj = ObjectPath(repository, entry); BackupEngine.NoLinks(obj);
                if (!File.Exists(obj))
                {
                    await StoragePolicy.EnsureCapacity(job, repository, manifest, length, progress, token);
                    input.Position = 0;
                    await using (var output = bandwidth.Open(temp, token)) await input.CopyToAsync(output, token);
                    await using (var verify = File.OpenRead(temp)) await BackupEngine.Check(verify, entry, token);
                    File.Move(temp, obj); manifest.NewDataBytes += length;
                }
                else { await using var verify = File.OpenRead(obj); await BackupEngine.Check(verify, entry, token); }
                info.Refresh(); if (info.Length != length || info.LastWriteTimeUtc != modified) throw new IOException("El archivo cambió durante el respaldo: " + source);
                manifest.Files.Add(entry);
                if (!oldFiles.TryGetValue(entry.Path, out var old) || old.Sha256 != hash) manifest.ChangedFiles++;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        for (int i = 0; i < job.Sources.Count; i++)
        {
            var source = Path.GetFullPath(job.Sources[i]);
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
            await Visit(source, $"{i + 1:D2}-" + (name.Length > 0 ? name : "Unidad").Replace(':', '_'));
        }
        manifest.DeletedFiles = oldFiles.Keys.Except(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase).Count();
        BackupEngine.ValidateManifest(manifest); token.ThrowIfCancellationRequested();
        var objectBytes = manifest.NewDataBytes; long manifestBytes;
        do { manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Store.Json).LongLength; manifest.NewDataBytes = objectBytes + manifestBytes; }
        while (JsonSerializer.SerializeToUtf8Bytes(manifest, Store.Json).LongLength != manifestBytes);
        await StoragePolicy.EnsureCapacity(job, repository, manifest, manifestBytes, progress, token);
        var day = Path.Combine(repository, "versions", DateTime.Now.ToString("yyyy-MM-dd")); BackupEngine.NoLinks(day); Directory.CreateDirectory(day);
        var final = Path.Combine(day, DateTime.Now.ToString("HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".pbi");
        await bandwidth.WriteManifest(final, manifest, token);
        return (final, manifest);
    }
    internal static string ObjectPath(string repository, FileEntry file)
    {
        if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Hash inválido.");
        return Path.Combine(repository, "objects", file.Sha256.ToUpperInvariant());
    }
    public static string Repository(string artifact, BackupManifest manifest)
    {
        var day = Directory.GetParent(artifact); var versions = day?.Parent; var repo = versions?.Parent;
        if (manifest.Version != 2 || !Guid.TryParseExact(manifest.JobId, "N", out _) || versions?.Name != "versions" || repo?.Name != manifest.JobId || !DateOnly.TryParseExact(day!.Name, "yyyy-MM-dd", out _)) throw new InvalidDataException("La versión incremental no está en su repositorio.");
        BackupEngine.NoLinks(repo.FullName); return repo.FullName;
    }
    public static async Task<string> Restore(string artifact, string destination, string? onlyFile, IProgress<string>? progress, CancellationToken token)
    {
        var manifest = await BackupArchive.ReadManifest(artifact, token); var repo = Repository(artifact, manifest);
        if (!Path.IsPathFullyQualified(destination) || BackupEngine.Within(destination, repo) || BackupEngine.Within(repo, destination)) throw new IOException("Elige un destino de restauración fuera del repositorio.");
        BackupEngine.NoLinks(destination); Directory.CreateDirectory(destination);
        var files = onlyFile == null ? manifest.Files : manifest.Files.Where(f => f.Path == onlyFile).ToList();
        if (onlyFile != null && files.Count != 1) throw new IOException("El archivo no existe en esta versión.");
        var final = Path.Combine(destination, "Restaurado-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]); var staging = final + ".partial";
        try
        {
            Directory.CreateDirectory(staging);
            if (onlyFile == null) foreach (var dir in manifest.Directories) Directory.CreateDirectory(BackupEngine.SafePath(staging, dir));
            foreach (var entry in files)
            {
                token.ThrowIfCancellationRequested(); progress?.Report("Restaurando versión: " + entry.Path);
                var obj = ObjectPath(repo, entry); BackupEngine.NoLinks(obj);
                var target = BackupEngine.SafePath(staging, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = File.OpenRead(obj)) await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await input.CopyToAsync(output, token);
                await using var verify = File.OpenRead(target); await BackupEngine.Check(verify, entry, token);
            }
            Directory.Move(staging, final); return final;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public static async Task<string> RestoreFile(string artifact, string file, string destination, CancellationToken token)
    {
        using var gate = Store.Acquire();
        if ((await BackupArchive.Inspect(artifact, token)).Kind == BackupKind.Incremental) return await Restore(artifact, destination, file, null, token);
        var manifest = await BackupArchive.ReadManifest(artifact, token); var entry = manifest.Files.Single(f => f.Path == file);
        BackupEngine.NoLinks(destination);
        if (!Path.IsPathFullyQualified(destination) || BackupEngine.Within(destination, artifact) || BackupEngine.Within(artifact, destination)) throw new IOException("Elige otra carpeta para restaurar.");
        Directory.CreateDirectory(destination); var final = Path.Combine(destination, "Archivo-" + Guid.NewGuid().ToString("N")); var staging = final + ".partial";
        try
        {
            Directory.CreateDirectory(staging); var target = BackupEngine.SafePath(staging, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(artifact))
            { using var zip = ZipFile.OpenRead(artifact); await using var input = zip.GetEntry("data/" + entry.Path)!.Open(); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); await input.CopyToAsync(output, token); }
            else
            { var source = BackupEngine.SafePath(Path.Combine(artifact, "data"), entry.Path); BackupEngine.NoLinks(source); File.Copy(source, target); }
            await using (var verify = File.OpenRead(target)) await BackupEngine.Check(verify, entry, token);
            Directory.Move(staging, final); return final;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}

