using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetShift.App.Models;
using NetShift.App.Services;

CheckDnsProviderAddressEditing();
CheckZapretTargetParsing();
CheckZapretStrategyHealth();
CheckZapretCurlModesAndRanking();
CheckZapretStrategyRankingPersistence();
CheckBackupIsIdempotent();
CheckBackupSurvivesMissingAdapter();
CheckDnsRestore(true, true, "reset");
CheckDnsRestore(false, true, "reset,ipv4=1.1.1.1");
CheckDnsRestore(true, false, "reset,ipv6=2001:4860::1");
CheckDnsRestore(false, false, "reset,ipv4=1.1.1.1,ipv6=2001:4860::1");
await CheckProcessTimeoutAsync();
CheckUpdateConfirmation();
CheckExecutableReleaseMetadata();
Console.WriteLine("NetShift regression checks passed.");

static void CheckDnsProviderAddressEditing()
{
    var addressProviders = DnsProvider.Presets.Where(provider => !provider.RequiresManualAddressInput).ToArray();
    var manualProviders = DnsProvider.Presets.Where(provider => provider.RequiresManualAddressInput).ToArray();
    if (addressProviders.Length == 0 || manualProviders.Length != 1 || !manualProviders[0].Name.Equals("Свой DNS", StringComparison.Ordinal))
        throw new InvalidOperationException("DNS presets and the custom provider no longer select the expected address editor state.");
}

static void CheckZapretTargetParsing()
{
    const string targetsFile = """
    DiscordMain = "https://discord.com"
    DiscordGateway = "https://gateway.discord.gg"
    DiscordCDN = "https://cdn.discordapp.com"
    DiscordUpdates = "https://updates.discord.com"
    YouTubeWeb = "https://www.youtube.com"
    YouTubeShort = "https://youtu.be"
    YouTubeImage = "https://i.ytimg.com"
    YouTubeVideoRedirect = "https://redirector.googlevideo.com"
    GoogleMain = "https://www.google.com"
    GoogleGstatic = "https://www.gstatic.com"
    CloudflareWeb = "https://www.cloudflare.com"
    CloudflareCDN = "https://cdnjs.cloudflare.com"
    CloudflareDNS1111 = "PING:1.1.1.1"
    CloudflareDNS1001 = "PING:1.0.0.1"
    GoogleDNS8888 = "PING:8.8.8.8"
    GoogleDNS8844 = "PING:8.8.4.4"
    """;
    var parsed = ZapretConnectivityTester.ParseTargets(targetsFile);
    if (parsed.Count != 16 || parsed.Count(target => target.Url is not null) != 12 || parsed.Count(target => target.Url is null) != 4)
        throw new InvalidOperationException("The zapret tester did not load the targets.txt URL and ping-only entries.");
    if (ZapretConnectivityTester.CountProbeOperations(parsed) != 52)
        throw new InvalidOperationException("The zapret progress count must include three curl probes per URL and one ping per ping target.");
    if (parsed.Count(target => target.Group == "Discord") != 4 || parsed.Count(target => target.Group == "YouTube") != 4 ||
        parsed.Count(target => target.Group == "Google") != 4 || parsed.Count(target => target.Group == "Cloudflare") != 4)
        throw new InvalidOperationException("The zapret target groups no longer match the official list.");

    var defaults = ZapretConnectivityTester.ParseTargets(string.Empty);
    if (defaults.Count != 17 || defaults.Count(target => target.Url is not null) != 12 || defaults.Count(target => target.Url is null) != 5)
        throw new InvalidOperationException("The zapret tester fallback targets no longer match test zapret.ps1.");
    if (ZapretConnectivityTester.CountProbeOperations(defaults) != 53)
        throw new InvalidOperationException("The zapret progress count does not match the fallback targets.");
}

static void CheckZapretStrategyHealth()
{
    if (ZapretStrategyHealthRules.FromResults(8, 8, 4, 4) != ZapretStrategyHealthStatus.Green ||
        ZapretStrategyHealthRules.FromResults(7, 8, 4, 4) != ZapretStrategyHealthStatus.Yellow ||
        ZapretStrategyHealthRules.FromResults(0, 8, 0, 4) != ZapretStrategyHealthStatus.Red ||
        ZapretStrategyHealthRules.FromResults(0, 0, 2, 2) != ZapretStrategyHealthStatus.Green)
        throw new InvalidOperationException("Zapret strategy health must distinguish full, partial, and failed probe results.");

    if (ZapretStrategyHealthRules.FromStoredStatus(null, false) != ZapretStrategyHealthStatus.Yellow ||
        ZapretStrategyHealthRules.FromStoredStatus("Red", true) != ZapretStrategyHealthStatus.Red ||
        ZapretStrategyHealthRules.SortOrder(ZapretStrategyHealthStatus.Green) >= ZapretStrategyHealthRules.SortOrder(ZapretStrategyHealthStatus.Yellow) ||
        ZapretStrategyHealthRules.SortOrder(ZapretStrategyHealthStatus.Yellow) >= ZapretStrategyHealthRules.SortOrder(ZapretStrategyHealthStatus.Red) ||
        ZapretStrategyHealthRules.SortOrder(ZapretStrategyHealthStatus.Red) >= ZapretStrategyHealthRules.SortOrder(ZapretStrategyHealthStatus.Unknown))
        throw new InvalidOperationException("Zapret strategy health migration or display ordering is incorrect.");
}

