using System.Text.Json;
namespace ProBackUp;
internal static class JobConfiguration
{
    internal static async Task Apply(BackupJob job)
    {
        BackupEngine.ValidateDefinition(job); await Scheduler.Apply(job);
        using var gate = Store.Acquire(); var settings = Store.Load();
        // Keep the active configuration independent from the editor's next draft.
        var applied = JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(job))!;
        var index = settings.Jobs.FindIndex(j => j.Id == job.Id);
        if (index < 0) settings.Jobs.Add(applied); else settings.Jobs[index] = applied;
        Store.Save(settings);
    }
}
