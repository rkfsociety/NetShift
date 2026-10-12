using System.Diagnostics;
using System.Text;
using NetShift.App.Services;

CheckBackupIsIdempotent();
CheckBackupSurvivesMissingAdapter();
CheckDnsRestore(true, true, "reset");
CheckDnsRestore(false, true, "reset,ipv4=1.1.1.1");
CheckDnsRestore(true, false, "reset,ipv6=2001:4860::1");
CheckDnsRestore(false, false, "reset,ipv4=1.1.1.1,ipv6=2001:4860::1");
await CheckProcessTimeoutAsync();
CheckUpdateConfirmation();
Console.WriteLine("NetShift regression checks passed.");

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
        !updaterScript.Contains("Move-Item -LiteralPath $backup -Destination $target", StringComparison.Ordinal))
        throw new InvalidOperationException("The updater no longer verifies, confirms, and rolls back its installation.");
    var nestedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(updaterScript));
    RunPowerShell("$scriptText=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + nestedScript + "'));" +
                  "$tokens=$null;$parseErrors=$null;[void][System.Management.Automation.Language.Parser]::ParseInput($scriptText,[ref]$tokens,[ref]$parseErrors);" +
                  "if($parseErrors.Count -gt 0){throw [string]::Join('; ', @($parseErrors | ForEach-Object { $_.Message }))}");

    var directory = Path.Combine(Path.GetTempPath(), "NetShiftRegression-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
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
