using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProBackUp.Central;
namespace ProBackUp.Server;
internal sealed partial class Database
{
    private readonly string connectionString;
    internal Database(string root)
    {
        Configuration.NoLinks(root); Directory.CreateDirectory(root);
        Configuration.NoLinks(Path.Combine(root, "reports.db")); Configuration.NoLinks(Path.Combine(root, "reports.db-wal")); Configuration.NoLinks(Path.Combine(root, "reports.db-shm"));
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "reports.db"), Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version"; if (Convert.ToInt32(command.ExecuteScalar()) > 2) throw new InvalidDataException("La base pertenece a una versión posterior del servidor.");
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS agents(id TEXT PRIMARY KEY, name TEXT NOT NULL, keyhash TEXT NOT NULL UNIQUE, revoked INTEGER NOT NULL DEFAULT 0, seen TEXT, machine TEXT, version TEXT);
            CREATE TABLE IF NOT EXISTS reports(agent TEXT NOT NULL REFERENCES agents(id), id TEXT NOT NULL, started TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(agent,id));
            CREATE INDEX IF NOT EXISTS reports_date ON reports(started DESC);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY, at TEXT NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS job_catalog(agent TEXT PRIMARY KEY REFERENCES agents(id), payload TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS commands(id TEXT PRIMARY KEY,agent TEXT NOT NULL REFERENCES agents(id),job TEXT NOT NULL,name TEXT NOT NULL,created TEXT NOT NULL,expires TEXT NOT NULL,state TEXT NOT NULL,record TEXT NOT NULL DEFAULT '',message TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS commands_agent ON commands(agent,created DESC);
            PRAGMA user_version=2;
            """; command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString); connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;"; command.ExecuteNonQuery(); return connection;
    }
    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    internal (string id, string token) Register(string name, string actor)
    {
        if (name.Length is < 1 or > 100) throw new ArgumentException("Nombre de equipo inválido.");
        var id = Guid.NewGuid().ToString("N"); var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO agents(id,name,keyhash) VALUES($id,$name,$hash)"; command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$hash", Hash(token)); command.ExecuteNonQuery();
        Audit(connection, transaction, actor, "Registró equipo " + id); transaction.Commit(); return (id, token);
    }
    internal string? Agent(string token)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT id FROM agents WHERE keyhash=$hash AND revoked=0"; command.Parameters.AddWithValue("$hash", Hash(token)); return command.ExecuteScalar() as string;
    }
    internal void Receive(string agent, AgentBatch batch)
    {
        if (batch.MachineName.Length is < 1 or > 128 || batch.Version.Length > 40 || batch.Reports.Count > 100) throw new ArgumentException("Lote inválido.");
        foreach (var report in batch.Reports) report.Validate();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "UPDATE agents SET seen=$seen,machine=$machine,version=$version WHERE id=$id AND revoked=0";
            command.Parameters.AddWithValue("$id", agent); command.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$machine", batch.MachineName); command.Parameters.AddWithValue("$version", batch.Version);
            if (command.ExecuteNonQuery() != 1) throw new UnauthorizedAccessException();
        }
        foreach (var report in batch.Reports)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO reports(agent,id,started,payload) VALUES($agent,$id,$started,$payload) ON CONFLICT(agent,id) DO UPDATE SET payload=excluded.payload,started=excluded.started WHERE json_extract(excluded.payload,'$.Updated') >= json_extract(reports.payload,'$.Updated')";
            command.Parameters.AddWithValue("$agent", agent); command.Parameters.AddWithValue("$id", report.Id); command.Parameters.AddWithValue("$started", report.Started.UtcDateTime.ToString("O")); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(report)); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    internal List<object> Agents()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT id,name,revoked,seen,machine,version,(SELECT json_extract(payload,'$.Finished') FROM reports WHERE reports.agent=agents.id AND json_extract(payload,'$.Status')='Correcto' ORDER BY julianday(json_extract(payload,'$.Finished')) DESC LIMIT 1) FROM agents ORDER BY name LIMIT 10000"; using var reader = command.ExecuteReader(); var result = new List<object>();
        while (reader.Read()) result.Add(new { id = reader.GetString(0), name = reader.GetString(1), revoked = reader.GetInt32(2) != 0, seen = reader.IsDBNull(3) ? null : reader.GetString(3), machine = reader.IsDBNull(4) ? null : reader.GetString(4), version = reader.IsDBNull(5) ? null : reader.GetString(5), lastBackup = reader.IsDBNull(6) ? null : reader.GetString(6) }); return result;
    }
    internal object Reports(string? agent, string? type, string? status, string? destination, string? user, DateTimeOffset? from, DateTimeOffset? to, int page)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reports.agent, agents.name, reports.payload FROM reports JOIN agents ON agents.id=reports.agent
            WHERE ($agent='' OR reports.agent=$agent) AND ($type='' OR json_extract(payload,'$.Format')=$type)
            AND ($status='' OR json_extract(payload,'$.Status')=$status) AND ($destination='' OR instr(lower(json_extract(payload,'$.Destination')),lower($destination))>0)
            AND ($user='' OR instr(lower(json_extract(payload,'$.User')),lower($user))>0)
            AND reports.started >= $from AND reports.started < $to ORDER BY reports.started DESC,reports.id LIMIT 101 OFFSET $offset
            """;
        command.Parameters.AddWithValue("$agent", agent ?? ""); command.Parameters.AddWithValue("$type", type ?? ""); command.Parameters.AddWithValue("$status", status ?? ""); command.Parameters.AddWithValue("$destination", destination ?? ""); command.Parameters.AddWithValue("$user", user ?? ""); command.Parameters.AddWithValue("$from", (from ?? DateTimeOffset.MinValue).UtcDateTime.ToString("O")); command.Parameters.AddWithValue("$to", (to ?? DateTimeOffset.MaxValue).UtcDateTime.ToString("O")); command.Parameters.AddWithValue("$offset", Math.Clamp(page, 0, 100000) * 100);
        using var reader = command.ExecuteReader(); var result = new List<object>();
        while (reader.Read()) result.Add(new { agent = reader.GetString(0), name = reader.GetString(1), report = JsonSerializer.Deserialize<CentralReport>(reader.GetString(2)) });
        bool more = result.Count > 100; return new { rows = result.Take(100), more };
    }
    internal bool Rename(string id, string name, string actor)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("Nombre de equipo inválido.");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE agents SET name=$name WHERE id=$id"; command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) return false;
        Audit(connection, transaction, actor, "Renombró equipo " + id + " a " + name); transaction.Commit(); return true;
    }
    internal bool Delete(string id, string actor)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM reports WHERE agent=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM commands WHERE agent=$id"; command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM job_catalog WHERE agent=$id"; command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM agents WHERE id=$id";
        if (command.ExecuteNonQuery() != 1) return false;
        Audit(connection, transaction, actor, "Eliminó equipo y reportes centrales " + id); transaction.Commit(); return true;
    }
    internal void Revoke(string id, string actor)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE agents SET revoked=1 WHERE id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); command.CommandText = "UPDATE commands SET state='Cancelado',message='Clave del cliente revocada' WHERE agent=$id AND state IN ('Pendiente','Esperando sesión')"; command.ExecuteNonQuery(); Audit(connection, transaction, actor, "Revocó equipo " + id); transaction.Commit();
    }
    private static void Audit(SqliteConnection connection, SqliteTransaction? transaction, string actor, string action)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO audit(at,actor,action) VALUES($at,$actor,$action)";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$actor", actor[..Math.Min(256, actor.Length)]); command.Parameters.AddWithValue("$action", action[..Math.Min(300, action.Length)]); command.ExecuteNonQuery();
    }
    internal void Audit(string actor, string action) { using var connection = Open(); Audit(connection, null, actor, action); }
    internal object AuditLog()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT at,actor,action FROM audit ORDER BY id DESC LIMIT 200"; using var reader = command.ExecuteReader(); var rows = new List<object>(); while (reader.Read()) rows.Add(new { at = reader.GetString(0), actor = reader.GetString(1), action = reader.GetString(2) }); return rows;
    }
    internal void Backup(string file) { Configuration.NoLinks(file); using var source = Open(); using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString()); target.Open(); source.BackupDatabase(target); }
}