static void CheckZapretCurlModesAndRanking()
{
    var http11 = ZapretConnectivityTester.BuildCurlArguments("https://discord.com", "HTTP");
    var tls12 = ZapretConnectivityTester.BuildCurlArguments("https://discord.com", "TLS1.2");
    var tls13 = ZapretConnectivityTester.BuildCurlArguments("https://discord.com", "TLS1.3");
    var expected = new[]
    {
        "-I", "-s", "-m", "4", "--connect-timeout", "2", "-o", "NUL", "-w", "%{http_code}",
        "--show-error", "--tlsv1.2", "--tls-max", "1.2", "https://discord.com"
    };
    if (!tls12.SequenceEqual(expected))
        throw new InvalidOperationException("The TLS 1.2 curl probe differs from the original standard tester.");
    if (http11[^2] != "--http1.1" || tls13[^4] != "--tlsv1.3" || tls13[^3] != "--tls-max" || tls13[^2] != "1.3")
        throw new InvalidOperationException("The HTTP/1.1 or TLS 1.3 curl probe differs from the original standard tester.");

    var lessHttp = new ZapretConnectivitySummary(35, 36, 16, 16, [], []);
    var morePing = new ZapretConnectivitySummary(35, 36, 17, 17, [], []);
    var moreHttp = new ZapretConnectivitySummary(36, 36, 0, 17, [], []);
    if (ZapretConnectivityTester.CompareScores(morePing, lessHttp) <= 0 ||
        ZapretConnectivityTester.CompareScores(moreHttp, morePing) <= 0)
        throw new InvalidOperationException("Zapret strategy ranking must prioritize HTTP/TLS successes, then ping successes.");

    var discord = ZapretConnectivityTester.ParseTargets("DiscordMain = \"https://discord.com\"")[0];
    var targetResult = new ZapretConnectivityTargetResult(discord,
    [
        new ZapretCurlProbeResult("HTTP", ZapretCurlProbeStatus.Success, 404, null),
        new ZapretCurlProbeResult("TLS1.2", ZapretCurlProbeStatus.Error, null, "timeout"),
        new ZapretCurlProbeResult("TLS1.3", ZapretCurlProbeStatus.Unsupported, null, "not supported")
    ], true, 12);
    var summary = ZapretConnectivityTester.Summarize([targetResult]);
    if (summary.HttpPassed != 1 || summary.HttpTotal != 3 || summary.PingPassed != 1 || summary.PingTotal != 1 ||
        summary.Groups["Discord"].HttpPassed != 1)
        throw new InvalidOperationException("Zapret test scoring no longer counts curl exit success and ping results like the original tester.");
}

static void CheckZapretStrategyRankingPersistence()
{
    var ordered = ZapretStrategyRanking.Order(
    [
        new ZapretStrategyRanking(@"C:\zapret\general3.bat", 35, 36, 17, 17),
        new ZapretStrategyRanking(@"C:\zapret\general10.bat", 36, 36, 0, 17),
        new ZapretStrategyRanking(@"C:\zapret\general2.bat", 35, 36, 17, 17)
    ]);
    if (Path.GetFileName(ordered[0].Path) != "general10.bat" ||
        Path.GetFileName(ordered[1].Path) != "general2.bat" ||
        Path.GetFileName(ordered[2].Path) != "general3.bat")
        throw new InvalidOperationException("Saved zapret strategies must retain HTTP-first, ping-second, natural-path ranking.");

    var serialized = JsonSerializer.Serialize(ordered);
    var restored = JsonSerializer.Deserialize<List<ZapretStrategyRanking>>(serialized);
    if (restored is null || restored.Count != ordered.Count ||
        restored[0].HttpPassed != 36 || restored[1].PingPassed != 17)
        throw new InvalidOperationException("Zapret strategy ranking did not survive settings serialization.");
}

