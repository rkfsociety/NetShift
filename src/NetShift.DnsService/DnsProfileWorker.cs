using System.Diagnostics;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace NetShift.DnsService;

internal sealed class DnsProfileWorker(ILogger<DnsProfileWorker> logger) : BackgroundService
{
    private const string RegistryPath = @"SOFTWARE\NetShift\Dns";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long lastAppliedRevision = -1;
        long lastSeenRevision = -1;
        var retryCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var profile = ReadProfile();
                if (profile is null)
                {
                    lastAppliedRevision = -1;
                    lastSeenRevision = -1;
                    retryCount = 0;
                }
                else if (profile.Revision != lastAppliedRevision)
                {
                    if (profile.Revision != lastSeenRevision)
                    {
                        lastSeenRevision = profile.Revision;
                        retryCount = 0;
                    }

                    if (!profile.Enabled)
                    {
                        lastAppliedRevision = profile.Revision;
                        retryCount = 0;
                    }
                    else
                    {
                        try
                        {
                            await Task.Run(() => ApplyProfile(profile), stoppingToken);
                            lastAppliedRevision = profile.Revision;
                            retryCount = 0;
                            logger.LogInformation("DNS-профиль NetShift применён к сетевому адаптеру {InterfaceGuid}.", profile.InterfaceGuid);
                        }
                        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                        {
                            retryCount++;
                            if (retryCount == 1 || retryCount % 12 == 0)
                                logger.LogWarning(error, "Не удалось восстановить DNS-профиль NetShift; следующая попытка через {Seconds} сек.", PollInterval.TotalSeconds);
                        }
                    }
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Не удалось прочитать DNS-профиль NetShift.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private static DnsProfile? ReadProfile()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        if (key is null) return null;
        var revision = Convert.ToInt64(key.GetValue("Revision", 0L));
        var enabled = Convert.ToInt32(key.GetValue("Enabled", 0)) == 1;
        if (!enabled) return new DnsProfile(false, revision, Guid.Empty, [], string.Empty);

        var interfaceGuidText = key.GetValue("InterfaceGuid") as string;
        if (!Guid.TryParse(interfaceGuidText, out var interfaceGuid))
            throw new InvalidDataException("В DNS-профиле NetShift отсутствует GUID адаптера.");
        var addresses = key.GetValue("Addresses") as string[] ?? [];
        if (addresses.Length == 0 || addresses.Any(value => !IPAddress.TryParse(value, out _)))
            throw new InvalidDataException("В DNS-профиле NetShift указан некорректный список серверов.");
        var dohTemplate = key.GetValue("DohTemplate") as string ?? string.Empty;
        if (dohTemplate.Length > 0 &&
            (!Uri.TryCreate(dohTemplate, UriKind.Absolute, out var dohUri) || dohUri.Scheme != Uri.UriSchemeHttps ||
             !string.IsNullOrEmpty(dohUri.UserInfo) || !string.IsNullOrEmpty(dohUri.Fragment)))
            throw new InvalidDataException("В DNS-профиле NetShift указан некорректный шаблон DoH.");
        return new DnsProfile(true, revision, interfaceGuid, addresses, dohTemplate);
    }

    private static void ApplyProfile(DnsProfile profile)
    {
        var script = new StringBuilder("$ErrorActionPreference = 'Stop'; ")
            .Append("$guid = [guid]").Append(PsLiteral(profile.InterfaceGuid.ToString("D"))).Append("; ")
            .Append("$adapter = Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { $_.InterfaceGuid -eq $guid } | Select-Object -First 1; ")
            .Append("if ($null -eq $adapter) { throw 'Сетевой адаптер из DNS-профиля пока не найден.' }; ");

        if (profile.DohTemplate.Length > 0)
        {
            script.Append("if (-not (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue) -or ")
                .Append("-not (Get-Command Set-DnsClientDohServerAddress -ErrorAction SilentlyContinue)) { ")
                .Append("throw 'Windows не поддерживает системный DoH для сохранённого профиля.' }; ");
            foreach (var address in profile.Addresses)
            {
                script.Append("$server = ").Append(PsLiteral(address)).Append("; ")
                    .Append("$known = @(Get-DnsClientDohServerAddress -ServerAddress $server -ErrorAction SilentlyContinue); ")
                    .Append("if ($known.Count -gt 0) { Set-DnsClientDohServerAddress -ServerAddress $server -DohTemplate ")
                    .Append(PsLiteral(profile.DohTemplate)).Append(" -AutoUpgrade $true -AllowFallbackToUdp $false } ")
                    .Append("else { Add-DnsClientDohServerAddress -ServerAddress $server -DohTemplate ")
                    .Append(PsLiteral(profile.DohTemplate)).Append(" -AutoUpgrade $true -AllowFallbackToUdp $false }; ");
            }
        }

        script.Append("Set-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -ServerAddresses @(")
            .Append(string.Join(",", profile.Addresses.Select(PsLiteral))).Append(")");
        RunPowerShell(script.ToString());
    }

    private static void RunPowerShell(string command)
    {
        var script = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
                     "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                     "try { $null = & { " + command +
                     " } } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        if (process.ExitCode == 0) return;
        var detail = ExtractPowerShellError(standardError.Result);
        if (string.IsNullOrWhiteSpace(detail)) detail = ExtractPowerShellError(standardOutput.Result);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "PowerShell не применил DNS-профиль." : detail);
    }

    private static string ExtractPowerShellError(string output)
    {
        var marker = output.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (marker < 0) return output.Trim();

        try
        {
            var xml = output[(marker + "#< CLIXML".Length)..].Trim();
            var document = XDocument.Parse(xml);
            var message = document.Descendants()
                .Where(element => element.Name.LocalName == "S" && element.Attribute("S")?.Value == "Error")
                .Select(element => element.Value.Replace("_x000D__x000A_", Environment.NewLine, StringComparison.Ordinal).Trim())
                .FirstOrDefault(value => value.Length > 0);
            return message ?? "Команда PowerShell завершилась с ошибкой.";
        }
        catch (System.Xml.XmlException)
        {
            return "Не удалось прочитать сообщение об ошибке PowerShell.";
        }
    }

    private static string PsLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed record DnsProfile(bool Enabled, long Revision, Guid InterfaceGuid, string[] Addresses, string DohTemplate);
}
