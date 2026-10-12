using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

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
                    dnsApplyScript.Append($"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses @({string.Join(",", addresses.Select(PsLiteral))})");
                    RunPowerShell(dnsApplyScript.ToString());
                    break;

                case "dns-reset":
                    if (args.Length < 3 || !int.TryParse(args[2], out interfaceIndex) || interfaceIndex <= 0)
                        throw new InvalidOperationException("Не удалось определить сетевой интерфейс.");
                    RunPowerShell($"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses");
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
            MessageBox.Show(error.Message, "NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.Environment["NO_UPDATE_CHECK"] = "1";
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"\"\"{scriptPath}\"\"");
        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Не удалось запустить командный файл.");
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
        process.WaitForExit();
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
        var script = "$ErrorActionPreference = 'Stop'; try { " + command +
                     " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        if (process.ExitCode != 0)
        {
            var detail = standardError.Result.Trim();
            if (string.IsNullOrWhiteSpace(detail))
                detail = standardOutput.Result.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "Команда PowerShell завершилась с ошибкой."
                : detail);
        }
    }

    private static string PsLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
