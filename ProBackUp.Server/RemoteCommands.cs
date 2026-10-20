using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProBackUp.Central;
namespace ProBackUp.Server;
internal sealed partial class Database
{
    internal List<RemoteJob> Jobs(string agent)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM job_catalog WHERE agent=$id"; command.Parameters.AddWithValue("$id", agent);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<List<RemoteJob>>(json)! : [];
    }
    private static void Expire(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE commands SET state='Caducado',message='El cliente no inició la orden dentro de 24 horas' WHERE state='Pendiente' AND expires <= $now"; command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }
    internal string RequestBackup(string agent, string job, string actor)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); Expire(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM job_catalog JOIN agents ON agents.id=job_catalog.agent WHERE agents.id=$agent AND revoked=0"; command.Parameters.AddWithValue("$agent", agent);
        var jobs = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<List<RemoteJob>>(json)! : [];
        var selected = jobs.SingleOrDefault(j => j.Id == job) ?? throw new ArgumentException("El cliente no tiene esa tarea o necesita actualizarse.");
        command.CommandText = "SELECT id FROM commands WHERE agent=$agent AND job=$job AND state IN ('Pendiente','En curso','Esperando sesión') LIMIT 1"; command.Parameters.AddWithValue("$job", job);
        if (command.ExecuteScalar() is string existing) { transaction.Commit(); return existing; }
        var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
        command.CommandText = "INSERT INTO commands(id,agent,job,name,created,expires,state) VALUES($id,$agent,$job,$name,$created,$expires,'Pendiente')";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", selected.Name); command.Parameters.AddWithValue("$created", now.ToString("O")); command.Parameters.AddWithValue("$expires", now.AddHours(24).ToString("O")); command.ExecuteNonQuery();
        Audit(connection, transaction, actor, "Solicitó respaldo " + id + " equipo " + agent + " tarea " + job); transaction.Commit(); return id;
    }
    internal RemoteReply Sync(string agent, RemoteSync sync)
    {
        if (sync.Jobs.Count > 200 || sync.Results.Count > 100 || sync.Jobs.Select(j => j.Id).Distinct().Count() != sync.Jobs.Count) throw new ArgumentException("Sincronización inválida.");
        foreach (var job in sync.Jobs) job.Validate();
        foreach (var result in sync.Results) if (!Guid.TryParseExact(result.Id, "N", out _) || (result.RecordId != "" && result.RecordId != result.Id) || result.Message.Length > 512 || result.State is not ("En curso" or "Correcto" or "Error" or "Cancelado" or "Rechazado" or "Esperando sesión")) throw new ArgumentException("Resultado inválido.");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM agents WHERE id=$agent AND revoked=0"; command.Parameters.AddWithValue("$agent", agent); if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new UnauthorizedAccessException();
        command.CommandText = "INSERT INTO job_catalog(agent,payload,updated) VALUES($agent,$payload,$now) ON CONFLICT(agent) DO UPDATE SET payload=excluded.payload,updated=excluded.updated"; command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(sync.Jobs)); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
        foreach (var result in sync.Results)
        {
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE commands SET state=$state,record=$record,message=$message WHERE id=$id AND agent=$agent AND state IN ('Pendiente','En curso','Esperando sesión') AND (state<>'En curso' OR $state<>'Esperando sesión')";
            update.Parameters.AddWithValue("$state", result.State); update.Parameters.AddWithValue("$record", result.RecordId); update.Parameters.AddWithValue("$message", result.Message); update.Parameters.AddWithValue("$id", result.Id); update.Parameters.AddWithValue("$agent", agent); update.ExecuteNonQuery();
        }
        Expire(connection, transaction);
        command.CommandText = "SELECT id,job,expires,state FROM commands WHERE agent=$agent AND state IN ('Pendiente','Esperando sesión') ORDER BY created LIMIT 200";
        var reply = new RemoteReply(); using (var reader = command.ExecuteReader()) while (reader.Read()) reply.Commands.Add(new() { Id = reader.GetString(0), JobId = reader.GetString(1), Expires = reader.GetString(3) == "Esperando sesión" ? DateTimeOffset.MaxValue : DateTimeOffset.Parse(reader.GetString(2)) });
        transaction.Commit(); return reply;
    }
    internal List<object> Commands(string agent)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); Expire(connection, transaction); transaction.Commit(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,created,expires,state,record,message FROM commands WHERE agent=$agent ORDER BY created DESC LIMIT 50"; command.Parameters.AddWithValue("$agent", agent);
        using var reader = command.ExecuteReader(); var rows = new List<object>();
        while (reader.Read()) rows.Add(new { id = reader.GetString(0), name = reader.GetString(1), created = reader.GetString(2), expires = reader.GetString(3), state = reader.GetString(4), recordId = reader.GetString(5), message = reader.GetString(6) }); return rows;
    }
}
