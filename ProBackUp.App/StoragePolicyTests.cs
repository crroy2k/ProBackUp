namespace ProBackUp;
internal static class StoragePolicyTests
{
    internal static async Task Run(string root, Action<bool, string> check)
    {
        var source = Path.Combine(root, "retention-source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "shared.txt"), "Conservar sin cambios");
        var changed = Path.Combine(source, "changed.txt"); await File.WriteAllTextAsync(changed, "primero");
        var job = new BackupJob { Name = "Retención", Incremental = true, Sources = [source], Destination = Path.Combine(root, "retention-destination") };
        var first = await BackupEngine.Run(job, new()); await File.WriteAllTextAsync(changed, "segundo"); var second = await BackupEngine.Run(job, new());
        var m1 = await BackupArchive.ReadManifest(first.Artifact, default); var m2 = await BackupArchive.ReadManifest(second.Artifact, default);
        var repo = IncrementalStore.Repository(second.Artifact, m2);
        m1.Created = DateTimeOffset.Now.AddDays(-30); Store.Write(first.Artifact, m1);
        job.RetentionDays = 20;
        await StoragePolicy.AfterPublish(job, repo, m2, null, default);
        check(!File.Exists(first.Artifact) && File.Exists(second.Artifact), "Retención elimina versión vencida y conserva la reciente");
        var restored = await BackupEngine.Restore(second.Artifact, Path.Combine(root, "retention-restore"), null, default);
        check(await File.ReadAllTextAsync(Path.Combine(restored, "01-retention-source", "shared.txt")) == "Conservar sin cambios" && await File.ReadAllTextAsync(Path.Combine(restored, "01-retention-source", "changed.txt")) == "segundo", "Última versión restaura archivos originales compartidos y modificados tras limpiar");
        check(Directory.GetFiles(Path.Combine(repo, "objects")).Length == 2 && Store.History().Single(r => r.Id == first.Id).Status == "Eliminado por retención", "Elimina solo objetos sin referencias y actualiza historial");
        await File.WriteAllTextAsync(changed, "tercero"); var third = await BackupEngine.Run(job, new()); var m3 = await BackupArchive.ReadManifest(third.Artifact, default);
        var pending = new BackupManifest { Version = 2, JobId = job.Id };
        // Simulated per-user free space tests the same decision logic without filling the host disk.
        await StoragePolicy.EnsureCapacity(job, repo, pending, 10, null, default, () => File.Exists(second.Artifact) ? 0 : StoragePolicy.Reserve + 10);
        check(!File.Exists(second.Artifact) && File.Exists(third.Artifact), "Espacio insuficiente limpia de más antiguo a reciente");
        bool rejected = false;
        try { await StoragePolicy.EnsureCapacity(job, repo, pending, 10, null, default, () => 0); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
        check(rejected && File.Exists(third.Artifact), "Cuota insuficiente nunca elimina la última versión");
        await File.WriteAllTextAsync(changed, "cuarto"); var fourth = await BackupEngine.Run(job, new());
        job.CleanWhenFull = false; rejected = false;
        try { await StoragePolicy.EnsureCapacity(job, repo, pending, 10, null, default, () => 0); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
        check(rejected && File.Exists(third.Artifact) && File.Exists(fourth.Artifact), "Opción detener y avisar no borra versiones por espacio");
        job.CleanWhenFull = true; job.StorageLimitBytes = 1; rejected = false;
        try { await StoragePolicy.EnsureCapacity(job, repo, pending, 10, null, default, () => long.MaxValue); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
        check(rejected && File.Exists(fourth.Artifact), "Límite manual opcional se respeta sin sacrificar última versión");
        job.StorageLimitBytes = 0; await File.WriteAllTextAsync(changed, "quinto"); var fifth = await BackupEngine.Run(job, new());
        var m5 = await BackupArchive.ReadManifest(fifth.Artifact, default);
        await File.WriteAllTextAsync(IncrementalStore.ObjectPath(repo, m5.Files.First()), "corrupto"); rejected = false;
        try { await StoragePolicy.EnsureCapacity(job, repo, pending, 10, null, default, () => 0); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
        check(rejected && File.Exists(fourth.Artifact) && File.Exists(fifth.Artifact), "Corrupción impide limpiar cualquier versión");
        var inflight = new BackupJob { Name = "Protección en curso", Incremental = true, Sources = [source], Destination = Path.Combine(root, "inflight-destination") };
        var a = await BackupEngine.Run(inflight, new()); var ma = await BackupArchive.ReadManifest(a.Artifact, default);
        await File.WriteAllTextAsync(changed, "otra versión"); var b = await BackupEngine.Run(inflight, new()); var mb = await BackupArchive.ReadManifest(b.Artifact, default);
        var otherRepo = IncrementalStore.Repository(b.Artifact, mb);
        await StoragePolicy.EnsureCapacity(inflight, otherRepo, ma, 10, null, default, () => File.Exists(a.Artifact) ? 0 : StoragePolicy.Reserve + 10);
        check(!File.Exists(a.Artifact) && ma.Files.All(f => File.Exists(IncrementalStore.ObjectPath(otherRepo, f))), "Limpieza protege objetos que necesita la copia en curso aunque retire su versión antigua");
        inflight.StorageLimitBytes = 1; await File.WriteAllTextAsync(changed, "nuevo contenido que no cabe");
        var quotaFailure = await BackupEngine.Run(inflight, new());
        check(quotaFailure.Status == "Error" && File.Exists(b.Artifact) && Directory.GetFiles(Path.Combine(otherRepo, "versions"), "*.pbi", SearchOption.AllDirectories).Length == 1, "Fallo real por límite registra error sin publicar copia parcial ni perder última versión");
        await File.WriteAllTextAsync(changed, "otra versión"); var unchangedQuotaFailure = await BackupEngine.Run(inflight, new());
        check(unchangedQuotaFailure.Status == "Error" && File.Exists(b.Artifact) && Directory.GetFiles(Path.Combine(otherRepo, "versions"), "*.pbi", SearchOption.AllDirectories).Length == 1, "Comprueba espacio para metadatos aunque ningún archivo haya cambiado");
        var lastRestore = await BackupEngine.Restore(b.Artifact, Path.Combine(root, "last-valid-restore"), null, default);
        check(await File.ReadAllTextAsync(Path.Combine(lastRestore, "01-retention-source", "changed.txt")) == "otra versión", "Última versión sigue restaurable después de fallar por cuota");
        check(StoragePolicy.Available(job.Destination) > 0, "Consulta real del espacio disponible para la cuenta local");
    }
}
