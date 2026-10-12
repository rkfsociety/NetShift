using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace NetShift.App.Services;

internal static class ZapretDiagnosticsService
{
    private static readonly string[] KnownConflicts =
    ["GoodbyeDPI", "discordfix_zapret", "winws1", "winws2", "Killer", "SmartByte", "TracSrvWrapper", "EPWD", "Intel Connectivity Network Service"];

    public static async Task<IReadOnlyList<string>> CollectAsync(string folder)
    {
        var lines = new List<string> { "Диагностика NetShift (только чтение)", $"Папка: {folder}" };
        var bfeTask = QueryServiceAsync("BFE");
        var zapretTask = QueryServiceAsync("zapret");
        var windivertTask = QueryServiceAsync("WinDivert");
        var tcpTask = RunReadOnlyCommandAsync("netsh.exe", "interface", "tcp", "show", "global");
        var servicesTask = QueryAllServicesAsync();
        await Task.WhenAll((Task)bfeTask, zapretTask, windivertTask, tcpTask, servicesTask).ConfigureAwait(false);

        var bfe = bfeTask.Result;
        lines.Add(bfe?.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) == true ? "✓ Base Filtering Engine работает." : "! Base Filtering Engine не запущен или недоступен.");
        var zapret = zapretTask.Result;
        lines.Add(zapret is null ? "• Служба zapret не установлена." : zapret.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ? "✓ Служба zapret работает." : "• Служба zapret установлена, но остановлена.");
        lines.Add(windivertTask.Result is null ? "• Служба WinDivert не зарегистрирована." : "• Служба WinDivert зарегистрирована; NetShift не меняет её состояние.");
        var timestampLine = tcpTask.Result.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("timestamp", StringComparison.OrdinalIgnoreCase) || line.Contains("метк", StringComparison.OrdinalIgnoreCase));
        if (timestampLine is not null) lines.Add($"• TCP timestamps: {timestampLine.Trim()} (диагностика не меняет этот параметр).");

        var processes = Process.GetProcessesByName("winws");
        lines.Add($"• Запущено процессов winws.exe: {processes.Length}.");
        foreach (var process in processes)
        {
            try { lines.Add($"  PID {process.Id}: {process.MainModule?.FileName ?? "путь недоступен"}"); }
            catch { lines.Add($"  PID {process.Id}: путь недоступен."); }
            process.Dispose();
        }

        using var internetSettings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var proxyEnable = internetSettings?.GetValue("ProxyEnable");
        var proxyServer = internetSettings?.GetValue("ProxyServer") as string;
        lines.Add(Convert.ToInt32(proxyEnable ?? 0) == 1 ? $"! В Windows включён системный прокси: {proxyServer}" : "✓ Системный прокси Windows выключен.");
        var bin = Path.Combine(folder, "bin");
        lines.Add(Directory.Exists(bin) && Directory.EnumerateFiles(bin, "*.sys").Any()
            ? "✓ Файл драйвера WinDivert найден в пакете." : "! Файл драйвера WinDivert не найден.");
        lines.Add(folder.Any(character => character > 127 || char.IsWhiteSpace(character))
            ? "! В пути есть пробелы или нелатинские символы." : "✓ Путь без пробелов и нелатинских символов.");
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive) && folder.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase))
            lines.Add("! Папка zapret находится внутри OneDrive.");

        var services = servicesTask.Result;
        foreach (var knownConflict in KnownConflicts)
            if (services.Contains(knownConflict, StringComparison.OrdinalIgnoreCase)) lines.Add($"! Найдена потенциально конфликтующая служба: {knownConflict}.");
        var vpnServices = services.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase) && line.Contains("VPN", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(line.IndexOf(':') + 1)..].Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (vpnServices.Length > 0) lines.Add($"! Возможные VPN-службы: {string.Join(", ", vpnServices)}.");
        var adguardProcesses = Process.GetProcessesByName("AdguardSvc");
        if (adguardProcesses.Length > 0) lines.Add("! Найден процесс AdGuard Service (AdguardSvc.exe), который может конфликтовать с Discord.");
        foreach (var process in adguardProcesses) process.Dispose();

        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");
        try
        {
            if (File.ReadLines(hostsPath).Any(line => line.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || line.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
                lines.Add("! В системном hosts найдены записи YouTube.");
        }
        catch { lines.Add("• Не удалось прочитать системный hosts."); }
        lines.Add(HasDohConfiguration() ? "✓ В параметрах DNS Windows найдена настройка DoH." : "• Параметры DNS Windows не показывают DoH; проверьте защищённый DNS отдельно в браузере.");
        lines.Add("Проверки только читают настройки. NetShift не удаляет конфликтующие службы, не очищает кэш приложений и не включает TCP timestamps автоматически.");
        return lines;
    }

    private static async Task<string?> QueryServiceAsync(string serviceName)
    {
        try
        {
            var result = await ProcessExecutionService.CaptureAsync(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
                ["query", serviceName], TimeSpan.FromSeconds(15));
            return result.ExitCode == 0 ? result.StandardOutput + Environment.NewLine + result.StandardError : null;
        }
        catch { return null; }
    }

    private static async Task<string> QueryAllServicesAsync()
    {
        try
        {
            var result = await ProcessExecutionService.CaptureAsync(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
                ["query", "type=", "service", "state=", "all"], TimeSpan.FromSeconds(30));
            return result.StandardOutput;
        }
        catch { return string.Empty; }
    }

    private static async Task<string> RunReadOnlyCommandAsync(string executable, params string[] arguments)
    {
        try
        {
            var result = await ProcessExecutionService.CaptureAsync(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), executable),
                arguments, TimeSpan.FromSeconds(30));
            return result.StandardOutput + Environment.NewLine + result.StandardError;
        }
        catch { return string.Empty; }
    }

    private static bool HasDohConfiguration()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters");
            return root is not null && RegistryTreeHasDohFlag(root, 0);
        }
        catch { return false; }
    }

    private static bool RegistryTreeHasDohFlag(RegistryKey key, int depth)
    {
        if (depth > 6) return false;
        try { if (Convert.ToInt32(key.GetValue("DohFlags") ?? 0) > 0) return true; } catch { }
        foreach (var childName in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(childName);
            if (child is not null && RegistryTreeHasDohFlag(child, depth + 1)) return true;
        }
        return false;
    }
}
