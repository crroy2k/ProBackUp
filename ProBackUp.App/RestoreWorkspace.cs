using ProBackUp.Security;

namespace ProBackUp;
internal sealed class RestoreWorkspace : IDisposable
{
    private readonly DirectoryGuard parents = new();
    private readonly DirectoryGuard directories = new();
    private readonly List<string> files = [];
    private readonly string staging, final;
    private bool published, releasedForPublish;
    internal RestoreWorkspace(string destination, string finalName)
    {
        final = Path.Combine(destination, finalName);
        staging = final + ".partial";
        try { parents.Create(destination); directories.Create(staging); }
        catch { Dispose(); throw; }
    }
    internal void Directory(string relative) => directories.Create(BackupEngine.SafePath(staging, relative));
    internal FileStream File(string relative)
    {
        var path = BackupEngine.SafePath(staging, relative);
        directories.Create(Path.GetDirectoryName(path)!);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        files.Add(path);
        return file;
    }
    internal Stream Read(string relative) => System.IO.File.OpenRead(BackupEngine.SafePath(staging, relative));
    internal string Publish()
    {
        // No target writes follow this rename. Destination ancestors stay pinned.
        releasedForPublish = true;
        directories.Dispose();
        System.IO.Directory.Move(staging, final);
        published = true;
        return final;
    }
    public void Dispose()
    {
        if (!published && !releasedForPublish)
        {
            // Delete only files created here and empty pinned directories. Never recurse into an untrusted replacement.
            foreach (var file in files) try { System.IO.File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var directory in directories.Descendants(staging))
            {
                directories.Release(directory);
                try { System.IO.Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        directories.Dispose(); parents.Dispose();
    }
}
