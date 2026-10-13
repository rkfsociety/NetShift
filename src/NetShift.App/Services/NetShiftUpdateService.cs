using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetShift.App.Services;

public sealed record NetShiftUpdate(string Tag, string Version, string ExecutableName, string ExecutableUrl, long ExecutableSize, string Sha256);
public static class NetShiftUpdateService
{
    private const long MaxExecutableBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetShift/1.0 (Windows desktop)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "Updates");

    private static string UpdateLogPath => Path.Combine(UpdatesDirectory, "update.log");

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
            return version?.ToString(3) ?? "0.3.032";
        }
    }

    internal static string? GetPendingUpdateBackup(string[] args, string? executablePath)
    {
        if (args.Length != 2 || !args[0].Equals("--confirm-update", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(executablePath) ||
            !Path.GetFileName(executablePath).Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var executable = Path.GetFullPath(executablePath);
        var candidate = Path.GetFullPath(args[1]);
        var expectedPrefix = Path.GetFileName(executable) + ".update-backup-";
        var candidateName = Path.GetFileName(candidate);
        var suffix = candidateName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ? candidateName[expectedPrefix.Length..] : string.Empty;
        if (!Path.GetDirectoryName(candidate)!.Equals(Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(suffix, "N", out _) || !File.Exists(candidate))
            return null;
        return candidate;
    }

    internal static void ConfirmUpdateStartup(string? backupPath)
    {
        if (backupPath is null) return;
        try { File.Delete(backupPath); } catch { }
    }

    internal static string? GetPendingUpdateFailure(string[] args, string? logPath = null)
    {
        if (args.Length != 1 || !args[0].Equals("--update-failed", StringComparison.Ordinal))
            return null;

        var resolvedLogPath = logPath ?? UpdateLogPath;
        string message;
        try
        {
            message = File.Exists(resolvedLogPath) ? File.ReadAllText(resolvedLogPath).Trim() : string.Empty;
        }
        catch (IOException)
        {
            message = string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            message = string.Empty;
        }

        try { File.Delete(resolvedLogPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return string.IsNullOrWhiteSpace(message) ? "Не удалось получить подробности сбоя обновления." : message;
    }

    internal static string BuildSha256PowerShellFunction() =>
        "function Get-Sha256Hex([string]$path) { " +
        "$stream = $null; $algorithm = $null; " +
        "try { $stream = [IO.File]::OpenRead($path); $algorithm = [Security.Cryptography.SHA256]::Create(); " +
        "return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } " +
        "finally { if ($null -ne $stream) { $stream.Dispose() }; if ($null -ne $algorithm) { $algorithm.Dispose() } } }; ";

    internal static string BuildUpdaterScript(string source, string target, string backup, string replacement, string sourceHash,
        string workingDirectory, string logPath, int processId) =>
        "$ErrorActionPreference = 'Stop'; " +
        BuildSha256PowerShellFunction() +
        $"$source = {PsLiteral(source)}; $target = {PsLiteral(target)}; $backup = {PsLiteral(backup)}; $replacement = {PsLiteral(replacement)}; " +
        $"$sourceHash = {PsLiteral(sourceHash)}; " +
        $"$workingDirectory = {PsLiteral(workingDirectory)}; $logPath = {PsLiteral(logPath)}; " +
        $"$parentId = {processId}; $newProcess = $null; " +
        "try { " +
        "Wait-Process -Id $parentId -Timeout 120 -ErrorAction SilentlyContinue; " +
        "if (Get-Process -Id $parentId -ErrorAction SilentlyContinue) { throw 'NetShift не завершился за отведённое время.' }; " +
        "if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Файл обновления не найден.' }; " +
        "if (-not [string]::Equals((Get-Sha256Hex $source), $sourceHash, [StringComparison]::OrdinalIgnoreCase)) { throw 'Файл обновления изменился после проверки.' }; " +
        "Copy-Item -LiteralPath $source -Destination $replacement; " +
        "if (-not [string]::Equals((Get-Sha256Hex $replacement), $sourceHash, [StringComparison]::OrdinalIgnoreCase)) { throw 'Не удалось проверить временную копию обновления.' }; " +
        "[IO.File]::Replace($replacement, $target, $backup, $true); " +
        "$startInfo = [Diagnostics.ProcessStartInfo]::new($target); $startInfo.WorkingDirectory = $workingDirectory; $startInfo.UseShellExecute = $false; " +
        "$startInfo.Arguments = '--confirm-update \"' + $backup + '\"'; $newProcess = [Diagnostics.Process]::Start($startInfo); " +
        "if (-not $newProcess.WaitForInputIdle(30000)) { throw 'Новая версия не завершила запуск за отведённое время.' }; " +
        "for ($attempt = 0; $attempt -lt 120 -and (Test-Path -LiteralPath $backup); $attempt++) { " +
        "if ($newProcess.HasExited) { throw 'Новая версия завершилась до подтверждения запуска.' }; Start-Sleep -Milliseconds 500 }; " +
        "if (Test-Path -LiteralPath $backup) { throw 'Новая версия не подтвердила успешный запуск за отведённое время.' }; " +
        "} catch { " +
        "if ($null -ne $newProcess -and -not $newProcess.HasExited) { Stop-Process -Id $newProcess.Id -Force -ErrorAction SilentlyContinue; try { $newProcess.WaitForExit(15000) } catch {} }; " +
        "if (Test-Path -LiteralPath $backup) { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }; Move-Item -LiteralPath $backup -Destination $target -Force }; " +
        "if (Test-Path -LiteralPath $replacement) { Remove-Item -LiteralPath $replacement -Force }; " +
        "[IO.File]::WriteAllText($logPath, $_.Exception.Message); " +
        "$failureInfo = [Diagnostics.ProcessStartInfo]::new($target); $failureInfo.WorkingDirectory = $workingDirectory; " +
        "$failureInfo.UseShellExecute = $false; $failureInfo.Arguments = '--update-failed'; " +
        "if ((Test-Path -LiteralPath $target -PathType Leaf) -and -not (Get-Process -Id $parentId -ErrorAction SilentlyContinue)) { " +
        "try { [void][Diagnostics.Process]::Start($failureInfo) } catch { [IO.File]::AppendAllText($logPath, [Environment]::NewLine + $_.Exception.Message) } }; " +
        "} finally { if (Test-Path -LiteralPath $replacement) { Remove-Item -LiteralPath $replacement -Force -ErrorAction SilentlyContinue } }";

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
        return ParseLatestRelease(document.RootElement, repository, CurrentVersion);
    }

    internal static NetShiftUpdate? ParseLatestRelease(JsonElement root, string repository, string currentVersion)
    {
        var tag = RequiredString(root, "tag_name");
        var latestVersion = ParseVersion(tag);
        if (latestVersion <= ParseVersion(currentVersion)) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("В релизе NetShift не найден список файлов.");

        JsonElement executableAsset = default;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = RequiredString(asset, "name");
            if (name.Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase)) executableAsset = asset;
        }
        if (executableAsset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"В релизе {tag} не найден файл NetShift.exe.");

        var executableUrl = ValidateReleaseAssetUrl(RequiredString(executableAsset, "browser_download_url"), repository, tag);
        var executableSize = executableAsset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size) ? size : 0;
        if (executableSize <= 0 || executableSize > MaxExecutableBytes)
            throw new InvalidDataException("Размер NetShift.exe недопустим.");

        var digestMatch = Regex.Match(RequiredString(executableAsset, "digest"), "^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);
        if (!digestMatch.Success)
            throw new InvalidDataException("GitHub API не предоставил корректную SHA-256 для NetShift.exe.");

        var versionText = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
        return new NetShiftUpdate(tag, versionText, "NetShift.exe", executableUrl, executableSize, digestMatch.Groups[1].Value.ToLowerInvariant());
    }

    public static async Task<string> DownloadExecutableAsync(NetShiftUpdate update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var safeTag = Regex.Replace(update.Tag, "[^A-Za-z0-9._-]", "_");
        var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "Updates", safeTag);
        Directory.CreateDirectory(updateDirectory);
        var stagedExecutablePath = Path.Combine(updateDirectory, update.ExecutableName);
        var temporaryExecutablePath = stagedExecutablePath + ".download";

        try
        {
            using var response = await Client.GetAsync(update.ExecutableUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != update.ExecutableSize)
                throw new InvalidDataException("Размер NetShift.exe не совпадает с данными GitHub.");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporaryExecutablePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var lastProgressTimestamp = Stopwatch.GetTimestamp();
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxExecutableBytes || total > update.ExecutableSize)
                        throw new InvalidDataException("Загруженный NetShift.exe превышает объявленный размер.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (Stopwatch.GetElapsedTime(lastProgressTimestamp) >= TimeSpan.FromMilliseconds(100))
                    {
                        lastProgressTimestamp = Stopwatch.GetTimestamp();
                        progress?.Report((double)total / update.ExecutableSize);
                    }
                }
                if (total != update.ExecutableSize)
                    throw new InvalidDataException("Загруженный NetShift.exe имеет неполный размер.");
                var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!actualHash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 загруженного NetShift.exe не совпал с GitHub API.");
            }

            File.Move(temporaryExecutablePath, stagedExecutablePath, overwrite: true);
            progress?.Report(1);
            return stagedExecutablePath;
        }
        catch
        {
            if (File.Exists(temporaryExecutablePath)) File.Delete(temporaryExecutablePath);
            if (File.Exists(stagedExecutablePath)) File.Delete(stagedExecutablePath);
            throw;
        }
    }

    public static void LaunchUpdater(string stagedExecutablePath, string targetExecutablePath, int processId)
    {
        var updatesRoot = Path.GetFullPath(UpdatesDirectory) + Path.DirectorySeparatorChar;
        var source = Path.GetFullPath(stagedExecutablePath);
        var target = Path.GetFullPath(targetExecutablePath);
        if (!source.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(source).Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(source) ||
            !Path.GetFileName(target).Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target) || processId <= 0)
            throw new InvalidOperationException("Не удалось проверить пути для установки обновления NetShift.");

        var workingDirectory = Path.GetDirectoryName(target)!;
        var backup = target + ".update-backup-" + Guid.NewGuid().ToString("N");
        var replacement = target + ".update-new-" + Guid.NewGuid().ToString("N");
        var logPath = UpdateLogPath;
        using var sourceStream = File.OpenRead(source);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceStream));
        var script = BuildUpdaterScript(source, target, backup, replacement, sourceHash, workingDirectory, logPath, processId);
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
