namespace ProBackUp;
public static class BackupFileFilter
{
    public static List<FileEntry> Apply(IEnumerable<FileEntry> files, string query, string extensions)
    {
        string search = query.Trim();
        var allowed = extensions.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.StartsWith("*.", StringComparison.Ordinal) ? e[1..] : e == "*" ? e : e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool all = allowed.Count == 0 || allowed.Contains("*");
        return files.Where(f => (search.Length == 0 || f.Path.Contains(search, StringComparison.OrdinalIgnoreCase)) && (all || allowed.Contains(Path.GetExtension(f.Path)))).ToList();
    }
}
