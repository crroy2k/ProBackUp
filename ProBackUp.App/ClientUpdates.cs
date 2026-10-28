using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProBackUp;
internal sealed record ClientRelease(Version Version, string Tag, Uri Installer, Uri Checksums, long Size, string Digest);
internal static class ClientUpdates
{
    internal const string Repository = "crroy2k/ProBackUp";
    internal const string InstallerName = "ProBackUp-Setup.exe";
    internal static Version Current => typeof(Program).Assembly.GetName().Version!;
    internal static HttpClient Client()
    {
        var client = new HttpClient(new HttpClientHandler { CheckCertificateRevocationList = true }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProBackUp/" + Current);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    internal static Uri AssetUri(string url, string tag, string name)
    {
        var expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.AbsoluteUri != expected) throw new IOException("La publicación contiene una dirección de descarga no autorizada.");
        return uri;
    }
    internal static ClientRelease? Parse(JsonElement release)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith("client-v", StringComparison.Ordinal) || !Version.TryParse(tag[8..], out var version)) return null;
        var assets = release.GetProperty("assets").EnumerateArray().ToList();
        var installer = assets.Where(a => a.GetProperty("name").GetString() == InstallerName).ToList();
        var sums = assets.Where(a => a.GetProperty("name").GetString() == "SHA256SUMS.txt").ToList();
        if (installer.Count != 1 || sums.Count != 1) return null;
        long size = installer[0].GetProperty("size").GetInt64();
        if (size < 2 || size > 300L * 1024 * 1024) throw new IOException("Tamaño de instalador no admitido.");
        var digest = installer[0].TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        return new(version, tag, AssetUri(installer[0].GetProperty("browser_download_url").GetString()!, tag, InstallerName), AssetUri(sums[0].GetProperty("browser_download_url").GetString()!, tag, "SHA256SUMS.txt"), size, digest);
    }
    internal static async Task<ClientRelease?> Latest(HttpClient client, CancellationToken token)
    {
        // Skip server releases and previews; compare versions rather than publication dates.
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new IOException("GitHub limitó las consultas. Intenta nuevamente más tarde.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await Limited(response.Content, 2 * 1024 * 1024, token));
        return document.RootElement.EnumerateArray().Select(Parse).Where(r => r != null).OrderByDescending(r => r!.Version).FirstOrDefault();
    }
    private static async Task<byte[]> Limited(HttpContent content, int maximum, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var buffer = new byte[8192];
        int n; while ((n = await stream.ReadAsync(buffer, token)) > 0) { if (output.Length + n > maximum) throw new IOException("Respuesta de actualización demasiado grande."); output.Write(buffer, 0, n); }
        return output.ToArray();
    }
    internal static string ExpectedHash(string checksums)
    {
        var matches = checksums.Split('\n').Select(l => l.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length == 2 && p[1].TrimStart('*') == InstallerName).ToList();
        if (matches.Count != 1 || matches[0][0].Length != 64 || !matches[0][0].All(Uri.IsHexDigit)) throw new IOException("Falta un SHA-256 válido y único para el instalador.");
        return matches[0][0];
    }
    internal static async Task<string> Download(HttpClient client, ClientRelease release, string cache, IProgress<string>? progress, CancellationToken token)
    {
        AssetUri(release.Installer.AbsoluteUri, release.Tag, InstallerName); AssetUri(release.Checksums.AbsoluteUri, release.Tag, "SHA256SUMS.txt");
        using var checksum = await client.GetAsync(release.Checksums, HttpCompletionOption.ResponseHeadersRead, token); checksum.EnsureSuccessStatusCode();
        var expected = ExpectedHash(Encoding.UTF8.GetString(await Limited(checksum.Content, 65536, token)));
        if (release.Digest.Length > 0 && !release.Digest.Equals("sha256:" + expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("La huella de GitHub no coincide con el archivo de verificación.");
        BackupEngine.NoLinks(cache); Directory.CreateDirectory(cache);
        var directory = Path.Combine(cache, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path = Path.Combine(directory, InstallerName);
        try
        {
            using var response = await client.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; long total = 0; int n;
                while ((n = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += n; if (total > release.Size) throw new IOException("La descarga supera el tamaño publicado.");
                    hash.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), token);
                    progress?.Report($"Descargando actualización {release.Version}: {100 * total / release.Size}%");
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("El instalador no pasó la verificación SHA-256. No se ejecutó.");
            }
            using var binary = File.OpenRead(path); if (binary.ReadByte() != 'M' || binary.ReadByte() != 'Z') throw new IOException("El archivo descargado no es un ejecutable de Windows.");
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); throw; }
    }
    internal static Process StartInstaller(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }) ?? throw new IOException("No se pudo abrir el instalador.");
}
