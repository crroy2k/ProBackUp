namespace ProBackUp.Central;
public sealed class CentralReport
{
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public string Id { get; set; } = "";
    public string JobId { get; set; } = "";
    public string JobName { get; set; } = "";
    public string User { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Artifact { get; set; } = "";
    public string Format { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Finished { get; set; }
    public long Bytes { get; set; }
    public long CopiedBytes { get; set; }
    public int Files { get; set; }
    public string Error { get; set; } = "";
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || !Guid.TryParseExact(JobId, "N", out _) ||
            JobName.Length is < 1 or > 200 || User.Length > 256 || Destination.Length > 2048 || Artifact.Length > 2048 || Error.Length > 4096 ||
            Format is not ("FullZip" or "FullFolder" or "Incremental") || Status is not ("Correcto" or "Error" or "Cancelado" or "Eliminado por retención") ||
            Bytes < 0 || CopiedBytes < 0 || Files < 0 || Started.Year < 2020 || Finished < Started || Finished > DateTimeOffset.UtcNow.AddDays(1))
            throw new ArgumentException("Reporte inválido.");
    }
}
public sealed class AgentBatch
{
    public string MachineName { get; set; } = "";
    public string Version { get; set; } = "";
    public List<CentralReport> Reports { get; set; } = [];
}

public sealed class RemoteJob
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Format { get; set; } = "";
    public string Destination { get; set; } = "";
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || Name.Length is < 1 or > 200 || Destination.Length > 2048 || Format is not ("FullZip" or "FullFolder" or "Incremental")) throw new ArgumentException("Tarea remota inválida.");
    }
}
public sealed class RemoteCommand
{
    public string Id { get; set; } = "";
    public string JobId { get; set; } = "";
    public DateTimeOffset Expires { get; set; }
}
public sealed class RemoteResult
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "";
    public string RecordId { get; set; } = "";
    public string Message { get; set; } = "";
}
public sealed class RemoteSync
{
    public List<RemoteJob> Jobs { get; set; } = [];
    public List<RemoteResult> Results { get; set; } = [];
}
public sealed class RemoteReply
{
    public List<RemoteCommand> Commands { get; set; } = [];
}
