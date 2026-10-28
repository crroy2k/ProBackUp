using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
namespace ProBackUp;
internal static class ClientUpdateTests
{
    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); } }
    internal static async Task Run(string root, Action<bool, string> check)
    {
        const string tag = "client-v9.0.0";
        var bytes = "MZ-installer-fixture"u8.ToArray(); var hash = Convert.ToHexString(SHA256.HashData(bytes));
        object Release(string name, bool preview = false) => new { tag_name = name, draft = false, prerelease = preview, assets = new[] { new { name = ClientUpdates.InstallerName, size = bytes.Length, browser_download_url = $"https://github.com/{ClientUpdates.Repository}/releases/download/{name}/{ClientUpdates.InstallerName}", digest = "sha256:" + hash }, new { name = "SHA256SUMS.txt", size = 100, browser_download_url = $"https://github.com/{ClientUpdates.Repository}/releases/download/{name}/SHA256SUMS.txt", digest = "" } } };
        string sums = hash + "  ProBackUp-Setup.exe\n";
        using var transport = new Transport(request => new(HttpStatusCode.OK) { Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(JsonSerializer.Serialize(new[] { Release("server-v99.0.0"), Release("client-v99.0.0", true), Release(tag) })) : request.RequestUri.AbsolutePath.EndsWith(".txt") ? new StringContent(sums) : new ByteArrayContent(bytes) });
        using var client = new HttpClient(transport);
        var latest = await ClientUpdates.Latest(client, default);
        check(latest?.Tag == tag && latest.Version == new Version(9, 0, 0), "Actualización selecciona cliente estable e ignora releases del servidor y previews");
        var cache = Path.Combine(root, "updates-test");
        var downloaded = await ClientUpdates.Download(client, latest!, cache, null, default);
        check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "Descarga del instalador comprueba SHA-256, tamaño y cabecera de ejecutable");
        File.Delete(downloaded); Directory.Delete(Path.GetDirectoryName(downloaded)!);
        sums = new string('0', 64) + "  ProBackUp-Setup.exe\n";
        bool rejected = false; try { await ClientUpdates.Download(client, latest! with { Digest = "" }, cache, null, default); } catch (IOException) { rejected = true; }
        check(rejected && !Directory.EnumerateFileSystemEntries(cache).Any(), "Descarga corrupta se rechaza y elimina sin ejecutar ni dejar instalador parcial");
        rejected = false; try { ClientUpdates.AssetUri("http://evil.test/install.exe", tag, ClientUpdates.InstallerName); } catch (IOException) { rejected = true; }
        check(rejected, "Actualización rechaza URLs ajenas al repositorio y conexiones sin HTTPS");
        rejected = false; try { ClientUpdates.ExpectedHash(hash + "  ProBackUp-Setup.exe\n" + hash + "  ProBackUp-Setup.exe"); } catch (IOException) { rejected = true; }
        check(rejected, "Manifiesto de huellas duplicado se rechaza");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancellation = false; try { await ClientUpdates.Latest(client, cancelled.Token); } catch (OperationCanceledException) { cancellation = true; }
        check(cancellation, "Búsqueda de actualización permite cancelar");
        check(!ProBackUp.Installation.ClientInstallation.Elevated, "Pruebas de cliente ejecutadas sin privilegios de administrador");
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        check(ProBackUp.Installation.ClientInstallation.DesktopSid() == current.User?.Value, "Instalación identifica al usuario real del escritorio mediante su token de Windows");
    }
}