static void CheckBackupIsIdempotent()
{
    var script = "class MockRegistryKey { [int]$Enabled; MockRegistryKey([int]$enabled){$this.Enabled=$enabled} " +
                 "[object] GetValue([string]$name,[object]$default){if($name -eq 'Enabled'){return $this.Enabled};return $default} }; " +
                 "$backup=[MockRegistryKey]::new(1);$captures=0;" + LegacyDnsScriptFragments.CaptureBackupOnlyWhenInactive +
                 "$captures++;};if($captures -ne 0){throw 'An active Legacy backup was overwritten.'};" +
                 "$backup=[MockRegistryKey]::new(0);" + LegacyDnsScriptFragments.CaptureBackupOnlyWhenInactive +
                 "$captures++;};if($captures -ne 1){throw 'An inactive Legacy backup was not captured.'}";
    RunPowerShell(script);
}

static void CheckBackupSurvivesMissingAdapter()
{
    var script = "$a=$null; try { " + LegacyDnsScriptFragments.RequireAdapterBeforeRestore +
                 "throw 'RESTORE CONTINUED' } catch { if ($_.Exception.Message -eq 'Адаптер Legacy DNS пока не найден; резервная копия сохранена.') { exit 0 }; throw }";
    RunPowerShell(script);
}

static void CheckDnsRestore(bool dhcp4, bool dhcp6, string expectedCalls)
{
    var script = "$script:Calls=[System.Collections.Generic.List[string]]::new();" +
                 "function Set-DnsClientServerAddress { param([int]$InterfaceIndex,[string[]]$ServerAddresses,[switch]$ResetServerAddresses) " +
                 "if($ResetServerAddresses){$script:Calls.Add('reset');return};" +
                 "$family=if($ServerAddresses[0].Contains(':')){'ipv6'}else{'ipv4'};" +
                 "$script:Calls.Add($family+'='+($ServerAddresses -join ';')) };" +
                 "$a=[pscustomobject]@{InterfaceIndex=12};$d4=@('1.1.1.1');$d6=@('2001:4860::1');" +
                 $"$dhcp4=${dhcp4.ToString().ToLowerInvariant()};$dhcp6=${dhcp6.ToString().ToLowerInvariant()};" +
                 LegacyDnsScriptFragments.RestoreDnsSettings +
                 $"if(($script:Calls -join ',') -ne '{expectedCalls}'){{throw ('Unexpected DNS restore calls: '+($script:Calls -join ','))}}";
    RunPowerShell(script);
}

static void RunPowerShell(string command, bool expectFailure = false)
{
    var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    if (!File.Exists(powershellPath))
        throw new PlatformNotSupportedException("The Legacy DNS regression checks require Windows PowerShell.");
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + command));
    var startInfo = new ProcessStartInfo(powershellPath)
    {
        Arguments = $"-NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {encoded}",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(15000))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("A Legacy DNS regression script did not finish within 15 seconds.");
    }
    Task.WaitAll(output, error);
    if (expectFailure && process.ExitCode == 0)
        throw new InvalidOperationException("The restore script continued without a matching adapter.");
    if (!expectFailure && process.ExitCode != 0)
        throw new InvalidOperationException($"Legacy DNS regression script failed: {error.Result}{output.Result}{Environment.NewLine}{command}");
}

static async Task CheckProcessTimeoutAsync()
{
    var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    try
    {
        await ProcessExecutionService.CaptureAsync(powershellPath, ["-NoLogo", "-NoProfile", "-Command", "Start-Sleep -Seconds 30"], TimeSpan.FromMilliseconds(250));
        throw new InvalidOperationException("A timed-out process was reported as successful.");
    }
    catch (TimeoutException)
    {
    }
}

