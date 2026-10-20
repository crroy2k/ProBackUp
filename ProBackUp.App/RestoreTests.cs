using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace ProBackUp;
internal static class RestoreTests
{
    public static void Run(string report)
    {
        string oldRoot = Store.Root; string root = Path.Combine(Path.GetTempPath(), "ProBackUp-restore-test-" + Guid.NewGuid().ToString("N")); Store.Root = Path.Combine(root, "state"); var results = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); }
        void Ready(FileVersionsDialog form)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do { Application.DoEvents(); Thread.Sleep(10); } while (form.Loading && DateTime.UtcNow < deadline);
            if (form.Loading) throw new TimeoutException("La vista de versiones no terminó de cargar.");
        }
        try
        {
            string source = Path.Combine(root, "source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "datos.txt"), "Versión antigua"); File.WriteAllText(Path.Combine(source, "eliminado.txt"), "Archivo que desaparece");
            Directory.CreateDirectory(Path.Combine(source, "Informes")); File.WriteAllText(Path.Combine(source, "Informes", "informe.PDF"), "fixture pdf"); File.WriteAllText(Path.Combine(source, "Informes", "informe.docx"), "fixture docx"); File.WriteAllText(Path.Combine(source, "notas.txtx"), "extensión distinta");
            var job = new BackupJob { Name = "Versiones", Sources = [source], Destination = Path.Combine(root, "backups"), Incremental = true };
            var first = BackupEngine.Run(job, new()).GetAwaiter().GetResult(); File.WriteAllText(Path.Combine(source, "datos.txt"), "Versión nueva"); File.Delete(Path.Combine(source, "eliminado.txt"));
            var second = BackupEngine.Run(job, new()).GetAwaiter().GetResult(); var settings = new AppSettings { Jobs = [job] };
            Check(first.Status == "Correcto" && second.Status == "Correcto", "Prepara dos versiones distintas del mismo día");
            string zipSource = Path.Combine(root, "large.zip");
            using (var archive = ZipFile.Open(zipSource, ZipArchiveMode.Create))
            {
                var data = archive.CreateEntry("data/large.bin", CompressionLevel.NoCompression); using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024];
                using (var output = data.Open()) for (int i = 0; i < 65; i++) { output.Write(buffer); sha.AppendData(buffer); }
                var manifest = new BackupManifest { Files = [new FileEntry("large.bin", 65L * 1024 * 1024, Convert.ToHexString(sha.GetHashAndReset()))] };
                using var writer = new StreamWriter(archive.CreateEntry("probackup-manifest.json").Open()); writer.Write(JsonSerializer.Serialize(manifest));
            }
            var large = BackupArchive.Inspect(zipSource, default).GetAwaiter().GetResult(); Check(new FileInfo(zipSource).Length > BackupArchive.MaxManifestBytes && large.Manifest.Files.Count == 1, "Lee versiones en ZIP de más de 64 MB");
            string renamed = Path.Combine(root, "respaldo-renombrado.bin"); File.Copy(zipSource, renamed); Check(BackupArchive.Inspect(renamed, default).GetAwaiter().GetResult().Kind == BackupKind.FullZip, "Detecta formato por contenido, no por la extensión");
            string recoveredLarge = BackupEngine.Restore(renamed, Path.Combine(root, "restore-large"), null, default).GetAwaiter().GetResult(); Check(new FileInfo(Path.Combine(recoveredLarge, "large.bin")).Length == 65L * 1024 * 1024, "Restaura ZIP grande identificado por contenido");
            using (var form = new FileVersionsDialog(first, settings))
            {
                form.Show(); Ready(form); Check(form.SelectedArtifact == first.Artifact, "Respeta la versión elegida en el historial, aunque exista una más reciente");
                form.SelectFile("01-source/datos.txt"); form.SelectVersion(second.Id); Ready(form); Check(form.SelectedArtifact == second.Artifact && form.SelectedFile == "01-source/datos.txt", "Conserva el archivo seleccionado al cambiar de hora");
                form.SelectVersion(first.Id); Ready(form); form.SelectFile("01-source/eliminado.txt"); form.SelectVersion(second.Id); Ready(form); Check(form.SelectedFile == null && !form.CanRestore, "No sustituye un archivo ausente por otro de la versión nueva");
                form.SelectVersion(first.Id); Ready(form); Check(form.SelectedFile == "01-source/eliminado.txt" && form.CanRestore, "Recupera la selección al volver a una versión donde existe");
                form.SetFilter("INFORMES", "*.pdf; DOCX"); Check(form.VisibleFiles.Count == 2 && form.VisibleFiles.All(p => p.Contains("Informes/")) && form.SelectedArtifact == first.Artifact, "Combina búsqueda por ruta y múltiples extensiones sin cambiar versión");
                SavePreview(form, "buscador-versiones.png");
                Check(!form.CanRestore, "No restaura una selección oculta por los filtros");
                form.SetFilter("", ".txt"); Check(form.VisibleFiles.Count == 2 && !form.VisibleFiles.Any(p => p.EndsWith(".txtx")), "Filtra la extensión exacta sin confundir txt con txtx");
                form.SetFilter("no-existe", ""); Check(form.VisibleFiles.Count == 0 && !form.CanRestore, "Búsqueda sin coincidencias no permite restaurar otro archivo");
                form.SetFilter("informe", "pdf,docx"); form.SelectVersion(second.Id); Ready(form); Check(form.VisibleFiles.Count == 2 && form.VisibleFiles.All(p => p.Contains("informe")), "Conserva búsqueda y extensiones al cambiar de hora");
                form.SelectVersion(first.Id); Ready(form); form.SetFilter("", ""); Check(form.VisibleFiles.Count == 5 && form.SelectedFile == "01-source/eliminado.txt" && form.CanRestore, "Limpiar filtros recupera selección sin cambiar el archivo");
                SavePreview(form, "versiones-corregidas.png");
                string output = Task.Run(() => RestoreOperations.Run(new(first.Artifact, Path.Combine(root, "restore-one"), new(), form.SelectedFile), null, default)).GetAwaiter().GetResult();
                Check(File.ReadAllText(Path.Combine(output, "01-source", "eliminado.txt")) == "Archivo que desaparece", "Restaura el contenido exacto del archivo y la hora elegidos");
                form.Close();
            }
            using (var wizard = new RestoreDialog(true, first.Artifact, new(), BackupKind.FullZip, "01-source/datos.txt"))
            {
                wizard.Show(); var deadline = DateTime.UtcNow.AddSeconds(10); do { Application.DoEvents(); Thread.Sleep(10); } while (wizard.Loading && DateTime.UtcNow < deadline);
                Check(wizard.DetectedKind == BackupKind.Incremental && wizard.SelectedFile == "01-source/datos.txt" && wizard.CanRestore, "Restauración detecta el formato real y conserva el archivo solicitado"); SavePreview(wizard, "restauracion-unificada.png"); wizard.Close();
            }
            using (var missing = new RestoreDialog(true, second.Artifact, new(), BackupKind.Incremental, "01-source/eliminado.txt"))
            {
                missing.Show(); var deadline = DateTime.UtcNow.AddSeconds(10); do { Application.DoEvents(); Thread.Sleep(10); } while (missing.Loading && DateTime.UtcNow < deadline);
                Check(missing.SelectedFile == null && !missing.CanRestore, "Restauración no permite sustituir silenciosamente un archivo ausente"); missing.Close();
            }
            Check(JsonSerializer.Deserialize<BackupManifest>("{\"Product\":\"ProBackUp\",\"Version\":1}")!.Created == default, "Los respaldos antiguos no inventan una fecha de versión");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); File.WriteAllText(report, string.Join(Environment.NewLine, results) + Environment.NewLine + "ALL RESTORE TESTS PASSED");
        }
        finally { Store.Root = oldRoot; if (Directory.Exists(root)) Directory.Delete(root, true); }
        void SavePreview(Form form, string name)
        {
            string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "previews"); Directory.CreateDirectory(folder); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, name));
        }
    }
}
