using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using NetShift.App.Models;

namespace NetShift.App.Services;

public static class AdminCommandRouter
{
    public static bool TryHandle(string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("--admin", StringComparison.Ordinal))
            return false;

        try
        {
            var operation = args.Length > 1 ? args[1] : string.Empty;
            switch (operation)
            {
                case "dns-apply":
                    if (args.Length < 4 || !int.TryParse(args[2], out var interfaceIndex) || interfaceIndex <= 0)
                        throw new InvalidOperationException("Не удалось определить сетевой интерфейс.");
                    var addresses = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (addresses.Length == 0 || addresses.Any(value => !IPAddress.TryParse(value, out _)))
                        throw new InvalidOperationException("Список DNS-адресов содержит некорректный IP-адрес.");
                    var dohTemplate = args.Length > 4 ? args[4].Trim() : string.Empty;
                    var dnsApplyScript = new StringBuilder();
                    if (dohTemplate.Length > 0)
                    {
                        if (!Uri.TryCreate(dohTemplate, UriKind.Absolute, out var dohUri) || dohUri.Scheme != Uri.UriSchemeHttps ||
                            !string.IsNullOrEmpty(dohUri.UserInfo) || !string.IsNullOrEmpty(dohUri.Fragment))
                            throw new InvalidOperationException("Шаблон DoH должен быть корректным HTTPS-адресом.");

                        dnsApplyScript.Append("if (-not (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue) -or ")
                            .Append("-not (Get-Command Set-DnsClientDohServerAddress -ErrorAction SilentlyContinue)) { ")
                            .Append("throw 'Эта версия Windows не поддерживает системный DoH через DNS Client; адреса интерфейса не изменены.' }; ");
                        foreach (var address in addresses)
                        {
                            dnsApplyScript.Append("$server = ").Append(PsLiteral(address)).Append("; ")
                                .Append("$known = @(Get-DnsClientDohServerAddress -ServerAddress $server -ErrorAction SilentlyContinue); ")
                                .Append("if ($known.Count -gt 0) { Set-DnsClientDohServerAddress -ServerAddress $server -DohTemplate ")
                                .Append(PsLiteral(dohTemplate)).Append(" -AutoUpgrade $true -AllowFallbackToUdp $false } ")
                                .Append("else { Add-DnsClientDohServerAddress -ServerAddress $server -DohTemplate ")
                                .Append(PsLiteral(dohTemplate)).Append(" -AutoUpgrade $true -AllowFallbackToUdp $false }; ");
                        }
                    }
                    dnsApplyScript.Append($"$adapter = Get-NetAdapter -InterfaceIndex {interfaceIndex} -ErrorAction Stop; ")
                        .Append($"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses @({string.Join(",", addresses.Select(PsLiteral))}); ")
                        .Append("$interfaceGuid = ([guid]$adapter.InterfaceGuid).ToString('D'); ")
                        .Append("$key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SOFTWARE\\NetShift\\Dns'); ")
                        .Append("$revision = [long]$key.GetValue('Revision', 0); ")
                        .Append("$key.SetValue('Enabled', 1, [Microsoft.Win32.RegistryValueKind]::DWord); ")
                        .Append("$key.SetValue('InterfaceGuid', $interfaceGuid, [Microsoft.Win32.RegistryValueKind]::String); ")
                        .Append("$key.SetValue('Addresses', [string[]]@(").Append(string.Join(",", addresses.Select(PsLiteral))).Append("), [Microsoft.Win32.RegistryValueKind]::MultiString); ")
                        .Append("$key.SetValue('DohTemplate', ").Append(PsLiteral(dohTemplate)).Append(", [Microsoft.Win32.RegistryValueKind]::String); ")
                        .Append("$key.SetValue('Revision', ($revision + 1), [Microsoft.Win32.RegistryValueKind]::QWord); $key.Dispose()");
                    RunPowerShell(dnsApplyScript.ToString());
                    break;

                case "dns-reset":
                    if (args.Length < 3 || !int.TryParse(args[2], out interfaceIndex) || interfaceIndex <= 0)
                        throw new InvalidOperationException("Не удалось определить сетевой интерфейс.");
                    RunPowerShell($"$adapter = Get-NetAdapter -InterfaceIndex {interfaceIndex} -ErrorAction Stop; " +
                        $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses; " +
                        "$interfaceGuid = ([guid]$adapter.InterfaceGuid).ToString('D'); " +
                        "$key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\\NetShift\\Dns', $true); " +
                        "if ($null -ne $key -and [string]::Equals([string]$key.GetValue('InterfaceGuid'), $interfaceGuid, [StringComparison]::OrdinalIgnoreCase)) { " +
                        "$revision = [long]$key.GetValue('Revision', 0); $key.SetValue('Enabled', 0, [Microsoft.Win32.RegistryValueKind]::DWord); " +
                        "$key.SetValue('Revision', ($revision + 1), [Microsoft.Win32.RegistryValueKind]::QWord) }; if ($null -ne $key) { $key.Dispose() }");
                    break;

                case "dns-legacy-apply":
                    if (args.Length < 3 || !int.TryParse(args[2], out interfaceIndex) || interfaceIndex <= 0)
                        throw new InvalidOperationException("Не удалось определить сетевой интерфейс.");
                    var includeIpv6Loopback = args.Length > 3 && bool.TryParse(args[3], out var enableLegacyIpv6) && enableLegacyIpv6;
                    RunPowerShell($"$ErrorActionPreference='Stop'; $i={interfaceIndex}; $a=Get-NetAdapter -InterfaceIndex $i -ErrorAction Stop; " +
                        "$backup=[Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SOFTWARE\\NetShift\\LegacyDns'); " +
                        LegacyDnsScriptFragments.CaptureBackupOnlyWhenInactive +
                        "$d4=@((Get-DnsClientServerAddress -InterfaceIndex $i -AddressFamily IPv4).ServerAddresses); " +
                        "$d6=@((Get-DnsClientServerAddress -InterfaceIndex $i -AddressFamily IPv6).ServerAddresses); " +
                        "$guidText=([guid]$a.InterfaceGuid).ToString('D'); " +
                        "$v4Key=Get-ItemProperty -LiteralPath ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\'+$guidText) -ErrorAction SilentlyContinue; " +
                        "$v6Key=Get-ItemProperty -LiteralPath ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\Interfaces\\'+$guidText) -ErrorAction SilentlyContinue; " +
                        "$dhcp4=[string]::IsNullOrWhiteSpace([string]$v4Key.NameServer); $dhcp6=[string]::IsNullOrWhiteSpace([string]$v6Key.NameServer); " +
                        "$backup.SetValue('InterfaceGuid',([guid]$a.InterfaceGuid).ToString('D'),[Microsoft.Win32.RegistryValueKind]::String); " +
                        "$backup.SetValue('InterfaceIndex',$i,[Microsoft.Win32.RegistryValueKind]::DWord); " +
                        "$backup.SetValue('IPv4',[string[]]$d4,[Microsoft.Win32.RegistryValueKind]::MultiString); " +
                        "$backup.SetValue('IPv6',[string[]]$d6,[Microsoft.Win32.RegistryValueKind]::MultiString); " +
                        "$backup.SetValue('DhcpIPv4',[int]$dhcp4,[Microsoft.Win32.RegistryValueKind]::DWord); " +
                        "$backup.SetValue('DhcpIPv6',[int]$dhcp6,[Microsoft.Win32.RegistryValueKind]::DWord); " +
                        "$native=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\\NetShift\\Dns'); if($null -ne $native){$backup.SetValue('NativeEnabled',[int]$native.GetValue('Enabled',0),[Microsoft.Win32.RegistryValueKind]::DWord);$backup.SetValue('NativeGuid',[string]$native.GetValue('InterfaceGuid',''),[Microsoft.Win32.RegistryValueKind]::String);$backup.SetValue('NativeAddresses',[string[]]$native.GetValue('Addresses',@()),[Microsoft.Win32.RegistryValueKind]::MultiString);$backup.SetValue('NativeDoh',[string]$native.GetValue('DohTemplate',''),[Microsoft.Win32.RegistryValueKind]::String);$backup.SetValue('NativeRevision',[long]$native.GetValue('Revision',0),[Microsoft.Win32.RegistryValueKind]::QWord);$native.Dispose()}; " +
                        "$backup.SetValue('Enabled',1,[Microsoft.Win32.RegistryValueKind]::DWord) }; $backup.Dispose(); " +
                        "$native=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\\NetShift\\Dns',$true); if($null -ne $native){$r=[long]$native.GetValue('Revision',0);$native.SetValue('Enabled',0,[Microsoft.Win32.RegistryValueKind]::DWord);$native.SetValue('Revision',($r+1),[Microsoft.Win32.RegistryValueKind]::QWord);$native.Dispose()}; " +
                        $"$loopback=@('127.0.0.1'); {(includeIpv6Loopback ? "$loopback+=@('::1');" : string.Empty)} Set-DnsClientServerAddress -InterfaceIndex $i -ServerAddresses $loopback");
                    break;

                case "dns-legacy-restore":
                    RunPowerShell("$ErrorActionPreference='Stop'; $backup=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\\NetShift\\LegacyDns',$true); " +
                        "if($null -ne $backup -and [int]$backup.GetValue('Enabled',0) -eq 1){ $guid=[guid]$backup.GetValue('InterfaceGuid'); " +
                        "$a=Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {$_.InterfaceGuid -eq $guid} | Select-Object -First 1; " +
                        LegacyDnsScriptFragments.RequireAdapterBeforeRestore +
                        "$d4=[string[]]$backup.GetValue('IPv4');$d6=[string[]]$backup.GetValue('IPv6'); " +
                        "$dhcp4=[int]$backup.GetValue('DhcpIPv4',0) -eq 1;$dhcp6=[int]$backup.GetValue('DhcpIPv6',0) -eq 1; " +
                        LegacyDnsScriptFragments.RestoreDnsSettings +
                        "$native=[Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('SOFTWARE\\NetShift\\Dns');$native.SetValue('Enabled',[int]$backup.GetValue('NativeEnabled',0),[Microsoft.Win32.RegistryValueKind]::DWord);$native.SetValue('InterfaceGuid',[string]$backup.GetValue('NativeGuid',''),[Microsoft.Win32.RegistryValueKind]::String);$native.SetValue('Addresses',[string[]]$backup.GetValue('NativeAddresses',@()),[Microsoft.Win32.RegistryValueKind]::MultiString);$native.SetValue('DohTemplate',[string]$backup.GetValue('NativeDoh',''),[Microsoft.Win32.RegistryValueKind]::String);$native.SetValue('Revision',([long]$native.GetValue('Revision',0)+1),[Microsoft.Win32.RegistryValueKind]::QWord);$native.Dispose(); " +
                        "$backup.SetValue('Enabled',0,[Microsoft.Win32.RegistryValueKind]::DWord); }; if($null -ne $backup){$backup.Dispose()}");
                    break;

                case "dns-service-install-embedded":
                    InstallEmbeddedDnsService();
                    break;

                case "dns-service-remove":
                    RemoveDnsService();
                    break;

                case "zapret-start":
                    var strategy = RequireZapretFile(args);
                    StartBatch(strategy);
                    break;

                case "zapret-service-install":
                    InstallZapretService(RequireZapretFile(args));
                    break;

                case "zapret-service-remove":
                    if (args.Length < 3) throw new InvalidOperationException("Не указана папка zapret.");
                    RemoveZapretService(RequireZapretFolder(args[2]));
                    break;

                case "zapret-service-start":
                    if (args.Length < 3) throw new InvalidOperationException("Не указана папка zapret.");
                    StartZapretService(RequireZapretFolder(args[2]));
                    break;

                case "zapret-service-stop":
                    if (args.Length < 3) throw new InvalidOperationException("Не указана папка zapret.");
                    StopZapretService(RequireZapretFolder(args[2]));
                    break;

                case "zapret-game-filter-save":
                    if (args.Length < 6) throw new InvalidOperationException("Не переданы параметры игрового фильтра.");
                    SaveGameFilter(RequireZapretFolder(args[2]), args[3], args[4], args[5]);
                    break;

                case "zapret-settings-save":
                    if (args.Length < 7)
                        throw new InvalidOperationException("Не переданы все параметры пакета.");
                    var settingsFolder = RequireZapretFolder(args[2]);
                    SaveGameFilter(settingsFolder, args[3], args[4], args[5]);
                    SetIpSetMode(settingsFolder, args[6]);
                    break;

                case "zapret-ipset-mode":
                    if (args.Length < 4) throw new InvalidOperationException("Не передан режим IPSet.");
                    SetIpSetMode(RequireZapretFolder(args[2]), args[3]);
                    break;

                case "zapret-ipset-update":
                    if (args.Length < 5) throw new InvalidOperationException("Не передан загруженный список IPSet и его контрольная сумма.");
                    UpdateIpSet(RequireZapretFolder(args[2]), args[3], args[4]);
                    break;

                case "zapret-fake-replace":
                    if (args.Length < 5) throw new InvalidOperationException("Не переданы параметры замены fake-файла.");
                    ReplaceFakeFile(RequireZapretFolder(args[2]), args[3], args[4]);
                    break;

                case "zapret-hosts-append":
                    if (args.Length < 4) throw new InvalidOperationException("Не передан список hosts и его контрольная сумма.");
                    AppendHosts(args[2], args[3]);
                    break;

                case "zapret-hosts-restore":
                    if (args.Length < 4) throw new InvalidOperationException("Не передана резервная копия hosts и её контрольная сумма.");
                    RestoreHostsBackup(args[2], args[3]);
                    break;

                case "zapret-run-tests":
                    if (args.Length < 3) throw new InvalidOperationException("Не указана папка zapret.");
                    StartUpstreamTests(RequireZapretFolder(args[2]));
                    break;

                case "zapret-test-strategies":
                    if (args.Length < 4) throw new InvalidOperationException("Не указана папка zapret или файл статуса проверки.");
                    RunZapretStrategyTestSweep(RequireZapretFolder(args[2]), RequireZapretTestProgressPath(args[3]));
                    break;

                case "zapret-package-install":
                    if (args.Length < 5) throw new InvalidOperationException("Не переданы архив релиза, версия и контрольная сумма.");
                    InstallPackage(args[2], args[3], args[4]);
                    break;

                case "zapret-stop":
                    if (args.Length < 3)
                        throw new InvalidOperationException("Не указана папка zapret.");
                    var executablePath = Path.GetFullPath(Path.Combine(args[2], "bin", "winws.exe"));
                    var script = $"$target = [IO.Path]::GetFullPath({PsLiteral(executablePath)}); " +
                                 "$matches = Get-CimInstance Win32_Process -Filter \"Name='winws.exe'\" | " +
                                 "Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq $target) }; " +
                                 "$matches | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; " +
                                 "if (-not $matches) { throw 'В выбранной папке запущенный winws.exe не найден.' }; " +
                                 "Write-Output ('Остановлено процессов: ' + @($matches).Count)";
                    RunPowerShell(script);
                    break;

                default:
                    throw new InvalidOperationException("Неизвестная административная команда.");
            }
            return true;
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            ThemedDialog.Show(null, error.Message, "NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
    }

    private static string RequireZapretFile(string[] args)
    {
        if (args.Length < 3)
            throw new InvalidOperationException("Не указан файл zapret.");

        var path = Path.GetFullPath(args[2]);
        var fileName = Path.GetFileName(path);
        var valid = fileName.StartsWith("general", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (!valid || !File.Exists(path))
            throw new InvalidOperationException("Выбранный файл не похож на скрипт официального пакета zapret.");
        return path;
    }

    private static void StartBatch(string scriptPath)
    {
        var executablePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(scriptPath)!, "bin", "winws.exe"));
        if (IsZapretProcessRunning(executablePath)) return;

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c \"{scriptPath}\"",
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["NO_UPDATE_CHECK"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить командный файл.");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (IsZapretProcessRunning(executablePath)) return;
            if (process.HasExited && attempt > 10) break;
            Thread.Sleep(100);
        }

        var exitCode = process.HasExited ? $" Код завершения сценария: {process.ExitCode}." : string.Empty;
        throw new InvalidOperationException("Сценарий завершился, но winws.exe из выбранного пакета не запущен." + exitCode +
            " Проверьте пакет zapret и повторите запуск.");
    }

