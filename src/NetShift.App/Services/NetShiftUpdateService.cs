using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetShift.App.Services;

public sealed record NetShiftUpdate(string Tag, string Version, string ArchiveName, string ArchiveUrl, string ChecksumUrl, long ArchiveSize, string? ApiSha256);
public static class NetShiftUpdateService
{
    private const long MaxArchiveBytes = 400L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetShift/1.0 (Windows desktop)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static string CurrentVersion
    {
        get
        {
            var informationalVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var versionMatch = Regex.Match(
                informationalVersion ?? string.Empty,
                "^(?<version>\\d+\\.\\d+\\.\\d+)",
                RegexOptions.CultureInvariant);
            if (versionMatch.Success) return versionMatch.Groups["version"].Value;

            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version?.ToString(3) ?? "0.3.007";
        }
    }

    public static string? Repository
    {
        get
        {
            var metadata = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key.Equals("NetShiftUpdateRepository", StringComparison.Ordinal));
            var value = metadata?.Value?.Trim();
            return value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)
                ? value
                : null;
        }
    }

    public static async Task<NetShiftUpdate?> CheckLatestAsync(CancellationToken cancellationToken)
    {
        var repository = Repository ?? throw new InvalidOperationException("В этой сборке не задан GitHub-репозиторий NetShift.");
        var apiUrl = $"https://api.github.com/repos/{repository}/releases/latest";
        using var response = await Client.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tag = RequiredString(root, "tag_name");
        var latestVersion = ParseVersion(tag);
        if (latestVersion <= ParseVersion(CurrentVersion)) return null;

        var archiveName = $"NetShift-{tag}-win-x64.zip";
        var checksumName = archiveName + ".sha256";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("В релизе NetShift не найден список файлов.");

        JsonElement archiveAsset = default;
        JsonElement checksumAsset = default;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = RequiredString(asset, "name");
            if (name.Equals(archiveName, StringComparison.OrdinalIgnoreCase)) archiveAsset = asset;
            else if (name.Equals(checksumName, StringComparison.OrdinalIgnoreCase)) checksumAsset = asset;
        }
        if (archiveAsset.ValueKind == JsonValueKind.Undefined || checksumAsset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"В релизе {tag} не найден архив приложения или файл контрольной суммы.");

        var archiveUrl = ValidateReleaseAssetUrl(RequiredString(archiveAsset, "browser_download_url"), repository, tag);
        var checksumUrl = ValidateReleaseAssetUrl(RequiredString(checksumAsset, "browser_download_url"), repository, tag);
        var archiveSize = archiveAsset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size) ? size : 0;
        if (archiveSize <= 0 || archiveSize > MaxArchiveBytes)
            throw new InvalidDataException("Размер ZIP-архива NetShift недопустим.");

        string? apiSha256 = null;
        if (archiveAsset.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String)
        {
            var digestMatch = Regex.Match(digestElement.GetString() ?? string.Empty, "^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);
            if (digestMatch.Success) apiSha256 = digestMatch.Groups[1].Value.ToLowerInvariant();
        }

        var versionText = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
        return new NetShiftUpdate(tag, versionText, archiveName, archiveUrl, checksumUrl, archiveSize, apiSha256);
    }

    public static async Task<string> DownloadAndExtractAsync(NetShiftUpdate update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var checksumText = await Client.GetStringAsync(update.ChecksumUrl, cancellationToken);
        if (checksumText.Length > 4096)
            throw new InvalidDataException("Файл контрольной суммы NetShift неожиданно велик.");
        var checksumMatch = Regex.Match(checksumText.Trim(), $"^(?<hash>[0-9a-fA-F]{{64}})\\s+\\*?{Regex.Escape(update.ArchiveName)}$", RegexOptions.CultureInvariant);
        if (!checksumMatch.Success)
            throw new InvalidDataException("Файл контрольной суммы не соответствует ZIP-архиву NetShift.");
        var expectedHash = checksumMatch.Groups["hash"].Value.ToLowerInvariant();
        if (update.ApiSha256 is not null && !update.ApiSha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольные суммы из GitHub API и файла релиза не совпадают.");

        var safeTag = Regex.Replace(update.Tag, "[^A-Za-z0-9._-]", "_");
        var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "Updates", safeTag);
        Directory.CreateDirectory(updateDirectory);
        var archivePath = Path.Combine(updateDirectory, update.ArchiveName);
        var temporaryArchivePath = archivePath + ".download";
        var stagedExecutablePath = Path.Combine(updateDirectory, "NetShift.exe");

        try
        {
            using var response = await Client.GetAsync(update.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != update.ArchiveSize)
                throw new InvalidDataException("Размер ZIP-архива не совпадает с данными GitHub.");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporaryArchivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes || total > update.ArchiveSize)
                        throw new InvalidDataException("Загруженный ZIP-архив превышает объявленный размер.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report((double)total / update.ArchiveSize);
                }
                if (total != update.ArchiveSize)
                    throw new InvalidDataException("Загруженный ZIP-архив имеет неполный размер.");
            }

            await using (var archiveStream = File.OpenRead(temporaryArchivePath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 загруженного ZIP-архива NetShift не совпал.");
            }

            using (var zip = ZipFile.OpenRead(temporaryArchivePath))
            {
                if (zip.Entries.Count != 1 || !zip.Entries[0].FullName.Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase) ||
                    zip.Entries[0].Length <= 0 || zip.Entries[0].Length > MaxArchiveBytes)
                    throw new InvalidDataException("ZIP-архив должен содержать только NetShift.exe.");

                await using var entryStream = zip.Entries[0].Open();
                await using var executableStream = new FileStream(stagedExecutablePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await entryStream.CopyToAsync(executableStream, cancellationToken);
            }

            File.Delete(temporaryArchivePath);
            progress?.Report(1);
            return stagedExecutablePath;
        }
        catch
        {
            if (File.Exists(temporaryArchivePath)) File.Delete(temporaryArchivePath);
            if (File.Exists(stagedExecutablePath)) File.Delete(stagedExecutablePath);
            throw;
        }
    }

    public static void LaunchUpdater(string stagedExecutablePath, string targetExecutablePath, int processId)
    {
        var updatesRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "Updates")) + Path.DirectorySeparatorChar;
        var source = Path.GetFullPath(stagedExecutablePath);
        var target = Path.GetFullPath(targetExecutablePath);
        if (!source.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(source).Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(source) ||
            !Path.GetFileName(target).Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target) || processId <= 0)
            throw new InvalidOperationException("Не удалось проверить пути для установки обновления NetShift.");

        var workingDirectory = Path.GetDirectoryName(target)!;
        var backup = target + ".update-backup";
        var logPath = Path.Combine(Path.GetDirectoryName(source)!, "update.log");
        var script = "$ErrorActionPreference = 'Stop'; " +
                     $"$source = {PsLiteral(source)}; $target = {PsLiteral(target)}; $backup = {PsLiteral(backup)}; " +
                     $"$workingDirectory = {PsLiteral(workingDirectory)}; $logPath = {PsLiteral(logPath)}; " +
                     $"$parentId = {processId}; " +
                     "try { " +
                     "Wait-Process -Id $parentId -Timeout 120 -ErrorAction SilentlyContinue; " +
                     "if (Get-Process -Id $parentId -ErrorAction SilentlyContinue) { throw 'NetShift не завершился за отведённое время.' }; " +
                     "if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Файл обновления не найден.' }; " +
                     "if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }; " +
                     "Move-Item -LiteralPath $target -Destination $backup -Force; " +
                     "try { Move-Item -LiteralPath $source -Destination $target -Force } catch { Move-Item -LiteralPath $backup -Destination $target -Force; throw }; " +
                     "Start-Process -FilePath $target -WorkingDirectory $workingDirectory; " +
                     "Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue; " +
                     "} catch { " +
                     "if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target -Force }; " +
                     "[IO.File]::WriteAllText($logPath, $_.Exception.Message); " +
                     "try { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show(('Не удалось обновить NetShift: ' + $_.Exception.Message), 'NetShift') | Out-Null } catch {}; " +
                     "}";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory
        };
        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Не удалось запустить установщик обновления NetShift.");
    }

    private static string ValidateReleaseAssetUrl(string value, string repository, string tag)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{repository}/releases/download/{tag}/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ссылка на обновление ведёт не на GitHub-релиз NetShift.");
        return uri.AbsoluteUri;
    }

    private static Version ParseVersion(string value)
    {
        var match = Regex.Match(value.Trim(), "^v?(?<version>\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?)(?:[-+].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version))
            throw new InvalidDataException($"Версия NetShift «{value}» имеет неверный формат.");
        return version;
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"GitHub API не вернул поле {propertyName}.");
        return property.GetString()!;
    }

    private static string PsLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
