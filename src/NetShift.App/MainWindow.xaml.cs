using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using NetShift.App.Models;
using NetShift.App.Services;
using Microsoft.Win32;

namespace NetShift.App;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "settings.json");
    private readonly ObservableCollection<DnsProvider> _dnsProviders = new(DnsProvider.Presets);
    private IReadOnlyList<NetworkAdapterInfo> _adapters = [];
    private AppSettings _settings = new();
    private NetShiftUpdate? _availableUpdate;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private bool _allowClose;
    private bool _trayNoticeShown;
    private bool _trayIconDisposed;

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = CreateTrayIcon();
        Closing += MainWindow_Closing;
        Closed += (_, _) => DisposeTrayIcon();
        System.Windows.Application.Current.SessionEnding += (_, _) => AllowCloseAndDisposeTrayIcon();
        ProviderComboBox.ItemsSource = _dnsProviders;
        ProviderComboBox.SelectedIndex = 0;
        Loaded += MainWindow_Loaded;
        GameFilterModeComboBox.ItemsSource = new[]
        {
            new PackageChoice("Выключен", "disabled"),
            new PackageChoice("TCP и UDP", "all"),
            new PackageChoice("Только TCP", "tcp"),
            new PackageChoice("Только UDP", "udp")
        };
        GameFilterModeComboBox.DisplayMemberPath = nameof(PackageChoice.Name);
        GameFilterModeComboBox.SelectedValuePath = nameof(PackageChoice.Value);
        IpSetModeComboBox.ItemsSource = new[]
        {
            new PackageChoice("Загруженный список", "loaded"),
            new PackageChoice("Не фильтровать IP", "any"),
            new PackageChoice("Не использовать IPSet", "none")
        };
        IpSetModeComboBox.DisplayMemberPath = nameof(PackageChoice.Name);
        IpSetModeComboBox.SelectedValuePath = nameof(PackageChoice.Value);
        FakeTargetComboBox.ItemsSource = new[]
        {
            new PackageChoice("Discord UDP", "discord"),
            new PackageChoice("Игровой UDP", "game")
        };
        FakeTargetComboBox.DisplayMemberPath = nameof(PackageChoice.Name);
        FakeTargetComboBox.SelectedValuePath = nameof(PackageChoice.Value);
        FakeTargetComboBox.SelectedIndex = 0;
        LoadSettings();
        AppAutoStartCheckBox.IsChecked = IsAppAutoStartEnabled();
        InstallDnsServiceButton.IsEnabled = NetShiftUpdateService.Repository is not null;
        RefreshDnsServiceStatus();
        RefreshAdapters(selectFirst: true);
        RefreshZapretFolder();
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Открыть NetShift");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            AllowCloseAndDisposeTrayIcon();
            System.Windows.Application.Current.Shutdown();
        });
        menu.Items.Add(openItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        var icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "NetShift — DNS и zapret",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(ShowFromTray);
        };
        return icon;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;

        e.Cancel = true;
        Hide();
        if (_trayNoticeShown) return;
        _trayNoticeShown = true;
        _trayIcon.ShowBalloonTip(3500, "NetShift продолжает работать", "Окно скрыто в системный трей. Откройте значок NetShift, чтобы вернуться.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void AllowCloseAndDisposeTrayIcon()
    {
        _allowClose = true;
        DisposeTrayIcon();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIconDisposed) return;
        _trayIconDisposed = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings));
        }
        catch (Exception error)
        {
            SetZapretStatus($"Не удалось сохранить настройки: {error.Message}");
        }
    }

    private void RefreshAdapters(bool selectFirst = false)
    {
        var selectedIndex = (AdapterComboBox.SelectedItem as NetworkAdapterInfo)?.InterfaceIndex;
        _adapters = NetworkService.GetAdapters();
        AdapterComboBox.ItemsSource = _adapters;
        var selected = _adapters.FirstOrDefault(adapter => adapter.InterfaceIndex == selectedIndex);
        AdapterComboBox.SelectedItem = selected ?? (selectFirst ? _adapters.FirstOrDefault() : null);
        if (AdapterComboBox.SelectedItem is null)
            CurrentDnsText.Text = _adapters.Count == 0 ? "Сетевые адаптеры не найдены." : "Выберите адаптер, чтобы увидеть текущие DNS.";
    }

    private void RefreshZapretFolder()
    {
        var folder = _settings.ZapretFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            var defaultFolder = Path.Combine(AppContext.BaseDirectory, "zapret");
            folder = Directory.Exists(defaultFolder) ? defaultFolder : FindLatestInstalledPackage() ?? string.Empty;
        }
        ZapretFolderTextBox.Text = folder;
        UpdateZapretInventory();
    }

    private static string? FindLatestInstalledPackage()
    {
        var root = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "NetShift");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root, "zapret-*")
            .Where(path => File.Exists(Path.Combine(path, "service.bat")) && File.Exists(Path.Combine(path, "bin", "winws.exe")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void UpdateZapretInventory()
    {
        var folder = ZapretFolderTextBox.Text.Trim();
        var strategies = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()
            : [];

        var selectedPath = StrategyComboBox.SelectedValue as string;
        StrategyComboBox.ItemsSource = strategies.Select(path => new StrategyItem(Path.GetFileName(path), path)).ToArray();
        StrategyComboBox.DisplayMemberPath = nameof(StrategyItem.Name);
        StrategyComboBox.SelectedValuePath = nameof(StrategyItem.Path);
        StrategyComboBox.SelectedValue = strategies.FirstOrDefault(path => path == selectedPath) ?? strategies.FirstOrDefault();

        var version = ReadZapretVersion(folder);
        var hasService = File.Exists(Path.Combine(folder, "service.bat"));
        var valid = hasService && strategies.Length > 0;
        var pathWarning = folder.Any(character => character > 127 || char.IsWhiteSpace(character))
            ? " · проверьте путь: upstream рекомендует папку без кириллицы и пробелов"
            : string.Empty;
        SetZapretStatus(valid
            ? $"Пакет готов{(version is null ? string.Empty : $" · версия {version}")} · стратегий: {strategies.Length}{pathWarning}"
            : "В этой папке не найдены service.bat и general*.bat.");
        StartStrategyButton.IsEnabled = valid && StrategyComboBox.SelectedItem is not null;
        StopStrategyButton.IsEnabled = valid;
        InstallServiceButton.IsEnabled = valid && StrategyComboBox.SelectedItem is not null;
        RemoveServiceButton.IsEnabled = valid;
        GameFilterModeComboBox.IsEnabled = valid;
        GameFilterTcpTextBox.IsEnabled = valid;
        GameFilterUdpTextBox.IsEnabled = valid;
        IpSetModeComboBox.IsEnabled = valid;
        AutoUpdateCheckBox.IsEnabled = valid;
        FakeTargetComboBox.IsEnabled = valid;
        SavePackageSettingsButton.IsEnabled = valid;
        RefreshPackageSettings(valid ? folder : string.Empty);
        RefreshFakeFiles(valid ? folder : string.Empty);
        RefreshServiceStatus();
    }

    private void RefreshPackageSettings(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            GameFilterModeComboBox.SelectedValue = "disabled";
            GameFilterTcpTextBox.Text = "1024-65535";
            GameFilterUdpTextBox.Text = "1024-65535";
            IpSetModeComboBox.SelectedValue = "any";
            AutoUpdateCheckBox.IsChecked = false;
            return;
        }
        var gameSettings = Path.Combine(folder, "utils", "game_filter.enabled");
        var mode = "disabled";
        var tcp = "1024-65535";
        var udp = "1024-65535";
        if (File.Exists(gameSettings))
        {
            foreach (var line in File.ReadLines(gameSettings))
            {
                var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2) continue;
                switch (pair[0].ToLowerInvariant())
                {
                    case "mode": mode = pair[1].ToLowerInvariant(); break;
                    case "tcp": if (pair[1].Length == 0) mode = "tcp"; else if (IsValidPortRange(pair[1])) tcp = pair[1]; break;
                    case "udp": if (pair[1].Length == 0) mode = "udp"; else if (IsValidPortRange(pair[1])) udp = pair[1]; break;
                    case "all": if (pair[1].Equals("", StringComparison.Ordinal)) mode = "all"; break;
                }
            }
        }
        if (mode is not ("disabled" or "all" or "tcp" or "udp")) mode = "disabled";
        GameFilterModeComboBox.SelectedValue = mode;
        GameFilterTcpTextBox.Text = tcp;
        GameFilterUdpTextBox.Text = udp;

        var ipSetPath = Path.Combine(folder, "lists", "ipset-all.txt");
        var ipSetMode = "any";
        if (File.Exists(ipSetPath))
        {
            var lines = File.ReadAllLines(ipSetPath).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            if (lines.Length > 0)
                ipSetMode = lines.Any(line => line.Contains("203.0.113.113/32", StringComparison.Ordinal)) ? "none" : "loaded";
        }
        IpSetModeComboBox.SelectedValue = ipSetMode;
        AutoUpdateCheckBox.IsChecked = _settings.CheckZapretUpdatesBeforeStart;
    }

    private void RefreshFakeFiles(string folder)
    {
        var selected = FakeFileComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(folder))
        {
            FakeFileComboBox.ItemsSource = Array.Empty<string>();
            FakeFileComboBox.IsEnabled = false;
            return;
        }
        var bin = Path.Combine(folder, "bin");
        var fakes = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "*.bin", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null && !name.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()
            : [];
        FakeFileComboBox.ItemsSource = fakes;
        FakeFileComboBox.SelectedItem = fakes.FirstOrDefault(name => name == selected) ?? fakes.FirstOrDefault();
        FakeFileComboBox.IsEnabled = fakes.Length > 0;
    }

    private void RefreshServiceStatus()
    {
        if (ServiceStatusText is null) return;
        var output = QueryWindowsService("zapret");
        if (output is null)
        {
            ServiceStatusText.Text = "Служба zapret: не установлена";
            StartServiceButton.IsEnabled = false;
            StopServiceButton.IsEnabled = false;
            RemoveServiceButton.IsEnabled = false;
            return;
        }
        var state = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ? "работает"
            : output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase) ? "останавливается"
            : output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) ? "запускается"
            : output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ? "остановлена"
            : "установлена";
        var strategy = string.Empty;
        var serviceFolder = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            strategy = key?.GetValue("zapret-discord-youtube") as string ?? string.Empty;
            var imagePath = key?.GetValue("ImagePath") as string;
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                var match = Regex.Match(imagePath, "^\\s*\\\"(?<path>[^\\\"]*winws\\.exe)\\\"|^\\s*(?<path>[^\\s]*winws\\.exe)", RegexOptions.IgnoreCase);
                if (match.Success) serviceFolder = Path.GetDirectoryName(Path.GetDirectoryName(match.Groups["path"].Value)) ?? string.Empty;
            }
        }
        catch { }
        ServiceStatusText.Text = $"Служба zapret: {state}{(string.IsNullOrWhiteSpace(strategy) ? string.Empty : $" · стратегия {strategy}")}{(string.IsNullOrWhiteSpace(serviceFolder) ? string.Empty : $" · {serviceFolder}")}";
        var packageSelected = Directory.Exists(ZapretFolderTextBox.Text.Trim());
        StartServiceButton.IsEnabled = packageSelected && state == "остановлена";
        StopServiceButton.IsEnabled = packageSelected && state == "работает";
        RemoveServiceButton.IsEnabled = packageSelected;
    }

    private static string? QueryWindowsService(string serviceName)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
            var startInfo = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            startInfo.ArgumentList.Add("query");
            startInfo.ArgumentList.Add(serviceName);
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            var output = stdout.Result + Environment.NewLine + stderr.Result;
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
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

    private static string? ReadZapretVersion(string folder)
    {
        try
        {
            var script = Path.Combine(folder, "service.bat");
            if (!File.Exists(script)) return null;
            var firstLines = File.ReadLines(script).Take(8);
            foreach (var line in firstLines)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, "LOCAL_VERSION\\s*=\\s*([^\" ]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    private void SetZapretStatus(string text) => ZapretStatusText.Text = text;

    private void AdapterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CurrentDnsText is null) return;
        if (AdapterComboBox.SelectedItem is NetworkAdapterInfo adapter)
            CurrentDnsText.Text = $"Текущие DNS: {adapter.DnsSummary}";
    }

    private void ProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Ipv4TextBox is null || ProviderComboBox.SelectedItem is not DnsProvider provider) return;
        Ipv4TextBox.Text = provider.Ipv4;
        Ipv6TextBox.Text = provider.Ipv6;
        DohTemplateTextBox.Text = provider.DohTemplate ?? string.Empty;
        DohEnabledCheckBox.IsChecked = !string.IsNullOrWhiteSpace(provider.DohTemplate);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.WhenAll(
            RefreshDnsAiProviderAsync(showError: false),
            CheckNetShiftUpdatesAsync(showError: false));
    }

    private void AppAutoStart_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AppAutoStartCheckBox.IsChecked == true;
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                ?? throw new InvalidOperationException("Не удалось открыть настройки автозапуска Windows.");
            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                    throw new InvalidOperationException("Не удалось определить путь к NetShift.exe.");
                runKey.SetValue("NetShift", $"\"{executable}\"", RegistryValueKind.String);
            }
            else
            {
                runKey.DeleteValue("NetShift", throwOnMissingValue: false);
            }
        }
        catch (Exception error)
        {
            AppAutoStartCheckBox.IsChecked = !enabled;
            MessageBox.Show(this, error.Message, "Автозапуск NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsAppAutoStartEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable) || runKey?.GetValue("NetShift") is not string command) return false;
            var configuredExecutable = command.Trim().Trim('"');
            return Path.GetFullPath(configuredExecutable).Equals(Path.GetFullPath(currentExecutable), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void RefreshDnsServiceStatus()
    {
        if (DnsServiceStatusText is null) return;
        var output = QueryWindowsService("NetShiftDns");
        InstallDnsServiceButton.IsEnabled = NetShiftUpdateService.Repository is not null;
        if (output is null)
        {
            DnsServiceStatusText.Text = "Служба DNS: не установлена · системный DNS остаётся настроенным в Windows";
            RemoveDnsServiceButton.IsEnabled = false;
            return;
        }

        var state = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ? "работает"
            : output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase) ? "останавливается"
            : output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) ? "запускается"
            : output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ? "остановлена"
            : "установлена";
        var startMode = "автозапуск не проверен";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NetShiftDns");
            startMode = Convert.ToInt32(key?.GetValue("Start") ?? 3) == 2 ? "автозапуск Windows включён" : "автозапуск Windows выключен";
        }
        catch { }
        DnsServiceStatusText.Text = $"Служба DNS: {state} · {startMode} · работает независимо от NetShift";
        RemoveDnsServiceButton.IsEnabled = true;
    }

    private async void InstallDnsService_Click(object sender, RoutedEventArgs e)
    {
        if (NetShiftUpdateService.Repository is null)
        {
            MessageBox.Show(this, "Установка службы доступна в релизной сборке NetShift.", "DNS-служба", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        InstallDnsServiceButton.IsEnabled = false;
        try
        {
            DnsServiceStatusText.Text = "Проверяю DNS-службу в последнем релизе NetShift…";
            var package = await NetShiftUpdateService.GetLatestDnsServicePackageAsync(CancellationToken.None);
            var progress = new Progress<double>(value => DnsServiceStatusText.Text = $"Скачиваю DNS-службу {package.Tag}: {value:P0}");
            var downloaded = await NetShiftUpdateService.DownloadDnsServicePackageAsync(package, progress, CancellationToken.None);
            DnsServiceStatusText.Text = "Архив проверен по SHA-256. Windows запросит права администратора для установки автозапускающейся службы.";
            if (RunElevatedWorker("dns-service-install", downloaded.ArchivePath, downloaded.Sha256))
                DnsServiceStatusText.Text = "DNS-служба установлена и настроена на автоматический запуск вместе с Windows.";
        }
        catch (Exception error)
        {
            DnsServiceStatusText.Text = "Установка DNS-службы не завершена.";
            MessageBox.Show(this, error.Message, "DNS-служба", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshDnsServiceStatus();
        }
    }

    private void RemoveDnsService_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Удалить службу NetShift DNS? Системные DNS-адреса останутся настроенными в Windows, но служба больше не будет восстанавливать профиль при запуске компьютера.",
                "Удалить DNS-службу", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        if (RunElevatedWorker("dns-service-remove"))
            DnsServiceStatusText.Text = "DNS-служба удалена. DNS Windows оставлен без изменений.";
        RefreshDnsServiceStatus();
    }

    private void RefreshDnsServiceStatus_Click(object sender, RoutedEventArgs e) => RefreshDnsServiceStatus();

    private async Task CheckNetShiftUpdatesAsync(bool showError)
    {
        if (NetShiftUpdateService.Repository is null)
        {
            UpdateStatusText.Text = $"NetShift {NetShiftUpdateService.CurrentVersion} · автообновление настроено для GitHub-релиза";
            UpdateButton.Content = "Обновление в релизной сборке";
            UpdateButton.IsEnabled = false;
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Проверяю последнюю версию NetShift…";
        try
        {
            _availableUpdate = await NetShiftUpdateService.CheckLatestAsync(CancellationToken.None);
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = $"NetShift {NetShiftUpdateService.CurrentVersion} · установлена последняя версия";
                UpdateButton.Content = "Проверить обновления";
            }
            else
            {
                UpdateStatusText.Text = $"Доступно обновление NetShift {_availableUpdate.Tag}";
                UpdateButton.Content = $"Скачать и установить {_availableUpdate.Tag}";
            }
        }
        catch (Exception error)
        {
            UpdateStatusText.Text = "Не удалось проверить обновления NetShift";
            UpdateButton.Content = "Повторить проверку";
            if (showError)
                MessageBox.Show(this, error.Message, "Обновление NetShift", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            await CheckNetShiftUpdatesAsync(showError: true);
            return;
        }

        var update = _availableUpdate;
        if (MessageBox.Show(this,
                $"Скачать и установить NetShift {update.Tag}? Приложение закроется и автоматически запустится после обновления.",
                "Обновление NetShift", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        UpdateButton.IsEnabled = false;
        try
        {
            UpdateStatusText.Text = $"Скачиваю NetShift {update.Tag}: 0%";
            var progress = new Progress<double>(value => UpdateStatusText.Text = $"Скачиваю NetShift {update.Tag}: {value:P0}");
            var stagedExecutable = await NetShiftUpdateService.DownloadAndExtractAsync(update, progress, CancellationToken.None);
            var targetExecutable = Path.Combine(AppContext.BaseDirectory, "NetShift.exe");
            NetShiftUpdateService.LaunchUpdater(stagedExecutable, targetExecutable, Environment.ProcessId);
            UpdateStatusText.Text = "Обновление подготовлено; NetShift перезапустится автоматически…";
            AllowCloseAndDisposeTrayIcon();
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            UpdateStatusText.Text = "Установка обновления не завершена";
            UpdateButton.IsEnabled = true;
            MessageBox.Show(this, error.Message, "Обновление NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshDnsAi_Click(object sender, RoutedEventArgs e) => await RefreshDnsAiProviderAsync(showError: true);

    private async Task RefreshDnsAiProviderAsync(bool showError)
    {
        RefreshDnsAiButton.IsEnabled = false;
        DnsProviderStatusText.Text = "DNS-AI: запрашиваю актуальные настройки сайта…";
        try
        {
            var updatedProvider = await DnsAiProviderService.GetCurrentAsync();
            var providerIndex = _dnsProviders.ToList().FindIndex(provider => provider.Name.Equals("DNS-AI", StringComparison.OrdinalIgnoreCase));
            if (providerIndex < 0) return;

            var wasSelected = ProviderComboBox.SelectedItem is DnsProvider selected && selected.Name.Equals("DNS-AI", StringComparison.OrdinalIgnoreCase);
            _dnsProviders[providerIndex] = updatedProvider;
            if (wasSelected) ProviderComboBox.SelectedItem = updatedProvider;
            DnsProviderStatusText.Text = $"DNS-AI: настройки обновлены с dns-ai.ru · {DateTime.Now:dd.MM.yyyy HH:mm}";
        }
        catch (Exception error)
        {
            DnsProviderStatusText.Text = "DNS-AI: сайт недоступен; используются встроенные резервные значения.";
            if (showError)
                MessageBox.Show(this, $"Не удалось обновить данные DNS-AI. Используются резервные значения.\n\n{error.Message}",
                    "DNS-AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshDnsAiButton.IsEnabled = true;
        }
    }

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e) => RefreshAdapters();

    private void ApplyDns_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterComboBox.SelectedItem is not NetworkAdapterInfo adapter)
        {
            MessageBox.Show(this, "Сначала выберите сетевой адаптер.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ipv4Addresses = SplitAddresses(Ipv4TextBox.Text);
        var ipv6Addresses = SplitAddresses(Ipv6TextBox.Text);
        var addresses = ipv4Addresses.Concat(ipv6Addresses).ToArray();
        var dohTemplate = DohEnabledCheckBox.IsChecked == true ? DohTemplateTextBox.Text.Trim() : string.Empty;
        if (addresses.Length == 0 || !HasFamily(ipv4Addresses, AddressFamily.InterNetwork) ||
            !HasFamily(ipv6Addresses, AddressFamily.InterNetworkV6))
        {
            MessageBox.Show(this, "Проверьте адреса: в поле IPv4 укажите только IPv4, в поле IPv6 — только IPv6. Нужен хотя бы один адрес.",
                "Проверьте адреса", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (DohEnabledCheckBox.IsChecked == true &&
            (!Uri.TryCreate(dohTemplate, UriKind.Absolute, out var dohUri) || dohUri.Scheme != Uri.UriSchemeHttps ||
             !string.IsNullOrEmpty(dohUri.UserInfo) || !string.IsNullOrEmpty(dohUri.Fragment)))
        {
            MessageBox.Show(this, "Укажите корректный HTTPS-шаблон DoH.", "Проверьте шаблон DoH", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(this,
            $"Заменить DNS на выбранном интерфейсе «{adapter.Name}»?\n\n{string.Join("\n", addresses)}" +
            (dohTemplate.Length == 0 ? string.Empty : $"\n\nDoH: {dohTemplate}\nОткат на незашифрованный DNS выключен.") +
            "\n\nWindows запросит подтверждение администратора.",
            "Применить DNS", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK) return;

        if (RunElevatedWorker("dns-apply", adapter.InterfaceIndex.ToString(), string.Join(',', addresses), dohTemplate))
        {
            MessageBox.Show(this, "DNS-адреса применены и сохранены в Windows. Установленная DNS-служба восстановит этот профиль при запуске компьютера.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAdapters();
        }
    }

    private void ResetDns_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterComboBox.SelectedItem is not NetworkAdapterInfo adapter)
        {
            MessageBox.Show(this, "Сначала выберите сетевой адаптер.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(this,
            $"Вернуть DNS по DHCP для «{adapter.Name}»? Это заменит вручную заданные DNS-адреса на автоматические.",
            "Вернуть DHCP", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK) return;

        if (RunElevatedWorker("dns-reset", adapter.InterfaceIndex.ToString()))
        {
            MessageBox.Show(this, "Для интерфейса включено получение DNS по DHCP.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAdapters();
        }
    }

    private static IEnumerable<string> SplitAddresses(string value) => value
        .Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(address => IPAddress.TryParse(address, out var parsed) ? parsed.ToString() : address);

    private static bool HasFamily(IEnumerable<string> addresses, AddressFamily expectedFamily) =>
        addresses.All(address => IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == expectedFamily);

    private void ChooseZapretFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку Flowseal/zapret-discord-youtube",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;
        ZapretFolderTextBox.Text = dialog.FolderName;
        _settings.ZapretFolder = dialog.FolderName;
        SaveSettings();
        UpdateZapretInventory();
    }

    private async void StartStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (StrategyComboBox.SelectedItem is not StrategyItem strategy) return;
        StartStrategyButton.IsEnabled = false;
        try
        {
            if (AutoUpdateCheckBox.IsChecked == true)
            {
                try
                {
                    var latest = await ZapretReleaseService.GetLatestReleaseInfoAsync(CancellationToken.None);
                    var installed = ReadZapretVersion(ZapretFolderTextBox.Text.Trim());
                    if (installed is not null && !installed.Equals(latest.Version, StringComparison.OrdinalIgnoreCase) &&
                        MessageBox.Show(this,
                            $"Доступен релиз {latest.Tag}, а установлена версия {installed}. Запустить установленную стратегию? Нажмите «Нет», чтобы сначала скачать релиз через NetShift.",
                            "Доступно обновление", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                        return;
                }
                catch (Exception error)
                {
                    if (MessageBox.Show(this, $"Не удалось проверить релиз GitHub:\n{error.Message}\n\nЗапустить установленную стратегию?",
                            "Проверка релиза недоступна", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                }
            }
            var confirmation = MessageBox.Show(this,
                $"Запустить {strategy.Name}? Скрипт Flowseal изменит обработку сетевого трафика и может включить TCP timestamps. Windows запросит права администратора.",
                "Запуск zapret", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.OK) return;
            RunElevatedWorker("zapret-start", strategy.Path);
            RefreshServiceStatus();
        }
        finally { StartStrategyButton.IsEnabled = StrategyComboBox.SelectedItem is StrategyItem; }
    }

    private void StopStrategy_Click(object sender, RoutedEventArgs e)
    {
        var folder = ZapretFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) return;
        var confirmation = MessageBox.Show(this,
            "Остановить winws.exe, запущенный из выбранной папки? Зарегистрированная служба Windows этим действием не удаляется.",
            "Остановить ручной запуск", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation == MessageBoxResult.OK)
            RunElevatedWorker("zapret-stop", folder);
    }

    private void InstallService_Click(object sender, RoutedEventArgs e)
    {
        if (StrategyComboBox.SelectedItem is not StrategyItem strategy) return;
        var folder = Path.GetDirectoryName(strategy.Path)!;
        var isInstalled = QueryWindowsService("zapret") is not null;
        var message = isInstalled
            ? $"Заменить установленную службу zapret стратегией {strategy.Name}? NetShift изменит её только если она установлена из выбранной папки или папки NetShift. Потребуются права администратора."
            : $"Установить стратегию {strategy.Name} как автоматическую службу Windows?\n\n{folder}\n\nПотребуются права администратора.";
        if (MessageBox.Show(this, message, "Установить службу zapret", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (RunElevatedWorker("zapret-service-install", strategy.Path)) RefreshServiceStatus();
    }

    private void StartService_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (RunElevatedWorker("zapret-service-start", folder)) RefreshServiceStatus();
    }

    private void StopService_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (MessageBox.Show(this, "Остановить службу zapret? Это временно прекратит работу выбранной стратегии.",
                "Остановить службу", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (RunElevatedWorker("zapret-service-stop", folder)) RefreshServiceStatus();
    }

    private void RemoveService_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (QueryWindowsService("zapret") is null) { RefreshServiceStatus(); return; }
        if (MessageBox.Show(this,
                $"Остановить и удалить службу zapret из выбранной папки или папки NetShift?\n\nТекущий пакет: {folder}\n\nСлужбы и процессы других программ не затрагиваются.",
                "Удалить службу zapret", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (RunElevatedWorker("zapret-service-remove", folder)) RefreshServiceStatus();
    }

    private void RefreshServiceStatus_Click(object sender, RoutedEventArgs e) => RefreshServiceStatus();

    private void SavePackageSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (GameFilterModeComboBox.SelectedValue is not string mode || IpSetModeComboBox.SelectedValue is not string ipSetMode ||
            !IsValidPortRange(GameFilterTcpTextBox.Text) || !IsValidPortRange(GameFilterUdpTextBox.Text))
        {
            MessageBox.Show(this, "Укажите диапазоны TCP и UDP. Примеры: 80, 1024-65535 или 1024-1934,1936-65535.",
                "Проверьте параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ipSetMode == "loaded" && ReadIpSetMode(folder) != "loaded" && !File.Exists(Path.Combine(folder, "lists", "ipset-all.txt.backup")))
        {
            MessageBox.Show(this, "Резервного IPSet нет. Сначала нажмите «Обновить IPSet», затем выберите загруженный список.",
                "Нет списка IPSet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                "Сохранить игровой фильтр, режим IPSet и проверку последнего релиза перед запуском? Изменение фильтра вступит в силу после перезапуска стратегии.",
                "Сохранить параметры zapret", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (RunElevatedWorker("zapret-settings-save", folder, mode, GameFilterTcpTextBox.Text.Trim(), GameFilterUdpTextBox.Text.Trim(), ipSetMode))
        {
            _settings.CheckZapretUpdatesBeforeStart = AutoUpdateCheckBox.IsChecked == true;
            SaveSettings();
            SetToolsOutput("Параметры zapret сохранены. Игровой фильтр применяется после перезапуска стратегии.");
            UpdateZapretInventory();
        }
    }

    private static string ReadIpSetMode(string folder)
    {
        var path = Path.Combine(folder, "lists", "ipset-all.txt");
        if (!File.Exists(path)) return "any";
        var lines = File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0) return "any";
        return lines.Any(line => line.Contains("203.0.113.113/32", StringComparison.Ordinal)) ? "none" : "loaded";
    }

    private async void UpdateIpSet_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        try
        {
            DownloadStatusText.Text = "Загружаю список IPSet с официального репозитория…";
            var content = await ZapretReleaseService.DownloadIpSetAsync(CancellationToken.None);
            var path = ZapretReleaseService.CacheText($"ipset-{Guid.NewGuid():N}.txt", content);
            if (RunElevatedWorker("zapret-ipset-update", folder, path, ComputeFileSha256(path)))
            {
                SetToolsOutput($"Список IPSet обновлён: {content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length:N0} строк. Предыдущий список сохранён рядом.");
                UpdateZapretInventory();
            }
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось обновить IPSet", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { DownloadStatusText.Text = ""; }
    }

    private void ReplaceFake_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder) || FakeTargetComboBox.SelectedValue is not string target || FakeFileComboBox.SelectedItem is not string fakeFile) return;
        var targetName = target == "discord" ? "Discord UDP" : "игровой UDP";
        if (MessageBox.Show(this, $"Заменить активный fake-файл для {targetName} на {fakeFile}? Текущий активный файл будет перезаписан.",
                "Заменить fake-файл", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (RunElevatedWorker("zapret-fake-replace", folder, target, fakeFile)) SetToolsOutput($"Активный файл для {targetName} заменён на {fakeFile}.");
    }

    private async void DownloadLatest_Click(object sender, RoutedEventArgs e)
    {
        DownloadLatestButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        try
        {
            DownloadStatusText.Text = "Проверяю последний GitHub-релиз и загружаю ZIP…";
            var progress = new Progress<double>(value => DownloadProgressBar.Value = value);
            var release = await ZapretReleaseService.DownloadLatestAsync(progress, CancellationToken.None);
            var installPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "NetShift", "zapret-" + release.Version);
            if (Directory.Exists(installPath))
            {
                if (!File.Exists(Path.Combine(installPath, "service.bat")) || !File.Exists(Path.Combine(installPath, "bin", "winws.exe")) ||
                    !Directory.EnumerateFiles(installPath, "general*.bat", SearchOption.TopDirectoryOnly).Any())
                    throw new InvalidDataException($"Папка {installPath} уже существует, но пакет неполный. Архив сохранён; выберите другой путь или исправьте папку.");
                File.Delete(release.ArchivePath);
                SelectZapretFolder(installPath);
                DownloadStatusText.Text = $"Последний релиз {release.Tag} уже установлен; выбрана его папка.";
                return;
            }

            DownloadStatusText.Text = $"ZIP {release.Tag} загружен и проверен SHA-256. Распаковываю…";
            if (RunElevatedWorker("zapret-package-install", release.ArchivePath, release.Tag, release.Sha256))
            {
                File.Delete(release.ArchivePath);
                SelectZapretFolder(installPath);
                DownloadStatusText.Text = $"Релиз {release.Tag} установлен в {installPath}.";
            }
            else DownloadStatusText.Text = "Установка не завершена; ZIP оставлен в локальных загрузках NetShift.";
        }
        catch (Exception error)
        {
            DownloadStatusText.Text = "Загрузка или проверка релиза не завершилась.";
            MessageBox.Show(this, error.Message, "Не удалось скачать релиз zapret", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DownloadLatestButton.IsEnabled = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void SelectZapretFolder(string folder)
    {
        ZapretFolderTextBox.Text = folder;
        _settings.ZapretFolder = folder;
        SaveSettings();
        UpdateZapretInventory();
    }

    private async void CheckRelease_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var release = await ZapretReleaseService.GetLatestReleaseInfoAsync(CancellationToken.None);
            var installed = ReadZapretVersion(ZapretFolderTextBox.Text.Trim());
            var message = installed is null ? $"Последний релиз GitHub: {release.Tag}."
                : installed.Equals(release.Version, StringComparison.OrdinalIgnoreCase) ? $"Установлена последняя версия {release.Tag}."
                : $"Последний релиз GitHub: {release.Tag}.\nВ выбранной папке установлена версия {installed}.";
            DownloadStatusText.Text = message.Replace('\n', ' ');
            MessageBox.Show(this, message, "Проверка обновлений", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось проверить релиз", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        var lines = new List<string> { "Диагностика NetShift (только чтение)", $"Папка: {folder}" };
        var bfe = QueryWindowsService("BFE");
        lines.Add(bfe?.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) == true ? "✓ Base Filtering Engine работает." : "! Base Filtering Engine не запущен или недоступен.");
        var zapret = QueryWindowsService("zapret");
        lines.Add(zapret is null ? "• Служба zapret не установлена." : zapret.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ? "✓ Служба zapret работает." : "• Служба zapret установлена, но остановлена.");
        var windivert = QueryWindowsService("WinDivert");
        lines.Add(windivert is null ? "• Служба WinDivert не зарегистрирована." : "• Служба WinDivert зарегистрирована; NetShift не меняет её состояние.");
        var tcpSettings = RunReadOnlyCommand("netsh.exe", "interface", "tcp", "show", "global");
        var timestampLine = tcpSettings.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
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
        var services = QueryAllServices();
        foreach (var knownConflict in new[] { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2", "Killer", "SmartByte", "TracSrvWrapper", "EPWD", "Intel Connectivity Network Service" })
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
        SetToolsOutput(string.Join(Environment.NewLine, lines));
    }

    private static string QueryAllServices()
    {
        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "query", "type=", "service", "state=", "all" }) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch { return string.Empty; }
    }

    private static string RunReadOnlyCommand(string executable, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), executable))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
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

    private void RunUpstreamTests_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        var script = Path.Combine(folder, "utils", "test zapret.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(this, "В выбранном пакете не найден официальный скрипт utils/test zapret.ps1.", "Тесты недоступны", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                "Откроется PowerShell с официальными тестами Flowseal. Скрипт проверяет сетевые адреса, может временно менять IPSet и останавливать/перезапускать winws, затем сохраняет отчёт. Для записи в папку пакета потребуется подтверждение администратора.",
                "Запустить тесты", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (RunElevatedWorker("zapret-run-tests", folder))
            SetToolsOutput("Официальный тестовый скрипт запущен в отдельном окне PowerShell.");
    }

    private async void CheckHosts_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out _)) return;
        try
        {
            DownloadStatusText.Text = "Загружаю официальный hosts-файл…";
            var content = await ZapretReleaseService.DownloadHostsAsync(CancellationToken.None);
            var downloadedPath = ZapretReleaseService.CacheText($"hosts-{Guid.NewGuid():N}.txt", content);
            var entries = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
            var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");
            var existing = File.ReadAllLines(hostsPath, Encoding.UTF8).Select(NormalizeHostsLine).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = entries.Where(line => !existing.Contains(NormalizeHostsLine(line))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length == 0)
            {
                SetToolsOutput($"Hosts уже содержит все {entries.Length} записей из официального файла.");
                DownloadStatusText.Text = "Hosts актуален";
                return;
            }
            var preview = string.Join(Environment.NewLine, missing.Take(8));
            var choice = MessageBox.Show(this,
                $"В системный hosts будут добавлены отсутствующие записи: {missing.Length} из {entries.Length}. Это изменит разрешение адресов перечисленных доменов. NetShift сначала создаст резервную копию.\n\n{preview}{(missing.Length > 8 ? "\n…" : string.Empty)}\n\nДобавить?",
                "Обновить hosts", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) { DownloadStatusText.Text = "Обновление hosts отменено."; return; }
            if (RunElevatedWorker("zapret-hosts-append", downloadedPath, ComputeFileSha256(downloadedPath)))
            {
                SetToolsOutput($"Добавлено записей hosts: {missing.Length}. Резервная копия создана рядом с системным hosts.");
                DownloadStatusText.Text = "Hosts обновлён";
            }
        }
        catch (Exception error)
        {
            DownloadStatusText.Text = "Проверка hosts не завершилась.";
            MessageBox.Show(this, error.Message, "Не удалось проверить hosts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreHostsBackup_Click(object sender, RoutedEventArgs e)
    {
        var hostsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc");
        var backup = Directory.Exists(hostsDirectory)
            ? Directory.GetFiles(hostsDirectory, "hosts.NetShift-*.bak").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (backup is null)
        {
            MessageBox.Show(this, "Резервные копии hosts, созданные NetShift, не найдены.", "Нет резервной копии", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Восстановить системный hosts из последней резервной копии? Текущий hosts также будет сохранён перед восстановлением.\n\n{Path.GetFileName(backup)}",
                "Восстановить hosts", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (RunElevatedWorker("zapret-hosts-restore", backup, ComputeFileSha256(backup)))
                SetToolsOutput($"Системный hosts восстановлен из {Path.GetFileName(backup)}. Предыдущее содержимое также сохранено.");
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось восстановить hosts", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static string NormalizeHostsLine(string line) => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private bool TryGetZapretFolder(out string folder)
    {
        folder = ZapretFolderTextBox.Text.Trim();
        if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "service.bat")) || !File.Exists(Path.Combine(folder, "bin", "winws.exe")))
        {
            MessageBox.Show(this, "Сначала укажите папку с установленным пакетом zapret или скачайте последний релиз.",
                "Пакет не выбран", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    private void SetToolsOutput(string value) => ToolsOutputTextBox.Text = value;

    private bool RunElevatedWorker(params string[] arguments)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            MessageBox.Show(this, "Не удалось определить путь к приложению.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        try
        {
            var allArguments = new[] { "--admin" }.Concat(arguments);
            var startInfo = new ProcessStartInfo(executable)
            {
                Arguments = string.Join(" ", allArguments.Select(QuoteWindowsArgument)),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            process.WaitForExit();
            if (process.ExitCode == 0) return true;
            MessageBox.Show(this, "Действие завершилось с ошибкой. Проверьте сообщение административного процесса и статус сети.",
                "NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            // The user declined the Windows elevation prompt.
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return false;
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
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

    private sealed class AppSettings
    {
        public AppSettings() { }
        public string? ZapretFolder { get; set; }
        public bool CheckZapretUpdatesBeforeStart { get; set; }
    }

    private sealed record PackageChoice(string Name, string Value);
    private sealed record StrategyItem(string Name, string Path);
}