static void CheckUpdateConfirmation()
{
    var targetPath = @"C:\Users\O'Brien\AppData\Local\Programs\NetShift\NetShift.exe";
    var backupPath = targetPath + ".update-backup-" + Guid.NewGuid().ToString("N");
    var updaterScript = NetShiftUpdateService.BuildUpdaterScript(
        @"C:\Users\O'Brien\AppData\Local\NetShift\Updates\v0.3.026\NetShift.exe", targetPath, backupPath,
        targetPath + ".update-new-" + Guid.NewGuid().ToString("N"), new string('a', 64), Path.GetDirectoryName(targetPath)!,
        @"C:\Users\O'Brien\AppData\Local\NetShift\Updates\update.log", 42);
    if (!updaterScript.Contains("[IO.File]::Replace", StringComparison.Ordinal) ||
        !updaterScript.Contains("--confirm-update", StringComparison.Ordinal) ||
        !updaterScript.Contains("Move-Item -LiteralPath $backup -Destination $target", StringComparison.Ordinal) ||
        updaterScript.Contains("Get-FileHash", StringComparison.Ordinal) ||
        !updaterScript.Contains("Get-Sha256Hex", StringComparison.Ordinal) ||
        !updaterScript.Contains("--update-failed", StringComparison.Ordinal) ||
        updaterScript.Contains("[System.Windows.MessageBox]::Show", StringComparison.Ordinal))
        throw new InvalidOperationException("The updater no longer verifies, confirms, and rolls back its installation.");
    var nestedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(updaterScript));
    RunPowerShell("$scriptText=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + nestedScript + "'));" +
                  "$tokens=$null;$parseErrors=$null;[void][System.Management.Automation.Language.Parser]::ParseInput($scriptText,[ref]$tokens,[ref]$parseErrors);" +
                  "if($parseErrors.Count -gt 0){throw [string]::Join('; ', @($parseErrors | ForEach-Object { $_.Message }))}");

    var directory = Path.Combine(Path.GetTempPath(), "NetShiftRegression-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var hashInput = Path.Combine(directory, "hash-input.bin");
        File.WriteAllText(hashInput, "NetShift updater hash check");
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hashInput)));
        var hashScript = NetShiftUpdateService.BuildSha256PowerShellFunction() +
            $"if (-not [string]::Equals((Get-Sha256Hex {PowerShellLiteral(hashInput)}), '{expectedHash}', [StringComparison]::OrdinalIgnoreCase)) {{ throw 'The updater SHA-256 helper returned the wrong hash.' }}";
        RunPowerShell(hashScript);

        var logPath = Path.Combine(directory, "update.log");
        File.WriteAllText(logPath, "Проверка обновления не пройдена.");
        var failure = NetShiftUpdateService.GetPendingUpdateFailure(["--update-failed"], logPath);
        if (failure != "Проверка обновления не пройдена." || File.Exists(logPath))
            throw new InvalidOperationException("The updater failure was not delivered to the application and cleared.");
        if (NetShiftUpdateService.GetPendingUpdateFailure(["--other"], logPath) is not null)
            throw new InvalidOperationException("An unrelated application argument was treated as an update failure.");

        var executable = Path.Combine(directory, "NetShift.exe");
        var backup = executable + ".update-backup-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(executable, "current");
        File.WriteAllText(backup, "previous");
        var resolved = NetShiftUpdateService.GetPendingUpdateBackup(["--confirm-update", backup], executable);
        if (!string.Equals(resolved, backup, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The valid update confirmation path was rejected.");

        NetShiftUpdateService.ConfirmUpdateStartup(resolved);
        if (File.Exists(backup))
            throw new InvalidOperationException("A confirmed update backup was not removed.");

        var outsideBackup = Path.Combine(Path.GetTempPath(), "NetShift.exe.update-backup-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outsideBackup, "outside");
        try
        {
            if (NetShiftUpdateService.GetPendingUpdateBackup(["--confirm-update", outsideBackup], executable) is not null)
                throw new InvalidOperationException("An update backup outside the application directory was accepted.");
        }
        finally { File.Delete(outsideBackup); }
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

static void CheckExecutableReleaseMetadata()
{
    var digest = new string('a', 64);
    var releaseJson = $$"""
    {
      "tag_name": "v0.3.045",
      "assets": [
        {
          "name": "NetShift.exe",
          "browser_download_url": "https://github.com/example/NetShift/releases/download/v0.3.045/NetShift.exe",
          "size": 1024,
          "digest": "sha256:{{digest}}"
        }
      ]
    }
    """;
    using var release = JsonDocument.Parse(releaseJson);
    var update = NetShiftUpdateService.ParseLatestRelease(release.RootElement, "example/NetShift", "0.3.044");
    if (update is null || update.ExecutableName != "NetShift.exe" || update.ExecutableSize != 1024 || update.Sha256 != digest ||
        update.ExecutableUrl != "https://github.com/example/NetShift/releases/download/v0.3.045/NetShift.exe")
        throw new InvalidOperationException("The executable-only release metadata was not accepted.");

    if (NetShiftUpdateService.ParseLatestRelease(release.RootElement, "example/NetShift", "0.3.045") is not null)
        throw new InvalidOperationException("A release matching the current app version was offered as an update.");

    var releaseWithoutDigest = releaseJson.Replace($"\"digest\": \"sha256:{digest}\"", "\"digest\": null", StringComparison.Ordinal);
    using var invalidRelease = JsonDocument.Parse(releaseWithoutDigest);
    try
    {
        NetShiftUpdateService.ParseLatestRelease(invalidRelease.RootElement, "example/NetShift", "0.3.044");
        throw new InvalidOperationException("An executable release without a SHA-256 digest was accepted.");
    }
    catch (InvalidDataException)
    {
    }
}
