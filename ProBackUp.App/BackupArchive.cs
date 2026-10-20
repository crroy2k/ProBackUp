using System.IO.Compression;
using System.Text.Json;
namespace ProBackUp;

public enum BackupKind { FullZip, FullFolder, Incremental }
public sealed record BackupInfo(BackupKind Kind, BackupManifest Manifest);
public static class BackupArchive
{
    public const long MaxManifestBytes = 64L * 1024 * 1024;
    public static readonly string[] Labels = ["Completo · ZIP comprimido", "Completo · carpeta sin compresión", "Incremental · versiones de archivos"];
    public static BackupKind FromJob(BackupJob job) => job.Incremental ? BackupKind.Incremental : job.Zip ? BackupKind.FullZip : BackupKind.FullFolder;
    public static BackupKind FromRecord(BackupRecord record) => Enum.TryParse<BackupKind>(record.Format, out var kind) ? kind : record.Artifact.EndsWith(".pbi", StringComparison.OrdinalIgnoreCase) ? BackupKind.Incremental : record.Artifact.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? BackupKind.FullZip : BackupKind.FullFolder;
    public static string Label(BackupKind kind) => Labels[(int)kind];
    public static async Task<BackupInfo> Inspect(string artifact, CancellationToken token)
    {
        BackupEngine.NoLinks(artifact);
        BackupManifest manifest; BackupKind kind;
        if (Directory.Exists(artifact))
        {
            var path = Path.Combine(artifact, "probackup-manifest.json"); BackupEngine.NoLinks(path);
            await using var stream = File.OpenRead(path); manifest = await ReadBounded(stream, token); kind = BackupKind.FullFolder;
            if (manifest.Version != 1) throw new InvalidDataException("Selecciona una versión .pbi dentro del repositorio incremental.");
        }
        else
        {
            await using var stream = File.OpenRead(artifact);
            var signature = new byte[4]; int read = await stream.ReadAsync(signature, token); stream.Position = 0;
            bool zip = read == 4 && signature[0] == 'P' && signature[1] == 'K' && signature[2] is 3 or 5 or 7;
            if (zip)
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
                var entries = archive.Entries.Where(e => e.FullName == "probackup-manifest.json").ToList();
                if (entries.Count != 1) throw new InvalidDataException("El ZIP debe contener un único manifiesto de ProBackUp.");
                if (entries[0].Length > MaxManifestBytes) throw new InvalidDataException("Manifiesto demasiado grande.");
                await using var metadata = entries[0].Open(); manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(metadata, Store.Json, token) ?? throw new InvalidDataException("Manifiesto vacío.");
                if (manifest.Version != 1) throw new InvalidDataException("Formato de ZIP no compatible."); kind = BackupKind.FullZip;
            }
            else
            {
                manifest = await ReadBounded(stream, token); if (manifest.Version != 2) throw new InvalidDataException("El archivo no es un ZIP ni una versión incremental de ProBackUp.");
                IncrementalStore.Repository(artifact, manifest); kind = BackupKind.Incremental;
            }
        }
        BackupEngine.ValidateManifest(manifest); return new(kind, manifest);
    }
    private static async Task<BackupManifest> ReadBounded(Stream stream, CancellationToken token)
    {
        if (stream.Length > MaxManifestBytes) throw new InvalidDataException("Manifiesto demasiado grande.");
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, Store.Json, token) ?? throw new InvalidDataException("Manifiesto vacío.");
    }
    public static async Task<BackupManifest> ReadManifest(string artifact, CancellationToken token) => (await Inspect(artifact, token)).Manifest;
}