    private static bool IsZapretProcessRunning(string executablePath)
    {
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                try
                {
                    var runningPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(runningPath) &&
                        Path.GetFullPath(runningPath).Equals(executablePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static string RequireZapretFolder(string folder)
    {
        var fullPath = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(fullPath, "service.bat")) ||
            !File.Exists(Path.Combine(fullPath, "bin", "winws.exe")) ||
            !Directory.Exists(Path.Combine(fullPath, "lists")))
            throw new InvalidOperationException("В выбранной папке не найден полный пакет zapret.");
        return fullPath;
    }

    private static void InstallZapretService(string strategyPath)
    {
        var folder = RequireZapretFolder(Path.GetDirectoryName(strategyPath)!);
        var executable = Path.GetFullPath(Path.Combine(folder, "bin", "winws.exe"));
        var imagePath = GetZapretServiceImagePath();
        if (!string.IsNullOrWhiteSpace(imagePath) && !ServiceBelongsToSelectedOrNetShiftPackage(imagePath, folder))
            throw new InvalidOperationException("Служба zapret уже занята другим пакетом. Удалите её через приложение, которое её установило.");

        var strategyArgs = BuildStrategyArguments(strategyPath, folder);
        StopAndDeleteService("zapret");
        var binaryCommandLine = $"\"{executable}\" {strategyArgs}";
        RunSc(["create", "zapret", "binPath=", binaryCommandLine, "DisplayName=", "zapret", "start=", "auto"]);
        RunSc(["description", "zapret", "Zapret DPI bypass software"]);
        using (var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\zapret", writable: true) ??
                         throw new InvalidOperationException("Не удалось открыть параметры службы zapret."))
            key.SetValue("zapret-discord-youtube", Path.GetFileNameWithoutExtension(strategyPath), RegistryValueKind.String);
        RunSc(["start", "zapret"]);
    }

    private static string BuildStrategyArguments(string strategyPath, string folder)
    {
        var lines = File.ReadAllLines(strategyPath, Encoding.UTF8);
        var start = Array.FindIndex(lines, line => line.Contains("winws.exe", StringComparison.OrdinalIgnoreCase));
        if (start < 0) throw new InvalidOperationException("В выбранной стратегии не найден запуск winws.exe.");

        var command = new StringBuilder();
        var firstLine = lines[start];
        var executablePosition = firstLine.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) + "winws.exe".Length;
        command.Append(firstLine[executablePosition..].Trim().TrimStart('"'));
        var continues = lines[start].TrimEnd().EndsWith('^');
        for (var index = start + 1; continues && index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            continues = line.EndsWith('^');
            if (continues) line = line[..^1].TrimEnd();
            command.Append(' ').Append(line);
        }

        var (tcp, udp) = ReadGameFilter(Path.Combine(folder, "utils", "game_filter.enabled"));
        var bin = Path.GetFullPath(Path.Combine(folder, "bin")) + Path.DirectorySeparatorChar;
        var lists = Path.GetFullPath(Path.Combine(folder, "lists")) + Path.DirectorySeparatorChar;
        var args = Regex.Replace(command.ToString(), @"%BIN%", _ => bin, RegexOptions.IgnoreCase);
        args = Regex.Replace(args, @"%LISTS%", _ => lists, RegexOptions.IgnoreCase);
        args = Regex.Replace(args, @"%GameFilterTCP%", _ => tcp, RegexOptions.IgnoreCase);
        args = Regex.Replace(args, @"%GameFilterUDP%", _ => udp, RegexOptions.IgnoreCase);
        args = Regex.Replace(args, @"\s*\^\s*", " ").Trim();
        if (args.Length == 0 || args.Contains("%BIN%", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("%LISTS%", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("%GameFilter", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Не удалось подготовить параметры выбранной стратегии для службы.");
        return args;
    }

    private static (string Tcp, string Udp) ReadGameFilter(string settingsPath)
    {
        var mode = "disabled";
        var tcpRange = "1024-65535";
        var udpRange = "1024-65535";
        if (File.Exists(settingsPath))
        {
            foreach (var line in File.ReadLines(settingsPath))
            {
                var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2) continue;
                switch (pair[0].ToLowerInvariant())
                {
                    case "mode": mode = pair[1].ToLowerInvariant(); break;
                    case "all": mode = "all"; break;
                    case "tcp":
                        if (pair[1].Length == 0) mode = "tcp";
                        else if (IsValidPortRange(pair[1])) tcpRange = pair[1];
                        break;
                    case "udp":
                        if (pair[1].Length == 0) mode = "udp";
                        else if (IsValidPortRange(pair[1])) udpRange = pair[1];
                        break;
                }
            }
        }
        return mode switch
        {
            "all" => (tcpRange, udpRange),
            "tcp" => (tcpRange, "12"),
            "udp" => ("12", udpRange),
            _ => ("12", "12")
        };
    }

    private static void RemoveZapretService(string folder)
    {
        var installedImage = GetZapretServiceImagePath();
        if (string.IsNullOrWhiteSpace(installedImage)) return;
        if (!ServiceBelongsToSelectedOrNetShiftPackage(installedImage, folder))
            throw new InvalidOperationException("Служба zapret установлена из другой папки; NetShift её не удалит.");
        StopAndDeleteService("zapret");
    }

    private static void StartZapretService(string folder)
    {
        EnsureServiceBelongsToPackage(folder);
        RunSc(["start", "zapret"]);
    }

    private static void StopZapretService(string folder)
    {
        EnsureServiceBelongsToPackage(folder);
        RunSc(["stop", "zapret"], allowFailure: true);
    }

    private static void EnsureServiceBelongsToPackage(string folder)
    {
        var imagePath = GetZapretServiceImagePath();
        if (string.IsNullOrWhiteSpace(imagePath)) throw new InvalidOperationException("Служба zapret не установлена.");
        if (!ServiceBelongsToSelectedOrNetShiftPackage(imagePath, folder))
            throw new InvalidOperationException("Служба zapret установлена из другой папки; NetShift не будет ей управлять.");
    }

    private static bool ServiceBelongsToSelectedOrNetShiftPackage(string imagePath, string selectedFolder)
    {
        var match = Regex.Match(imagePath, "^\\s*\\\"(?<path>[^\\\"]*winws\\.exe)\\\"|^\\s*(?<path>[^\\s]*winws\\.exe)", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var executable = Path.GetFullPath(match.Groups["path"].Value);
        var selectedExecutable = Path.GetFullPath(Path.Combine(selectedFolder, "bin", "winws.exe"));
        if (executable.Equals(selectedExecutable, StringComparison.OrdinalIgnoreCase)) return true;

        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var managedRoot = Path.GetFullPath(Path.Combine(drive, "NetShift")) + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var binFolder = Path.GetDirectoryName(executable);
        var packageFolder = binFolder is null ? null : Path.GetDirectoryName(binFolder);
        return binFolder is not null && Path.GetFileName(binFolder).Equals("bin", StringComparison.OrdinalIgnoreCase) &&
               packageFolder is not null && Path.GetFileName(packageFolder).StartsWith("zapret-", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(Path.Combine(packageFolder, "service.bat"));
    }

    private static string? GetZapretServiceImagePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
        return key?.GetValue("ImagePath") as string;
    }

    private static void StopAndDeleteService(string name)
    {
        var query = RunSc(["query", name], allowFailure: true);
        if (query.ExitCode != 0) return;
        if (query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
            query.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            RunSc(["stop", name], allowFailure: true);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                Thread.Sleep(300);
                query = RunSc(["query", name], allowFailure: true);
                if (query.ExitCode != 0 || query.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        RunSc(["delete", name]);
    }

    private static (int ExitCode, string Output) RunSc(IEnumerable<string> arguments, bool allowFailure = false)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить sc.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            throw new TimeoutException("sc.exe не завершился за 120 секунд.");
        }
        Task.WaitAll(stdout, stderr);
        var output = (stdout.Result + Environment.NewLine + stderr.Result).Trim();
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? $"sc.exe завершился с кодом {process.ExitCode}." : output);
        return (process.ExitCode, output);
    }

    private static void SaveGameFilter(string folder, string mode, string tcp, string udp)
    {
        if (mode is not ("disabled" or "all" or "tcp" or "udp") || !IsValidPortRange(tcp) || !IsValidPortRange(udp))
            throw new InvalidOperationException("Проверьте режим и диапазоны портов. Допустимо: 80, 1024-65535,1024-1934.");
        var utils = Path.Combine(folder, "utils");
        Directory.CreateDirectory(utils);
        File.WriteAllText(Path.Combine(utils, "game_filter.enabled"), $"mode={mode}\r\ntcp={tcp}\r\nudp={udp}\r\n", new UTF8Encoding(false));
    }

    private static bool IsValidPortRange(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            var numbers = part.Split('-', StringSplitOptions.TrimEntries);
            if (numbers.Length is < 1 or > 2 || !int.TryParse(numbers[0], out var first) || first is < 1 or > 65535) return false;
            var last = first;
            if (numbers.Length == 2 && (!int.TryParse(numbers[1], out last) || last is < 1 or > 65535)) return false;
            if (first > last) return false;
        }
        return true;
    }

    private static void SetIpSetMode(string folder, string mode)
    {
        if (mode is not ("loaded" or "none" or "any")) throw new InvalidOperationException("Неизвестный режим IPSet.");
        var listPath = Path.Combine(folder, "lists", "ipset-all.txt");
        var backupPath = listPath + ".backup";
        Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
        var currentLines = File.Exists(listPath) ? File.ReadAllLines(listPath) : [];
        var meaningfulLines = currentLines.Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var currentMode = meaningfulLines.Length == 0 ? "any" : meaningfulLines.Any(line => line.Contains("203.0.113.113/32", StringComparison.Ordinal)) ? "none" : "loaded";
        if (mode == currentMode) return;

        if (mode == "loaded")
        {
            if (!File.Exists(backupPath)) throw new InvalidOperationException("Резервный IPSet не найден. Сначала скачайте свежий список.");
            File.Copy(backupPath, listPath, overwrite: true);
        }
        else if (mode == "none")
        {
            if (currentMode == "loaded") File.Copy(listPath, backupPath, overwrite: true);
            File.WriteAllText(listPath, "203.0.113.113/32\r\n", new UTF8Encoding(false));
        }
        else
        {
            File.WriteAllText(listPath, string.Empty, new UTF8Encoding(false));
        }
    }

    private static void UpdateIpSet(string folder, string downloadedPath, string expectedHash)
    {
        var fullDownload = VerifyDownloadedFile(downloadedPath, expectedHash, ".txt");
        var lines = File.ReadAllLines(fullDownload, Encoding.UTF8);
        if (lines.Length > 1_000_000 || lines.Any(line => !IsIpSetLine(line)) || lines.Count(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#')) < 10)
            throw new InvalidDataException("Скачанный IPSet не прошёл проверку формата.");
        var listPath = Path.Combine(folder, "lists", "ipset-all.txt");
        var backupPath = listPath + ".backup";
        if (File.Exists(listPath) && new FileInfo(listPath).Length > 0 &&
            !File.ReadAllText(listPath, Encoding.UTF8).Contains("203.0.113.113/32", StringComparison.Ordinal))
            File.Copy(listPath, backupPath, overwrite: true);
        File.Copy(fullDownload, listPath, overwrite: true);
    }

    private static bool IsIpSetLine(string line)
    {
        var value = line.Trim();
        if (value.Length == 0 || value.StartsWith('#')) return true;
        var pieces = value.Split('/', 2);
        if (!IPAddress.TryParse(pieces[0], out var address)) return false;
        return pieces.Length == 1 || (int.TryParse(pieces[1], out var prefix) && prefix >= 0 && prefix <= (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
    }

    private static void ReplaceFakeFile(string folder, string target, string fakeName)
    {
        if (target is not ("discord" or "game") || !Regex.IsMatch(fakeName, "^[A-Za-z0-9._-]{1,80}\\.bin$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Некорректный тип или имя fake-файла.");
        var bin = Path.Combine(folder, "bin");
        var source = Path.Combine(bin, fakeName);
        var destination = Path.Combine(bin, target == "discord" ? "ACTIVE_DISCORD_UDP.bin" : "ACTIVE_GAME_UDP.bin");
        if (!File.Exists(source) || Path.GetFullPath(Path.GetDirectoryName(source)!) != Path.GetFullPath(bin))
            throw new InvalidOperationException("Выбранный fake-файл не найден в папке bin.");
        File.Copy(source, destination, overwrite: true);
    }

    private static void AppendHosts(string downloadedPath, string expectedHash)
    {
        var fullDownload = VerifyDownloadedFile(downloadedPath, expectedHash, ".txt");
        var lines = File.ReadAllLines(fullDownload, Encoding.UTF8);
        if (lines.Length is < 10 or > 1000 || lines.Any(line => !IsValidHostsLine(line)))
            throw new InvalidDataException("Скачанный hosts-файл не прошёл проверку формата.");

        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");
        var current = File.ReadAllLines(hostsPath, Encoding.UTF8).Select(NormalizeHostsLine).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = lines.Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !current.Contains(NormalizeHostsLine(line)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (additions.Length == 0) return;

        var backup = hostsPath + $".NetShift-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.bak";
        File.Copy(hostsPath, backup, overwrite: false);
        using var writer = new StreamWriter(hostsPath, append: true, new UTF8Encoding(false));
        writer.WriteLine();
        writer.WriteLine("# Added by NetShift from Flowseal/zapret-discord-youtube");
        foreach (var line in additions) writer.WriteLine(line);
    }

    private static void StartUpstreamTests(string folder)
    {
        var script = Path.GetFullPath(Path.Combine(folder, "utils", "test zapret.ps1"));
        var expectedPrefix = Path.GetFullPath(Path.Combine(folder, "utils")) + Path.DirectorySeparatorChar;
        if (!script.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(script))
            throw new InvalidOperationException("В выбранном пакете не найден официальный тестовый скрипт.");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteWindowsArgument(script)}",
            WorkingDirectory = folder,
            UseShellExecute = true
        };
        if (Process.Start(startInfo) is null) throw new InvalidOperationException("Не удалось запустить официальный тестовый скрипт.");
    }

    private static void RunZapretStrategyTestSweep(string folder, string progressPath)
    {
        var strategies = Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, Comparer<string>.Create(ZapretStrategyComparer.ComparePaths))
            .ToArray();
        if (strategies.Length == 0) throw new InvalidOperationException("В выбранной папке не найдены стратегии general*.bat.");

        ZapretConnectivityTester.EnsureCurlAvailable();
        EnsureZapretCanBeTested();
        var targets = ZapretConnectivityTester.LoadTargets(folder);
        var completedProbeCount = 0;
        var totalProbeCount = ZapretConnectivityTester.CountProbeOperations(targets) * strategies.Length;
        var progressWriteLock = new object();
        var testedCount = 0;
        var candidates = new List<(string Path, ZapretConnectivitySummary Summary)>();
        var failures = new List<string>();
        var progress = new ZapretTestProgress(null, "starting", 0, strategies.Length, [], 0, 0, 0, 0,
            "Подготовка проверки.",
            CompletedProbeCount: completedProbeCount,
            TotalProbeCount: totalProbeCount);
        WriteZapretTestProgress(progressPath, progress);

        foreach (var strategyPath in strategies)
        {
            EnsureZapretCanBeTested();
            var strategyName = Path.GetFileNameWithoutExtension(strategyPath);
            Process? batchProcess = null;
            ZapretConnectivitySummary? summary = null;
            string? failure = null;
            progress = new ZapretTestProgress(strategyPath, "starting", testedCount + 1, strategies.Length, [], 0, 0, 0, 0,
                $"Запускаю временно {strategyName}.",
                CompletedProbeCount: completedProbeCount,
                TotalProbeCount: totalProbeCount);
            WriteZapretTestProgress(progressPath, progress);

            try
            {
                batchProcess = StartZapretTestBatch(strategyPath, folder);
                progress = progress with { Phase = "checking", Message = "Проверяю соединение." };
                WriteZapretTestProgress(progressPath, progress);
                summary = ZapretConnectivityTester.CheckTargetsAsync(targets, probeCompleted: () =>
                {
                    lock (progressWriteLock)
                    {
                        completedProbeCount++;
                        progress = progress with { CompletedProbeCount = completedProbeCount };
                        WriteZapretTestProgress(progressPath, progress);
                    }
                }).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                failure = error.Message;
            }
            finally
            {
                try { StopZapretTestBatch(folder, batchProcess); }
                catch (Exception error)
                {
                    failure = error.Message;
                    summary = null;
                }
                batchProcess?.Dispose();
            }

            testedCount++;
            if (summary is not null)
            {
                candidates.Add((Path.GetFullPath(strategyPath), summary));
                progress = new ZapretTestProgress(strategyPath, "scored", testedCount, strategies.Length,
                    summary.Groups, summary.HttpPassed, summary.HttpTotal, summary.PingPassed, summary.PingTotal,
                    $"{strategyName}: HTTP/TLS {summary.HttpPassed}/{summary.HttpTotal}; ping {summary.PingPassed}/{summary.PingTotal}.",
                    CompletedProbeCount: completedProbeCount,
                    TotalProbeCount: totalProbeCount);
            }
            else
            {
                var failureMessage = string.IsNullOrWhiteSpace(failure) ? "причина не указана" : failure;
                failures.Add($"{strategyName}: {failureMessage}");
                progress = new ZapretTestProgress(strategyPath, "failed", testedCount, strategies.Length, [], 0, 0, 0, 0,
                    $"Не удалось проверить {strategyName}. {failure}",
                    CompletedProbeCount: completedProbeCount,
                    TotalProbeCount: totalProbeCount);
            }
            WriteZapretTestProgress(progressPath, progress);
        }

        var bestCandidate = candidates.FirstOrDefault();
        foreach (var candidate in candidates.Skip(1))
            if (ZapretConnectivityTester.CompareScores(candidate.Summary, bestCandidate.Summary) > 0)
                bestCandidate = candidate;

        var recommendedPath = candidates.Count == 0 ? null : bestCandidate.Path;
        var bestSummary = candidates.Count == 0 ? null : bestCandidate.Summary;
        progress = new ZapretTestProgress(null, "completed", testedCount, strategies.Length,
            bestSummary?.Groups ?? [], bestSummary?.HttpPassed ?? 0, bestSummary?.HttpTotal ?? 0,
            bestSummary?.PingPassed ?? 0, bestSummary?.PingTotal ?? 0,
            recommendedPath is null
                ? $"Не удалось проверить ни одну стратегию. Проверено: {testedCount} из {strategies.Length}. {failures.FirstOrDefault()}"
                : $"Лучшая по результатам оригинальной проверки: {Path.GetFileNameWithoutExtension(recommendedPath)} — HTTP/TLS {bestSummary!.HttpPassed}/{bestSummary.HttpTotal}, ping {bestSummary.PingPassed}/{bestSummary.PingTotal}. Ошибок запуска: {failures.Count}.",
            recommendedPath,
            Completed: true,
            CompletedProbeCount: completedProbeCount,
            TotalProbeCount: totalProbeCount);
        WriteZapretTestProgress(progressPath, progress);
    }

    private static Process StartZapretTestBatch(string strategyPath, string folder)
    {
        var executable = Path.GetFullPath(Path.Combine(folder, "bin", "winws.exe"));
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c \"{strategyPath}\"",
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["NO_UPDATE_CHECK"] = "1";
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить выбранный .bat.");

        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (IsZapretProcessRunning(executable)) return process;
            Thread.Sleep(200);
        }

        var exitCode = process.HasExited ? $" Код завершения сценария: {process.ExitCode}." : string.Empty;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
        throw new InvalidOperationException("Оригинальный сценарий не запустил winws.exe из выбранного пакета за 5 секунд." + exitCode);
    }

    private static void StopZapretTestBatch(string folder, Process? batchProcess)
    {
        if (batchProcess is not null && !batchProcess.HasExited)
        {
            try { batchProcess.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (batchProcess.HasExited) { }
            if (!batchProcess.WaitForExit(10_000))
                throw new TimeoutException("Не удалось завершить командный сценарий тестируемой стратегии.");
        }

        var executable = Path.GetFullPath(Path.Combine(folder, "bin", "winws.exe"));
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                string? runningPath;
                try { runningPath = process.MainModule?.FileName; }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(runningPath) ||
                    !Path.GetFullPath(runningPath).Equals(executable, StringComparison.OrdinalIgnoreCase))
                    continue;

                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                if (!process.WaitForExit(10_000))
                    throw new TimeoutException("Не удалось остановить временный winws.exe после проверки.");
            }
        }
    }

    private static void EnsureZapretCanBeTested()
    {
        var service = RunSc(["query", "zapret"], allowFailure: true).Output;
        if (Regex.IsMatch(service, @"STATE\s*:\s*[234]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Нельзя перебирать стратегии, пока служба zapret работает или запускается. Остановите её и повторите проверку.");

        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                string? path;
                try { path = process.MainModule?.FileName; }
                catch { path = null; }
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(path)
                    ? "Найден запущенный winws.exe. Остановите активную стратегию перед проверкой списка."
                    : $"Уже работает winws.exe ({path}). Остановите активную стратегию перед проверкой списка.");
            }
        }
    }

