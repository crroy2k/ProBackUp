using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProBackUp;

public static class BackupEngine
{
    private const string ManifestName = "probackup-manifest.json";
    public static bool Within(string path, string parent) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void NoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("No se admiten enlaces simbólicos ni puntos de redirección: " + p);
    }
    public static void ValidateDefinition(BackupJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Name)) throw new ArgumentException("Escribe un nombre para el respaldo.");
        if (job.Sources.Count == 0) throw new ArgumentException("Agrega al menos un archivo o carpeta de origen.");
        if (!Path.IsPathFullyQualified(job.Destination)) throw new ArgumentException("El destino debe ser una ruta absoluta o UNC.");
        NetworkAccess.ValidateJob(job);
        if (job.RetentionDays < 0 || job.StorageLimitBytes < 0) throw new ArgumentException("La retención y el límite no pueden ser negativos.");
        foreach (var source in job.Sources)
        {
            if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("El origen debe ser una ruta absoluta o UNC.");
            if (Within(job.Destination, source) || Within(source, job.Destination)) throw new IOException("El origen y el destino no pueden estar dentro uno del otro.");
        }
    }
    public static void Validate(BackupJob job)
    {
        ValidateDefinition(job);
        foreach (var source in job.Sources)
        {
            if (!File.Exists(source) && !Directory.Exists(source)) throw new IOException("El origen no existe o no es accesible con la sesión del usuario: " + source);
            NoLinks(source);
        }
    }
    public static async Task<BackupRecord> Run(BackupJob job, MailSettings mail, IProgress<string>? progress = null, CancellationToken cancel = default, string scheduleKey = "", string recordId = "", Func<Task>? onStarted = null)
    {
        using var session = NetworkAccess.AcquireJobSession(job);
        using var gate = Store.Acquire();
        if (session != null) UserSession.Bind(job, session);
        if (recordId.Length > 0 && (!Guid.TryParseExact(recordId, "N", out _) || File.Exists(Path.Combine(Store.Root, "history", recordId + ".json")))) throw new IOException("La orden ya tiene una ejecución registrada.");
        var record = new BackupRecord { Id = recordId.Length > 0 ? recordId : Guid.NewGuid().ToString("N"), JobId = job.Id, JobName = job.Name, Destination = job.Destination, User = job.Owner.Length > 0 ? job.Owner : job.Network.User.Length > 0 ? job.Network.User : System.Security.Principal.WindowsIdentity.GetCurrent().Name, ScheduleKey = scheduleKey, Format = BackupArchive.FromJob(job).ToString() };
        Store.Record(record);
        try
        {
            if (onStarted != null) await onStarted();
            var result = await NetworkAccess.RunJob(job, async () =>
            {
                var created = await Create(job, progress, cancel);
                // Destination metadata must be read under the same personal network identity.
                var copiedBytes = job.Incremental ? created.manifest.NewDataBytes : job.Zip ? new FileInfo(created.path).Length : created.manifest.Files.Sum(x => x.Bytes) + new FileInfo(Path.Combine(created.path, "probackup-manifest.json")).Length;
                return (created.path, created.manifest, copiedBytes);
            }, session);
            record.Artifact = result.path; record.Files = result.manifest.Files.Count; record.Bytes = result.manifest.Files.Sum(x => x.Bytes);
            record.CopiedBytes = result.copiedBytes;
            record.ChangedFiles = result.manifest.ChangedFiles; record.DeletedFiles = result.manifest.DeletedFiles;
            if (job.Incremental) await NetworkAccess.RunJob(job, async () => { await StoragePolicy.AfterPublish(job, IncrementalStore.Repository(result.path, result.manifest), result.manifest, progress, cancel); return true; }, session);
            record.Status = "Correcto";
        }
        catch (OperationCanceledException) { record.Status = "Cancelado"; record.Error = "Operación cancelada por el usuario."; }
        catch (Exception ex) { record.Status = "Error"; record.Error = ex.Message; try { var secret = job.Network.Password(); if (secret.Length > 0) record.Error = record.Error.Replace(secret, "[oculto]", StringComparison.Ordinal); } catch { } }
        record.Finished = DateTimeOffset.Now;
        Store.Record(record);
        if (mail.Enabled && ((record.Status == "Correcto" && mail.OnSuccess) || (record.Status != "Correcto" && mail.OnFailure)))
        {
            try
            {
                progress?.Report("Enviando notificación por correo…");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(mail.TimeoutSeconds));
                await MailService.Send(mail, $"[ProBackUp] {record.Status}: {job.Name}", $"Respaldo: {job.Name}\r\nEstado: {record.Status}\r\nInicio: {record.Started:yyyy-MM-dd HH:mm:ss zzz}\r\nFin: {record.Finished:yyyy-MM-dd HH:mm:ss zzz}\r\nArchivos: {record.Files}\r\nBytes: {record.Bytes}\r\nDestino: {record.Artifact}\r\nDetalle: {record.Error}", deadline.Token);
                record.EmailStatus = "Enviado";
            }
            catch (Exception ex) { record.EmailStatus = "Error de correo: " + MailService.SafeError(ex, mail); }
            Store.Record(record);
        }
        return record;
    }
    public static async Task<(string path, BackupManifest manifest)> Create(BackupJob job, IProgress<string>? progress, CancellationToken cancel)
    {
        Validate(job);
        NoLinks(job.Destination);
        if (job.Incremental) return await IncrementalStore.Create(job, progress, cancel);
        Directory.CreateDirectory(job.Destination);
        NoLinks(job.Destination);
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var safeName = string.Concat(job.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safeName.Length > 60) safeName = safeName[..60];
        if (safeName.Length == 0) safeName = "Respaldo";
        var day = Path.Combine(job.Destination, DateTime.Now.ToString("yyyy-MM-dd")); NoLinks(day); Directory.CreateDirectory(day);
        var final = Path.Combine(day, safeName + "-" + id + (job.Zip ? ".zip" : ""));
        var staging = Path.Combine(job.Destination, ".probackup-" + id + ".partial");
        var manifest = new BackupManifest { JobName = job.Name, JobId = job.Id, Created = DateTimeOffset.Now };
        try
        {
            Directory.CreateDirectory(staging);
            var data = Path.Combine(staging, "data"); Directory.CreateDirectory(data);
            for (int i = 0; i < job.Sources.Count; i++)
            {
                var source = Path.GetFullPath(job.Sources[i]);
                var prefix = $"{i + 1:D2}-" + (Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : "Unidad").Replace(':', '_');
                if (File.Exists(source)) await Copy(source, prefix, data, manifest, progress, cancel);
                else await CopyTree(source, prefix, data, manifest, progress, cancel);
            }
            manifest.ChangedFiles = manifest.Files.Count;
            Store.Write(Path.Combine(staging, ManifestName), manifest);
            cancel.ThrowIfCancellationRequested();
            if (job.Zip)
            {
                var zipTemp = staging + ".zip";
                try
                {
                    progress?.Report("Comprimiendo y verificando el respaldo…");
                    await Task.Run(() => ZipFile.CreateFromDirectory(staging, zipTemp, CompressionLevel.Optimal, false), cancel);
                    await VerifyZip(zipTemp, manifest, cancel);
                    File.Move(zipTemp, final);
                }
                finally { if (File.Exists(zipTemp)) File.Delete(zipTemp); }
            }
            else
            {
                progress?.Report("Verificando la copia espejo…");
                await VerifyFolder(staging, manifest, cancel);
                Directory.Move(staging, final);
            }
            return (final, manifest);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static async Task CopyTree(string source, string relative, string data, BackupManifest manifest, IProgress<string>? progress, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested(); NoLinks(source);
        Directory.CreateDirectory(Path.Combine(data, relative)); manifest.Directories.Add(relative.Replace('\\', '/'));
        foreach (var entry in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancel.ThrowIfCancellationRequested(); NoLinks(entry);
            var rel = Path.Combine(relative, Path.GetFileName(entry));
            if (Directory.Exists(entry)) await CopyTree(entry, rel, data, manifest, progress, cancel);
            else await Copy(entry, rel, data, manifest, progress, cancel);
        }
    }
    private static async Task Copy(string source, string relative, string data, BackupManifest manifest, IProgress<string>? progress, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested(); NoLinks(source);
        progress?.Report("Copiando: " + source);
        var info = new FileInfo(source); var length = info.Length; var modified = info.LastWriteTimeUtc;
        var target = Path.Combine(data, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
        {
            var buffer = new byte[131072]; int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0) { hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancel); }
            await output.FlushAsync(cancel);
        }
        info.Refresh();
        if (info.Length != length || info.LastWriteTimeUtc != modified) throw new IOException("El archivo cambió durante la copia: " + source);
        File.SetLastWriteTimeUtc(target, modified);
        manifest.Files.Add(new(relative.Replace('\\', '/'), length, Convert.ToHexString(hash.GetHashAndReset())));
    }
    internal static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.Product != "ProBackUp" || manifest.Version is not (1 or 2)) throw new InvalidDataException("Formato de respaldo no compatible.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (!paths.Add(entry.Path) || entry.Bytes < 0 || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Manifiesto inválido.");
            SafePath(Path.GetTempPath(), entry.Path);
        }
        foreach (var directory in manifest.Directories) SafePath(Path.GetTempPath(), directory);
    }
    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is ".." or "." or "" || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith(' ') || p.EndsWith('.') || Reserved(p))) throw new InvalidDataException("Ruta inválida en el respaldo.");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(result, root) || result.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta fuera del respaldo.");
        return result;
    }
    private static bool Reserved(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }
    internal static async Task Check(Stream stream, FileEntry expected, CancellationToken cancel)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; long count = 0; int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            count += read; if (count > expected.Bytes) throw new InvalidDataException("Tamaño alterado: " + expected.Path);
            sha.AppendData(buffer, 0, read);
        }
        if (count != expected.Bytes || !Convert.ToHexString(sha.GetHashAndReset()).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Verificación SHA-256 fallida: " + expected.Path);
    }
    private static async Task VerifyZip(string path, BackupManifest manifest, CancellationToken cancel)
    {
        ValidateManifest(manifest);
        using var archive = ZipFile.OpenRead(path);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries) if (!entries.Add(e.FullName)) throw new InvalidDataException("Entradas duplicadas en ZIP.");
        foreach (var entry in manifest.Files)
        {
            var zipEntry = archive.GetEntry("data/" + entry.Path) ?? throw new InvalidDataException("Falta un archivo: " + entry.Path);
            if (zipEntry.Length != entry.Bytes) throw new InvalidDataException("Tamaño incorrecto: " + entry.Path);
            await using var stream = zipEntry.Open(); await Check(stream, entry, cancel);
        }
    }
    private static async Task VerifyFolder(string root, BackupManifest manifest, CancellationToken cancel)
    {
        ValidateManifest(manifest); NoLinks(root);
        foreach (var entry in manifest.Files)
        {
            var path = SafePath(Path.Combine(root, "data"), entry.Path); NoLinks(path);
            await using var stream = File.OpenRead(path); await Check(stream, entry, cancel);
        }
    }
    public static async Task<string> Restore(string artifact, string destination, IProgress<string>? progress, CancellationToken cancel)
    {
        using var gate = Store.Acquire();
        var backup = await BackupArchive.Inspect(artifact, cancel);
        if (backup.Kind == BackupKind.Incremental) return await IncrementalStore.Restore(artifact, destination, null, progress, cancel);
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("El destino debe ser absoluto.");
        NoLinks(artifact); NoLinks(destination);
        if (Within(destination, artifact) || Within(artifact, destination)) throw new IOException("Restaura en una carpeta fuera del respaldo.");
        Directory.CreateDirectory(destination);
        var final = Path.Combine(destination, "Restaurado-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = final + ".partial";
        try
        {
            BackupManifest manifest = backup.Manifest;
            if (backup.Kind == BackupKind.FullZip)
            {
                using var zip = ZipFile.OpenRead(artifact);
                progress?.Report("Verificando integridad antes de restaurar…"); await VerifyZip(artifact, manifest, cancel);
                Directory.CreateDirectory(staging);
                foreach (var d in manifest.Directories) Directory.CreateDirectory(SafePath(staging, d));
                foreach (var entry in manifest.Files)
                {
                    cancel.ThrowIfCancellationRequested(); progress?.Report("Restaurando: " + entry.Path);
                    var target = SafePath(staging, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = zip.GetEntry("data/" + entry.Path)!.Open(); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, cancel);
                }
            }
            else
            {
                var mp = Path.Combine(artifact, ManifestName); NoLinks(mp);
                await VerifyFolder(artifact, manifest, cancel); Directory.CreateDirectory(staging);
                foreach (var d in manifest.Directories) Directory.CreateDirectory(SafePath(staging, d));
                foreach (var entry in manifest.Files)
                {
                    cancel.ThrowIfCancellationRequested(); progress?.Report("Restaurando: " + entry.Path);
                    var target = SafePath(staging, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var source = SafePath(Path.Combine(artifact, "data"), entry.Path); NoLinks(source);
                    await using var input = File.OpenRead(source); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); await input.CopyToAsync(output, cancel);
                }
            }
            // Verify the restored bytes as well; the source can change after preflight.
            foreach (var entry in manifest.Files) { await using var stream = File.OpenRead(SafePath(staging, entry.Path)); await Check(stream, entry, cancel); }
            Directory.Move(staging, final); return final;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
