using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ProBackUp.Server;

internal static class ReportExcel
{
    internal const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    internal static readonly SemaphoreSlim Gate = new(1);
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly string[] Headers = ["Equipo", "Máquina", "Usuario", "Tarea", "Tipo", "Inicio (Costa Rica)", "Fin (Costa Rica)", "Duración (segundos)", "Destino", "Resultado", "Archivos", "Versión completa (GB)", "Datos nuevos escritos (GB)", "Versión completa (bytes)", "Datos nuevos escritos (bytes)", "Ubicación del respaldo", "Detalle del error", "ID de ejecución"];
    private static readonly double[] Widths = [24, 22, 30, 30, 25, 23, 23, 22, 58, 28, 14, 25, 28, 28, 30, 58, 65, 36];
    internal sealed record Export(FileStream Stream, int Rows);

    internal static DateTime Local(DateTimeOffset date) => date.ToOffset(TimeSpan.FromHours(-6)).DateTime;
    internal static Export Create(IEnumerable<Database.ReportRow> reports, string root, IReadOnlyDictionary<string, string> filters, CancellationToken token)
    {
        var folder = Path.Combine(root, "exports"); Configuration.NoLinks(folder); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".xlsx"); Configuration.NoLinks(path);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
        try
        {
            int count = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                Part(zip, "[Content_Types].xml", """
                    <?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                    """);
                Part(zip, "_rels/.rels", """
                    <?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                    """);
                Part(zip, "xl/workbook.xml", """
                    <?xml version="1.0" encoding="utf-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Respaldos" sheetId="1" r:id="rId1"/><sheet name="Filtros" sheetId="2" r:id="rId2"/></sheets></workbook>
                    """);
                Part(zip, "xl/_rels/workbook.xml.rels", """
                    <?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                    """);
                Part(zip, "xl/styles.xml", """
                    <?xml version="1.0" encoding="utf-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm:ss"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF17365D"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="5"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="3" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="2" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
                    """);
                using (var x = Sheet(zip, "xl/worksheets/sheet1.xml", Widths))
                {
                    Row(x, 1, Headers.Select(h => (object)h).ToArray(), true);
                    foreach (var item in reports)
                    {
                        token.ThrowIfCancellationRequested();
                        if (count >= 1_048_575) throw new ArgumentException("El resultado supera el máximo de filas de Excel. Reduce el rango de fechas o filtra por equipo.");
                        var r = item.Report;
                        Row(x, ++count + 1, [item.Name, item.Machine, r.User, r.JobName, Kind(r.Format), Local(r.Started), Local(r.Finished), Math.Max(0, (r.Finished - r.Started).TotalSeconds), r.Destination, r.Status, r.Files, r.Bytes / 1073741824d, r.CopiedBytes / 1073741824d, r.Bytes, r.CopiedBytes, r.Artifact, r.Error, r.Id]);
                    }
                    x.WriteEndElement(); // sheetData
                    x.WriteStartElement("autoFilter", Ns); x.WriteAttributeString("ref", "A1:R" + (count + 1)); x.WriteEndElement();
                    x.WriteEndElement();
                }
                using (var x = Sheet(zip, "xl/worksheets/sheet2.xml", [34, 90]))
                {
                    Row(x, 1, ["Criterio", "Valor"], true); int row = 2;
                    Row(x, row++, ["Exportado (Costa Rica)", Local(DateTimeOffset.UtcNow)]);
                    Row(x, row++, ["Registros", count]);
                    foreach (var pair in filters) Row(x, row++, [pair.Key, pair.Value]);
                    Row(x, row, ["Cantidades", "GB = bytes / 1024³. Datos nuevos no equivale al tamaño total ni al tráfico de red."]);
                    x.WriteEndElement(); x.WriteEndElement();
                }
            }
            stream.Position = 0; return new Export(stream, count);
        }
        catch { stream.Dispose(); throw; }
    }
    private static string Kind(string value) => value switch { "FullZip" => "Completo ZIP", "FullFolder" => "Completo en carpeta", "Incremental" => "Incremental", _ => value };
    private static void Part(ZipArchive zip, string path, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Fastest).Open(), new UTF8Encoding(false)); writer.Write(xml);
    }
    private static XmlWriter Sheet(ZipArchive zip, string path, double[] widths)
    {
        var x = XmlWriter.Create(zip.CreateEntry(path, CompressionLevel.Fastest).Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true });
        x.WriteStartDocument(); x.WriteStartElement("worksheet", Ns);
        x.WriteStartElement("sheetViews", Ns); x.WriteStartElement("sheetView", Ns); x.WriteAttributeString("workbookViewId", "0");
        x.WriteStartElement("pane", Ns); x.WriteAttributeString("ySplit", "1"); x.WriteAttributeString("topLeftCell", "A2"); x.WriteAttributeString("activePane", "bottomLeft"); x.WriteAttributeString("state", "frozen"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("cols", Ns);
        for (int i = 0; i < widths.Length; i++) { x.WriteStartElement("col", Ns); x.WriteAttributeString("min", (i + 1).ToString()); x.WriteAttributeString("max", (i + 1).ToString()); x.WriteAttributeString("width", widths[i].ToString(CultureInfo.InvariantCulture)); x.WriteAttributeString("customWidth", "1"); x.WriteEndElement(); }
        x.WriteEndElement(); x.WriteStartElement("sheetData", Ns); return x;
    }
    private static void Row(XmlWriter x, int number, object[] cells, bool header = false)
    {
        x.WriteStartElement("row", Ns); x.WriteAttributeString("r", number.ToString());
        for (int i = 0; i < cells.Length; i++)
        {
            var value = cells[i]; int style = header ? 1 : value is DateTime ? 2 : value is int or long ? 3 : value is double ? 4 : 0;
            x.WriteStartElement("c", Ns); x.WriteAttributeString("r", ((char)('A' + i)).ToString() + number); x.WriteAttributeString("s", style.ToString());
            if (!header && value is DateTime date) x.WriteElementString("v", Ns, date.ToOADate().ToString("R", CultureInfo.InvariantCulture));
            else if (!header && value is int or long or double) x.WriteElementString("v", Ns, Convert.ToString(value, CultureInfo.InvariantCulture));
            else
            {
                x.WriteAttributeString("t", "inlineStr"); x.WriteStartElement("is", Ns); x.WriteStartElement("t", Ns); x.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                var text = new StringBuilder();
                foreach (var rune in (value?.ToString() ?? "").EnumerateRunes()) if (rune.Value is 9 or 10 or 13 or >= 32 and <= 0xD7FF or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF) text.Append(rune.ToString());
                x.WriteString(text.ToString()); x.WriteEndElement(); x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }
}