    private static string RequireZapretTestProgressPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var tempFolder = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fileName = Path.GetFileName(fullPath);
        if (!fullPath.StartsWith(tempFolder, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(fileName, "^NetShift-zapret-test-[0-9a-f]{32}\\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Недопустимый файл статуса проверки стратегий.");
        return fullPath;
    }

    private static void WriteZapretTestProgress(string path, ZapretTestProgress progress)
    {
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(progress);
        for (var attempt = 0; ; attempt++)
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static bool IsValidHostsLine(string line)
    {
        var value = line.Trim();
        if (value.Length == 0 || value.StartsWith('#')) return true;
        var fields = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && IPAddress.TryParse(fields[0], out _) &&
               fields.Skip(1).All(host => Regex.IsMatch(host, "^[A-Za-z0-9.-]{1,253}$", RegexOptions.CultureInvariant));
    }

    private static string NormalizeHostsLine(string line) => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void RestoreHostsBackup(string backupPath, string expectedHash)
    {
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");
        var expectedDirectory = Path.GetDirectoryName(Path.GetFullPath(hostsPath)) + Path.DirectorySeparatorChar;
        var fullBackup = Path.GetFullPath(backupPath);
        var fileName = Path.GetFileName(fullBackup);
        if (!fullBackup.StartsWith(expectedDirectory, StringComparison.OrdinalIgnoreCase) ||
            !fileName.StartsWith("hosts.NetShift-", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullBackup))
            throw new InvalidOperationException("Можно восстановить только резервную копию hosts, созданную NetShift.");
        using (var stream = File.OpenRead(fullBackup))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Резервная копия hosts изменилась после выбора.");
        }
        var safetyCopy = hostsPath + $".BeforeRestore-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.bak";
        File.Copy(hostsPath, safetyCopy, overwrite: false);
        File.Copy(fullBackup, hostsPath, overwrite: true);
    }

    private static void InstallEmbeddedDnsService()
    {
        const string resourceName = "NetShift.App.Payload.NetShift.DnsService.exe";
        using var payload = typeof(AdminCommandRouter).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("В этой сборке не встроен файл DNS-службы. Установите актуальный релиз NetShift.");
        InstallDnsService(payload);
    }

    private static void InstallDnsService(Stream payload)
    {
        var serviceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetShift", "DnsService");
        var target = Path.Combine(serviceDirectory, "NetShift.DnsService.exe");
        var staging = target + ".new-" + Guid.NewGuid().ToString("N");
        var backup = target + ".backup";
        Directory.CreateDirectory(serviceDirectory);
        if (payload.CanSeek && (payload.Length <= 0 || payload.Length > 200L * 1024 * 1024))
            throw new InvalidDataException("Встроенный файл DNS-службы имеет недопустимый размер.");
        using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            payload.CopyTo(output);

        var serviceQuery = RunSc(["query", "NetShiftDns"], allowFailure: true);
        var wasInstalled = serviceQuery.ExitCode == 0;
        var wasRunning = wasInstalled && serviceQuery.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        var serviceCreated = false;
        var hadBackup = File.Exists(target);
        try
        {
            if (wasInstalled)
            {
                var imagePath = GetServiceImagePath("NetShiftDns");
                if (!ImagePathEqualsExecutable(imagePath, target))
                    throw new InvalidOperationException("Имя службы NetShiftDns уже занято другой программой. NetShift не будет её заменять.");
                if (!File.Exists(target))
                    throw new FileNotFoundException("Не найден исполняемый файл установленной службы DNS.", target);
                RunSc(["config", "NetShiftDns", "start=", "auto", "obj=", "LocalSystem"]);
                StopServiceAndWait("NetShiftDns");
            }

            if (hadBackup) File.Copy(target, backup, overwrite: true);
            File.Move(staging, target, overwrite: true);
            if (!wasInstalled)
            {
                RunSc(["create", "NetShiftDns", "binPath=", $"\"{target}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "NetShift DNS Service"]);
                serviceCreated = true;
            }
            RunSc(["config", "NetShiftDns", "binPath=", $"\"{target}\"", "start=", "auto", "obj=", "LocalSystem"]);
            RunSc(["description", "NetShiftDns", "Восстанавливает сохранённые настройки DNS при запуске Windows."]);
            RunSc(["failure", "NetShiftDns", "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/30000"], allowFailure: true);
            RunSc(["start", "NetShiftDns"]);
            WaitForServiceState("NetShiftDns", "RUNNING");
            using (var payloadKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\NetShift\DnsService"))
            {
                var payloadVersion = typeof(AdminCommandRouter).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(attribute => attribute.Key.Equals("NetShiftDnsServiceVersion", StringComparison.Ordinal))?.Value;
                if (!string.IsNullOrWhiteSpace(payloadVersion))
                    payloadKey?.SetValue("Version", payloadVersion, RegistryValueKind.String);
            }
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch
        {
            if (hadBackup && File.Exists(backup))
            {
                try
                {
                    if (QueryServiceState("NetShiftDns") == "RUNNING") StopServiceAndWait("NetShiftDns");
                    File.Move(backup, target, overwrite: true);
                    if (wasInstalled && wasRunning) RunSc(["start", "NetShiftDns"], allowFailure: true);
                }
                catch { }
            }
            else if (serviceCreated)
            {
                try { StopAndDeleteService("NetShiftDns"); } catch { }
                try { if (File.Exists(target)) File.Delete(target); } catch { }
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { }
        }
    }

    private static void RemoveDnsService()
    {
        var serviceQuery = RunSc(["query", "NetShiftDns"], allowFailure: true);
        if (serviceQuery.ExitCode == 0)
        {
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetShift", "DnsService", "NetShift.DnsService.exe");
            if (!ImagePathEqualsExecutable(GetServiceImagePath("NetShiftDns"), target))
                throw new InvalidOperationException("Служба NetShiftDns указывает на другой исполняемый файл; удаление отменено.");
            StopAndDeleteService("NetShiftDns");
        }

        var serviceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetShift", "DnsService");
        var executable = Path.Combine(serviceDirectory, "NetShift.DnsService.exe");
        if (File.Exists(executable)) File.Delete(executable);
        if (Directory.Exists(serviceDirectory) && !Directory.EnumerateFileSystemEntries(serviceDirectory).Any())
            Directory.Delete(serviceDirectory);
        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\NetShift\DnsService", throwOnMissingSubKey: false);
        // DNS stays configured in Windows after removing the service.
    }

    private static string? GetServiceImagePath(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        return key?.GetValue("ImagePath") as string;
    }

    private static bool ImagePathEqualsExecutable(string? imagePath, string executable)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        var trimmed = imagePath.Trim();
        var configuredPath = trimmed.StartsWith('"')
            ? trimmed[1..].Split('"', 2)[0]
            : trimmed.Split(' ', 2)[0];
        try { return Path.GetFullPath(configuredPath).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string? QueryServiceState(string serviceName)
    {
        var query = RunSc(["query", serviceName], allowFailure: true);
        if (query.ExitCode != 0) return null;
        if (query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "RUNNING";
        if (query.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "STOPPED";
        return null;
    }

    private static void StopServiceAndWait(string serviceName)
    {
        if (QueryServiceState(serviceName) == "RUNNING") RunSc(["stop", serviceName], allowFailure: true);
        WaitForServiceState(serviceName, "STOPPED");
    }

    private static void WaitForServiceState(string serviceName, string expectedState)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (QueryServiceState(serviceName) == expectedState) return;
            Thread.Sleep(250);
        }
        throw new TimeoutException($"Служба {serviceName} не перешла в состояние {expectedState}.");
    }

    private static void InstallPackage(string archivePath, string tag, string expectedHash)
    {
        if (!Regex.IsMatch(tag, "^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Некорректная версия релиза.");
        var fullArchive = VerifyDownloadedFile(archivePath, expectedHash, ".zip");

        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var appRoot = Path.Combine(drive, "NetShift");
        var destination = Path.Combine(appRoot, "zapret-" + tag.TrimStart('v', 'V'));
        if (Directory.Exists(destination)) throw new IOException($"Папка уже существует: {destination}");
        Directory.CreateDirectory(appRoot);
        var staging = Path.Combine(appRoot, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(fullArchive);
            if (archive.Entries.Count is 0 or > 20_000) throw new InvalidDataException("В архиве некорректное количество файлов.");
            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Архив содержит символическую ссылку; установка отменена.");
                expandedBytes += entry.Length;
                if (expandedBytes > 1_000_000_000) throw new InvalidDataException("Распакованный архив превышает 1 ГБ.");
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(segment => segment is ".." or "."))
                    throw new InvalidDataException("Архив содержит небезопасный путь.");
                var outputPath = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                var stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                if (!outputPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Архив пытается записать файлы вне папки установки.");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                entry.ExtractToFile(outputPath, overwrite: false);
            }

            var packageRoots = Directory.GetDirectories(staging, "*", SearchOption.AllDirectories)
                .Where(directory => File.Exists(Path.Combine(directory, "service.bat")) && File.Exists(Path.Combine(directory, "bin", "winws.exe")))
                .Append(staging)
                .Where(directory => File.Exists(Path.Combine(directory, "service.bat")) && File.Exists(Path.Combine(directory, "bin", "winws.exe")))
                .ToArray();
            if (packageRoots.Length != 1) throw new InvalidDataException("В ZIP не найдена единственная папка с полным пакетом zapret.");
            Directory.Move(packageRoots[0], destination);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static string VerifyDownloadedFile(string path, string expectedHash, string extension)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "Downloads")) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath) ||
            !fullPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Не найден ожидаемый временный файл NetShift.");
        if (!Regex.IsMatch(expectedHash, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Некорректная контрольная сумма временного файла.");
        using var stream = File.OpenRead(fullPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Временный файл изменился после проверки загрузки.");
        return fullPath;
    }

    private static void RunPowerShell(string command)
    {
        var script = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
                     "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                     "try { $null = & { " + command +
                     " } } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
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
        if (!process.WaitForExit(600_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            Task.WaitAll(standardOutput, standardError);
            throw new TimeoutException("PowerShell не завершился за 10 минут.");
        }
        Task.WaitAll(standardOutput, standardError);
        if (process.ExitCode != 0)
        {
            var detail = ExtractPowerShellError(standardError.Result);
            if (string.IsNullOrWhiteSpace(detail))
                detail = ExtractPowerShellError(standardOutput.Result);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "Команда PowerShell завершилась с ошибкой."
                : detail);
        }
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
}
