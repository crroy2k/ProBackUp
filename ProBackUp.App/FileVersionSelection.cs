namespace ProBackUp;

// Keep the user's file identity independent of DataGridView's automatic row selection.
public sealed class FileVersionSelection
{
    private readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
    public string? Wanted { get; private set; }
    public string? Selected => Wanted != null && paths.TryGetValue(Wanted, out var actual) ? actual : null;
    public void Choose(string path) => Wanted = path;
    public void ClearSnapshot() => paths.Clear();
    public void Load(IEnumerable<FileEntry> files)
    {
        paths.Clear(); foreach (var file in files) paths.Add(file.Path, file.Path);
        Wanted ??= paths.Values.FirstOrDefault();
    }
}
