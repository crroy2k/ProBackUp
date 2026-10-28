using System.IO.Compression;
using System.Xml.Linq;
using ProBackUp.Central;
namespace ProBackUp.Server;
internal static class ReportExcelTests
{
    internal static void Run(string root, string samplePath, Action<bool, string> check)
    {
        // Isolated synthetic database, never the installed server's history.
        var db = new Database(Path.Combine(root, "excel-tests"));
        var agent = db.Register("Equipo Á", "test").id;
        var other = db.Register("Otro equipo", "test").id;
        var start = DateTimeOffset.Parse("2020-01-02T01:02:03Z");
        var job = Guid.NewGuid().ToString("N");
        var records = Enumerable.Range(0, 137).Select(i => new CentralReport { Id = Guid.NewGuid().ToString("N"), JobId = job, JobName = "=1+1 📁", User = "usuario@example.test", Format = "Incremental", Status = "Correcto", Started = start.AddSeconds(i), Finished = start.AddSeconds(i + 1), Bytes = 21L * 1073741824, CopiedBytes = 314572800, Files = 8, Destination = @"\\nas\respaldo", Artifact = @"\\nas\respaldo\versión.pbi", Error = "Texto & <prueba>\u0001" }).ToList();
        foreach (var batch in records.Chunk(100)) db.Receive(agent, new AgentBatch { MachineName = "PC-EXPORT", Version = "test", Reports = batch.ToList() });
        db.Receive(other, new AgentBatch { MachineName = "OTRO", Version = "test", Reports = [new CentralReport { Id = Guid.NewGuid().ToString("N"), JobId = job, JobName = "Otra tarea", Format = "FullFolder", Status = "Error", Started = start, Finished = start }] });
        var rows = db.ReadReports(agent, "Incremental", "Correcto", "NAS", "usuario", start, start.AddSeconds(137));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using (var export = ReportExcel.Create(rows, root, new Dictionary<string, string> { ["Equipo"] = "Equipo Á" }, CancellationToken.None).Stream)
        {
            using var zip = new ZipArchive(export, ZipArchiveMode.Read, true);
            var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var cells = sheet.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
            check(sheet.Descendants(ns + "row").Count() == 138, "Excel exporta 137 coincidencias, sin limite de pagina ni equipos ajenos");
            check(cells["N2"].Element(ns + "v")!.Value == (21L * 1073741824).ToString() && cells["O2"].Element(ns + "v")!.Value == "314572800" && cells["L2"].Element(ns + "v")!.Value == "21", "Excel conserva cantidades numericas y distingue GB logicos de bytes nuevos");
            check(DateTime.FromOADate(double.Parse(cells["F2"].Element(ns + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture)) == new DateTime(2020, 1, 1, 19, 4, 19), "Excel usa fecha real en horario de Costa Rica al cambiar de dia UTC");
            check(!sheet.Descendants(ns + "f").Any() && cells["D2"].Attribute("t")!.Value == "inlineStr" && cells["D2"].Value == "=1+1 📁" && cells["A2"].Value == "Equipo Á" && cells["Q2"].Value == "Texto & <prueba>", "Excel conserva tildes y emoji como texto literal sin formulas ni controles XML invalidos");
            check(sheet.Descendants(ns + "pane").Any() && (string?)sheet.Root!.Element(ns + "autoFilter")?.Attribute("ref") == "A1:R138" && zip.GetEntry("xl/worksheets/sheet2.xml") != null, "Excel incluye encabezado fijo, filtros y criterios de exportacion");
            export.Position = 0; using var sample = File.Create(samplePath); export.CopyTo(sample);
        }
        using (var empty = ReportExcel.Create(db.ReadReports(agent, "FullZip", null, null, null, null, null), root, new Dictionary<string, string>(), CancellationToken.None).Stream)
        using (var zip = new ZipArchive(empty, ZipArchiveMode.Read)) check(XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).Descendants(ns + "row").Count() == 1, "Excel vacio sigue siendo un libro valido con encabezados");
        check(db.ReadReports(agent, null, null, null, null, start.AddSeconds(10), start.AddSeconds(20)).Count() == 10, "Consulta Excel respeta fecha desde y hasta exclusivo");
        bool cancelled = false;
        try { using var ignored = ReportExcel.Create(rows, root, new Dictionary<string, string>(), new CancellationToken(true)).Stream; } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && Directory.GetFiles(Path.Combine(root, "exports")).Length == 0, "Exportacion cancelada o finalizada no conserva archivos temporales");
    }
}
