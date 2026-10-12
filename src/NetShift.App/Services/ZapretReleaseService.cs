using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetShift.App.Services;

public sealed record ZapretRelease(string Tag, string Version, string ArchivePath, string Sha256);
public sealed record ZapretReleaseInfo(string Tag, string Version);

public static class ZapretReleaseService
{
    private const string LatestApiUrl = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
    private const string IpSetUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
    private const string HostsUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";
    private const long MaxArchiveBytes = 100 * 1024 * 1024;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetShift/1.0 (Windows desktop)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<ZapretRelease> DownloadLatestAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var metadataResponse = await Client.GetAsync(LatestApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        metadataResponse.EnsureSuccessStatusCode();
        await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(metadataStream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tag = RequiredString(root, "tag_name");
        if (!Regex.IsMatch(tag, "^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("GitHub вернул некорректный тег релиза.");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("В последнем релизе GitHub нет списка файлов.");

        JsonElement selectedAsset = default;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = RequiredString(asset, "name");
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                name.StartsWith("zapret-discord-youtube-", StringComparison.OrdinalIgnoreCase))
            {
                selectedAsset = asset;
                break;
            }
        }

        if (selectedAsset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("В последнем релизе не найден ZIP-архив zapret-discord-youtube.");

        var downloadUrl = new Uri(RequiredString(selectedAsset, "browser_download_url"));
        if (downloadUrl.Scheme != Uri.UriSchemeHttps || downloadUrl.Host != "github.com" ||
            !downloadUrl.AbsolutePath.StartsWith("/Flowseal/zapret-discord-youtube/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ссылка на архив ведёт не на официальный GitHub-релиз Flowseal.");

        var expectedDigest = selectedAsset.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String
            ? digestElement.GetString()
            : null;
        var digestMatch = expectedDigest is null ? Match.Empty : Regex.Match(expectedDigest, "^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);
        if (!digestMatch.Success)
            throw new InvalidDataException("GitHub API не предоставил SHA-256 для ZIP-архива; загрузка отменена.");

        var expectedBytes = selectedAsset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size) ? size : 0;
        if (expectedBytes <= 0 || expectedBytes > MaxArchiveBytes)
            throw new InvalidDataException("Размер архива в GitHub API некорректен или превышает 100 МБ.");

        var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "Downloads");
        Directory.CreateDirectory(cacheDirectory);
        var archivePath = Path.Combine(cacheDirectory, $"{tag}-{Guid.NewGuid():N}.zip");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxArchiveBytes)
                throw new InvalidDataException("Архив превышает допустимый размер 100 МБ.");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes || total > expectedBytes)
                        throw new InvalidDataException("Загруженный архив больше размера, объявленного GitHub.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(Math.Min(99, total * 100d / expectedBytes));
                }
                if (total != expectedBytes)
                    throw new InvalidDataException("Размер загруженного архива не совпадает с размером в GitHub API.");
            }

            await using var archiveStream = File.OpenRead(archivePath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken));
            if (!actualHash.Equals(digestMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 архива не совпал с контрольной суммой GitHub.");

            progress?.Report(100);
            return new ZapretRelease(tag, tag.TrimStart('v', 'V'), archivePath, actualHash);
        }
        catch
        {
            try { File.Delete(archivePath); } catch { }
            throw;
        }
    }

    public static async Task<ZapretReleaseInfo> GetLatestReleaseInfoAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(LatestApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = RequiredString(root, "tag_name");
        var releaseUrl = RequiredString(root, "html_url");
        var uri = new Uri(releaseUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/Flowseal/zapret-discord-youtube/releases/tag/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ссылка на страницу релиза не принадлежит официальному репозиторию.");
        return new ZapretReleaseInfo(tag, tag.TrimStart('v', 'V'));
    }

    public static async Task<string> DownloadIpSetAsync(CancellationToken cancellationToken) =>
        await DownloadOfficialTextAsync(IpSetUrl, 20 * 1024 * 1024, cancellationToken);

    public static async Task<string> DownloadHostsAsync(CancellationToken cancellationToken) =>
        await DownloadOfficialTextAsync(HostsUrl, 2 * 1024 * 1024, cancellationToken);

    private static async Task<string> DownloadOfficialTextAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        var bytes = await Client.GetByteArrayAsync(url, cancellationToken);
        if (bytes.Length == 0 || bytes.Length > maxBytes)
            throw new InvalidDataException("Файл со списком пуст или превышает допустимый размер.");
        var content = new System.Text.UTF8Encoding(false, true).GetString(bytes);
        if (content.Contains("<html", StringComparison.OrdinalIgnoreCase) || content.Contains("404: Not Found", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub вернул страницу ошибки вместо файла списка.");
        return content;
    }

    public static string CacheText(string fileName, string content)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "Downloads");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"GitHub API не вернул поле {name}.");
        return property.GetString()!;
    }
}
