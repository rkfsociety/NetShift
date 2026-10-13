using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetShift.App.Models;
using NetShift.App.Services;
using Microsoft.Win32;

namespace NetShift.App;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions ZapretProgressJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetShift", "settings.json");
    private readonly ObservableCollection<DnsProvider> _dnsProviders = new(DnsProvider.Presets);
    private IReadOnlyList<NetworkAdapterInfo> _adapters = [];
    private AppSettings _settings = new();
    private NetShiftUpdate? _availableUpdate;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private LegacyDohProxy? _legacyDohProxy;
    private int? _legacyAdapterIndex;
    private string? _legacyDohEndpoint;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _closeInProgress;
    private bool _trayNoticeShown;
    private bool _trayIconDisposed;
    private bool _isZapretStrategyTestRunning;
    private string? _lastZapretTestProgressJson;

    public MainWindow()
    {
        InitializeComponent();
        AboutVersionText.Text = $"Версия NetShift {NetShiftUpdateService.CurrentVersion}";
        _trayIcon = CreateTrayIcon();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Closing += MainWindow_Closing;
        Closed += (_, _) => DisposeTrayIcon();
        System.Windows.Application.Current.SessionEnding += (_, _) => Dispatcher.BeginInvoke(ExitApplication);
        ProviderComboBox.ItemsSource = _dnsProviders;
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
        ProviderComboBox.SelectedItem = _dnsProviders.FirstOrDefault(provider =>
            provider.Name.Equals(_settings.DnsProviderName ?? "DNS-AI", StringComparison.OrdinalIgnoreCase)) ?? _dnsProviders[0];
        LegacyModeRadio.IsChecked = !_settings.NativeDnsMode;
        NativeModeRadio.IsChecked = _settings.NativeDnsMode;
        SupportIPv6CheckBox.IsChecked = _settings.SupportIPv6;
        ConfigureVpnAdaptersCheckBox.IsChecked = _settings.ConfigureVpnAdapters;
        AppAutoStartCheckBox.IsChecked = IsAppAutoStartEnabled();
        InstallDnsServiceButton.IsEnabled = NetShiftBootstrapper.HasEmbeddedDnsService();
        _ = RefreshDnsServiceStatusAsync();
        RefreshAdapters(selectFirst: true);
        RefreshZapretFolder();
    }

    private void NavigatePage_Click(object sender, RoutedEventArgs e)
    {
        var page = (sender as FrameworkElement)?.Tag?.ToString() ?? "dns";
        DnsPage.Visibility = page == "dns" ? Visibility.Visible : Visibility.Collapsed;
        ZapretPage.Visibility = page == "zapret" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = page switch
        {
            "zapret" => "zapret",
            "settings" => "Настройки",
            "about" => "О программе",
            _ => "DNS"
        };

        SetNavigationButtonState(DnsNavigationButton, page == "dns");
        SetNavigationButtonState(ZapretNavigationButton, page == "zapret");
        SetNavigationButtonState(SettingsNavigationButton, page == "settings");
        SetNavigationButtonState(AboutNavigationButton, page == "about");
    }

    private void SetNavigationButtonState(System.Windows.Controls.Button button, bool isSelected)
    {
        var transparentIdle = button == SettingsNavigationButton || button == AboutNavigationButton;
        button.Background = (System.Windows.Media.Brush)FindResource(isSelected
            ? "NavSelectedBrush"
            : transparentIdle ? "SidebarBrush" : "PanelBrush");
        button.BorderBrush = isSelected
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = isSelected
            ? System.Windows.Media.Brushes.White
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyDarkTitleBar()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Открыть NetShift");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(ExitApplication);
        menu.Items.Add(openItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        var icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "NetShift — DNS и zapret",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? System.Drawing.SystemIcons.Application,
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
        if (!_exitRequested)
        {
            Hide();
            if (!_trayNoticeShown)
            {
                _trayNoticeShown = true;
                _trayIcon.ShowBalloonTip(3500, "NetShift продолжает работать", "Окно скрыто в системный трей. Откройте значок NetShift, чтобы вернуться.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            return;
        }
        if (_closeInProgress) return;
        _closeInProgress = true;
        _ = CompleteCloseAsync();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private async Task CompleteCloseAsync()
    {
        await Task.Yield();
        try
        {
            if (!await StopLegacyDnsAndRestoreAsync())
            {
                _exitRequested = false;
                ShowFromTray();
                ThemedDialog.Show(this, "Не удалось вернуть DNS адаптера. NetShift останется запущен, чтобы не потерять локальный DNS-прокси.",
                    "Legacy DNS ещё активен", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _allowClose = true;
            DisposeTrayIcon();
            Close();
        }
        catch (Exception error)
        {
            _allowClose = false;
            _exitRequested = false;
            ShowFromTray();
            ThemedDialog.Show(this, error.Message, "Не удалось безопасно завершить NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
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
        _adapters = NetworkService.GetAdapters(_settings.ConfigureVpnAdapters);
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
        var strategies = RefreshStrategyItems();
        var folder = ZapretFolderTextBox.Text.Trim();
        var version = ReadZapretVersion(folder);
        var hasService = File.Exists(Path.Combine(folder, "service.bat"));
        var valid = hasService && strategies.Length > 0;
        var pathWarning = folder.Any(character => character > 127 || char.IsWhiteSpace(character))
            ? " · проверьте путь: upstream рекомендует папку без кириллицы и пробелов"
            : string.Empty;
        SetZapretStatus(valid
            ? $"Пакет готов{(version is null ? string.Empty : $" · версия {version}")} · стратегий: {strategies.Length}{pathWarning}"
            : "В этой папке не найдены service.bat и general*.bat.");
        StartStrategyButton.IsEnabled = valid && StrategyComboBox.SelectedItem is not null && !_isZapretStrategyTestRunning;
        StopStrategyButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        InstallServiceButton.IsEnabled = valid && StrategyComboBox.SelectedItem is not null && !_isZapretStrategyTestRunning;
        RemoveServiceButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        GameFilterModeComboBox.IsEnabled = valid && !_isZapretStrategyTestRunning;
        GameFilterTcpTextBox.IsEnabled = valid && !_isZapretStrategyTestRunning;
        GameFilterUdpTextBox.IsEnabled = valid && !_isZapretStrategyTestRunning;
        IpSetModeComboBox.IsEnabled = valid && !_isZapretStrategyTestRunning;
        AutoUpdateCheckBox.IsEnabled = valid && !_isZapretStrategyTestRunning;
        FakeTargetComboBox.IsEnabled = valid && !_isZapretStrategyTestRunning;
        FakeFileComboBox.IsEnabled = valid && !_isZapretStrategyTestRunning && FakeFileComboBox.Items.Count > 0;
        SavePackageSettingsButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        UpdateIpSetButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        CheckReleaseButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        ReplaceFakeButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        RunDiagnosticsButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        RunUpstreamTestsButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        CheckHostsButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        RestoreHostsButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        StrategyComboBox.IsEnabled = valid && !_isZapretStrategyTestRunning;
        CheckActiveStrategyButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        DownloadLatestButton.IsEnabled = !_isZapretStrategyTestRunning;
        ChooseZapretFolderButton.IsEnabled = !_isZapretStrategyTestRunning;
        RefreshPackageSettings(valid ? folder : string.Empty);
        RefreshFakeFiles(valid ? folder : string.Empty);
        _ = RefreshServiceStatusAsync();
    }

    private string[] RefreshStrategyItems()
    {
        var folder = ZapretFolderTextBox.Text.Trim();
        var strategies = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, Comparer<string>.Create(ZapretStrategyComparer.ComparePaths))
                .ToArray()
            : [];

        var selectedPath = StrategyComboBox.SelectedValue as string;
        StrategyComboBox.ItemsSource = strategies.Select(path =>
        {
            var fullPath = Path.GetFullPath(path);
            var hasHealth = _settings.ZapretStrategyHealth.TryGetValue(HealthKey(fullPath), out var isHealthy);
            var isRecommended = fullPath.Equals(_settings.RecommendedZapretStrategyPath, StringComparison.OrdinalIgnoreCase);
            return new StrategyItem(Path.GetFileName(path), fullPath, hasHealth ? isHealthy : null, isRecommended);
        }).ToArray();
        StrategyComboBox.SelectedValuePath = nameof(StrategyItem.Path);
        StrategyComboBox.SelectedValue = strategies.FirstOrDefault(path => path == selectedPath) ?? strategies.FirstOrDefault();
        return strategies;
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
            foreach (var line in File.ReadLines(ipSetPath))
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.Length == 0) continue;
                if (trimmedLine.Contains("203.0.113.113/32", StringComparison.Ordinal))
                {
                    ipSetMode = "none";
                    break;
                }
                ipSetMode = "loaded";
            }
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
        FakeFileComboBox.IsEnabled = fakes.Length > 0 && !_isZapretStrategyTestRunning;
    }

    private async Task RefreshServiceStatusAsync()
    {
        if (ServiceStatusText is null) return;
        RefreshManualStrategyStatus();
        var output = await QueryWindowsServiceAsync("zapret");
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
        if (_isZapretStrategyTestRunning)
        {
            StartStrategyButton.IsEnabled = false;
            StopStrategyButton.IsEnabled = false;
            InstallServiceButton.IsEnabled = false;
            StartServiceButton.IsEnabled = false;
            StopServiceButton.IsEnabled = false;
            RemoveServiceButton.IsEnabled = false;
        }
    }

    private void RefreshManualStrategyStatus()
    {
        var strategies = Process.GetProcessesByName("winws")
            .Select(process =>
            {
                using (process)
                {
                    try
                    {
                        const string titlePrefix = "zapret: ";
                        var title = process.MainWindowTitle.Trim();
                        return title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase)
                            ? title[titlePrefix.Length..]
                            : null;
                    }
                    catch (InvalidOperationException) { return null; }
                    catch (System.ComponentModel.Win32Exception) { return null; }
                }
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (strategies.Length == 0)
        {
            ManualStrategyStatusText.Text = "Ручной запуск стратегии: не активен";
            ManualStrategyStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            return;
        }

        ManualStrategyStatusText.Text = $"Ручная стратегия работает: {string.Join(", ", strategies)}";
        ManualStrategyStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
    }

    private static async Task<string?> QueryWindowsServiceAsync(string serviceName)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
            var result = await ProcessExecutionService.CaptureAsync(path, ["query", serviceName], TimeSpan.FromSeconds(15));
            var output = result.StandardOutput + Environment.NewLine + result.StandardError;
            return result.ExitCode == 0 ? output : null;
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
        DnsAddressSettingsPanel.Visibility = provider.RequiresManualAddressInput ? Visibility.Visible : Visibility.Collapsed;
        DnsDohSettingsPanel.Visibility = provider.RequiresManualAddressInput ? Visibility.Visible : Visibility.Collapsed;
        _settings.DnsProviderName = provider.Name;
        SaveSettings();
        var ipv4 = SplitAddressPair(provider.Ipv4);
        var ipv6 = SplitAddressPair(provider.Ipv6);
        Ipv4TextBox.Text = ipv4.Primary;
        Ipv4AlternateTextBox.Text = ipv4.Alternate;
        Ipv6TextBox.Text = ipv6.Primary;
        Ipv6AlternateTextBox.Text = ipv6.Alternate;
        DohTemplateTextBox.Text = provider.DohTemplate ?? string.Empty;
        DohEnabledCheckBox.IsChecked = !string.IsNullOrWhiteSpace(provider.DohTemplate);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        using (var legacyProfile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\NetShift\LegacyDns"))
        {
            if (Convert.ToInt32(legacyProfile?.GetValue("Enabled", 0) ?? 0) == 1)
            {
                if (!await RunElevatedWorkerAsync("dns-legacy-restore"))
                {
                    ThemedDialog.Show(this, "Сначала восстановите предыдущие DNS-настройки Legacy, чтобы не потерять подключение.",
                        "Нужно восстановить DNS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        if (NetShiftBootstrapper.HasEmbeddedDnsService() && !IsDnsServiceCurrent())
        {
            DnsServiceStatusText.Text = await QueryWindowsServiceAsync("NetShiftDns") is null
                ? "Первый запуск: устанавливаю службу DNS. Windows попросит подтвердить системную службу."
                : "Обновляю DNS-службу. Текущие DNS-адреса в Windows останутся активны.";
            await RunElevatedWorkerAsync("dns-service-install-embedded");
            await RefreshDnsServiceStatusAsync();
        }

        var dnsAiRefresh = RefreshDnsAiProviderAsync(showError: false);
        var updateCheck = CheckNetShiftUpdatesAsync(showError: false);

        if (!_settings.InitialZapretStrategyCheckCompleted)
        {
            var folder = ZapretFolderTextBox.Text.Trim();
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly).Any())
                await CheckZapretStrategiesAsync(isInitialCheck: true);
            else
            {
                ZapretHealthStatusText.Text = "Сначала скачайте пакет zapret, затем NetShift проверит его стратегии.";
                ZapretTestPhaseText.Text = "Ожидает пакет";
                ZapretTestStrategyText.Text = "Проверка запускается до установки стратегии и не требует работающей службы.";
            }
        }

        await Task.WhenAll(dnsAiRefresh, updateCheck);
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
            ThemedDialog.Show(this, error.Message, "Автозапуск NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private async Task RefreshDnsServiceStatusAsync()
    {
        if (DnsServiceStatusText is null) return;
        var output = await QueryWindowsServiceAsync("NetShiftDns");
        InstallDnsServiceButton.IsEnabled = NetShiftBootstrapper.HasEmbeddedDnsService();
        if (output is null)
        {
            InstallDnsServiceButton.Visibility = Visibility.Visible;
            DnsServiceStatusText.Text = "Не установлена";
            DnsServiceIndicator.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 181, 71));
            RemoveDnsServiceButton.IsEnabled = false;
            return;
        }

        InstallDnsServiceButton.Visibility = Visibility.Collapsed;
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
        DnsServiceStatusText.Text = state switch
        {
            "работает" => startMode.Contains("включён", StringComparison.Ordinal) ? "Работает · автозапуск Windows" : "Работает · автозапуск выключен",
            "остановлена" => "Остановлена",
            "запускается" => "Запускается",
            "останавливается" => "Останавливается",
            _ => "Установлена"
        };
        DnsServiceIndicator.Fill = state == "работает"
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 217, 130))
            : state is "запускается" or "останавливается"
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 181, 71))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(235, 82, 82));
        if (!IsDnsServiceCurrent()) DnsServiceStatusText.Text += " · обновить";
        RemoveDnsServiceButton.IsEnabled = true;
    }

    private static bool IsDnsServiceCurrent()
    {
        if (!NetShiftBootstrapper.HasEmbeddedDnsService()) return true;
        var expectedVersion = typeof(MainWindow).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals("NetShiftDnsServiceVersion", StringComparison.Ordinal))?.Value;
        if (string.IsNullOrWhiteSpace(expectedVersion)) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\NetShift\DnsService");
            return string.Equals(key?.GetValue("Version") as string, expectedVersion, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async void InstallDnsService_Click(object sender, RoutedEventArgs e)
    {
        if (!NetShiftBootstrapper.HasEmbeddedDnsService())
        {
            ThemedDialog.Show(this, "В этой сборке нет встроенного файла DNS-службы. Скачайте приложение из релиза NetShift.", "DNS-служба", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        InstallDnsServiceButton.IsEnabled = false;
        try
        {
            DnsServiceStatusText.Text = "Устанавливаю встроенную DNS-службу. Windows запросит права администратора.";
            if (await RunElevatedWorkerAsync("dns-service-install-embedded"))
                DnsServiceStatusText.Text = "DNS-служба установлена и настроена на автоматический запуск вместе с Windows.";
        }
        catch (Exception error)
        {
            DnsServiceStatusText.Text = "Установка DNS-службы не завершена.";
            ThemedDialog.Show(this, error.Message, "DNS-служба", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            await RefreshDnsServiceStatusAsync();
        }
    }

    private async void RemoveDnsService_Click(object sender, RoutedEventArgs e)
    {
        if (ThemedDialog.Show(this,
                "Удалить службу NetShift DNS? Системные DNS-адреса останутся настроенными в Windows, но служба больше не будет восстанавливать профиль при запуске компьютера.",
                "Удалить DNS-службу", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        if (await RunElevatedWorkerAsync("dns-service-remove"))
            DnsServiceStatusText.Text = "DNS-служба удалена. DNS Windows оставлен без изменений.";
        await RefreshDnsServiceStatusAsync();
    }

    private async void RefreshDnsServiceStatus_Click(object sender, RoutedEventArgs e) => await RefreshDnsServiceStatusAsync();

    private async Task<NetShiftUpdate?> CheckNetShiftUpdatesAsync(bool showError)
    {
        if (NetShiftUpdateService.Repository is null)
        {
            UpdateStatusText.Text = $"Текущая версия: {NetShiftUpdateService.CurrentVersion}";
            UpdateButton.Content = "Обновление в релизной сборке";
            UpdateButton.IsEnabled = false;
            UpdateButton.ToolTip = null;
            Grid.SetColumnSpan(UpdateButton, 2);
            InstallUpdateButton.Visibility = Visibility.Collapsed;
            _availableUpdate = null;
            return null;
        }

        UpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(UpdateButton, 2);
        UpdateButton.Content = "Проверка…";
        _availableUpdate = null;
        UpdateStatusText.Text = "Проверяю последнюю версию NetShift…";
        try
        {
            _availableUpdate = await NetShiftUpdateService.CheckLatestAsync(CancellationToken.None);
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = $"Текущая версия: {NetShiftUpdateService.CurrentVersion}";
                UpdateButton.Content = "Проверить обновления";
                UpdateButton.ToolTip = "Проверить наличие обновлений NetShift";
            }
            else
            {
                UpdateStatusText.Text = $"Доступна версия {_availableUpdate.Version}";
                UpdateButton.Content = "Проверить";
                UpdateButton.ToolTip = "Повторно проверить последнюю версию NetShift";
                Grid.SetColumnSpan(UpdateButton, 1);
                InstallUpdateButton.Content = "Установить";
                InstallUpdateButton.ToolTip = $"Скачать и установить NetShift {_availableUpdate.Version}";
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
            return _availableUpdate;
        }
        catch (Exception error)
        {
            _availableUpdate = null;
            UpdateStatusText.Text = "Не удалось проверить обновления NetShift";
            UpdateButton.Content = "Повторить проверку";
            UpdateButton.ToolTip = "Повторить проверку наличия обновлений NetShift";
            if (showError)
                ThemedDialog.Show(this, error.Message, "Обновление NetShift", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e) =>
        await CheckNetShiftUpdatesAsync(showError: true);

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var update = await CheckNetShiftUpdatesAsync(showError: true);
        if (update is null) return;

        if (ThemedDialog.Show(this,
                $"Скачать и установить NetShift {update.Tag}? Приложение закроется и автоматически запустится после обновления.",
                "Обновление NetShift", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        UpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        try
        {
            UpdateStatusText.Text = $"Скачиваю NetShift {update.Tag}: 0%";
            var progress = new Progress<double>(value => UpdateStatusText.Text = $"Скачиваю NetShift {update.Tag}: {value:P0}");
            var stagedExecutable = await NetShiftUpdateService.DownloadExecutableAsync(update, progress, CancellationToken.None);
            if (!await StopLegacyDnsAndRestoreAsync())
                throw new InvalidOperationException("Сначала восстановите DNS Legacy, затем повторите обновление NetShift.");
            var targetExecutable = Path.Combine(AppContext.BaseDirectory, "NetShift.exe");
            NetShiftUpdateService.LaunchUpdater(stagedExecutable, targetExecutable, Environment.ProcessId);
            UpdateStatusText.Text = "Обновление подготовлено; NetShift перезапустится автоматически…";
            _exitRequested = true;
            Close();
        }
        catch (Exception error)
        {
            UpdateStatusText.Text = "Установка обновления не завершена";
            ThemedDialog.Show(this, error.Message, "Обновление NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateButton.IsEnabled = NetShiftUpdateService.Repository is not null;
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
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
                ThemedDialog.Show(this, $"Не удалось обновить данные DNS-AI. Используются резервные значения.\n\n{error.Message}",
                    "DNS-AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshDnsAiButton.IsEnabled = true;
        }
    }

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e) => RefreshAdapters();

    private void ConfigureVpnAdapters_Click(object sender, RoutedEventArgs e)
    {
        _settings.ConfigureVpnAdapters = ConfigureVpnAdaptersCheckBox.IsChecked == true;
        SaveSettings();
        RefreshAdapters(selectFirst: true);
    }

    private async void ApplyDns_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterComboBox.SelectedItem is not NetworkAdapterInfo adapter)
        {
            ThemedDialog.Show(this, "Сначала выберите сетевой адаптер.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ipv4Addresses = SplitAddresses($"{Ipv4TextBox.Text},{Ipv4AlternateTextBox.Text}");
        var ipv6Addresses = SupportIPv6CheckBox.IsChecked == true
            ? SplitAddresses($"{Ipv6TextBox.Text},{Ipv6AlternateTextBox.Text}")
            : [];
        var addresses = ipv4Addresses.Concat(ipv6Addresses).ToArray();
        var legacyMode = LegacyModeRadio.IsChecked == true;
        var dohTemplate = DohTemplateTextBox.Text.Trim();
        if (!legacyMode && DohEnabledCheckBox.IsChecked != true) dohTemplate = string.Empty;
        if (addresses.Length == 0 || !HasFamily(ipv4Addresses, AddressFamily.InterNetwork) ||
            !HasFamily(ipv6Addresses, AddressFamily.InterNetworkV6))
        {
            ThemedDialog.Show(this, "Проверьте адреса: в поле IPv4 укажите только IPv4, в поле IPv6 — только IPv6. Нужен хотя бы один адрес.",
                "Проверьте адреса", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if ((legacyMode || DohEnabledCheckBox.IsChecked == true) &&
            (!Uri.TryCreate(dohTemplate, UriKind.Absolute, out var dohUri) || dohUri.Scheme != Uri.UriSchemeHttps ||
             !string.IsNullOrEmpty(dohUri.UserInfo) || !string.IsNullOrEmpty(dohUri.Fragment)))
        {
            ThemedDialog.Show(this, "Укажите корректный HTTPS-шаблон DoH.", "Проверьте шаблон DoH", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmation = ThemedDialog.Show(this,
            $"Заменить DNS на выбранном интерфейсе «{adapter.Name}»?\n\n{string.Join("\n", addresses)}" +
            (legacyMode ? $"\n\nРежим Legacy: локальный прокси и DoH {dohTemplate}. При выходе NetShift прежние DNS-настройки будут восстановлены." :
                dohTemplate.Length == 0 ? "" : $"\n\nDoH через Windows DNS Client: {dohTemplate}\nОткат на незашифрованный DNS выключен.") +
            "\n\nWindows запросит подтверждение администратора.",
            "Применить DNS", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK) return;

        if (legacyMode)
        {
            try
            {
                if (_legacyDohProxy is null || _legacyAdapterIndex != adapter.InterfaceIndex || !string.Equals(_legacyDohEndpoint, dohTemplate, StringComparison.OrdinalIgnoreCase))
                {
                    if (!await StopLegacyDnsAndRestoreAsync()) return;
                    _legacyDohProxy = await LegacyDohProxy.CreateAsync(dohTemplate);
                    _legacyDohEndpoint = dohTemplate;
                }
                if (await RunElevatedWorkerAsync("dns-legacy-apply", adapter.InterfaceIndex.ToString(), (SupportIPv6CheckBox.IsChecked == true).ToString()))
                {
                    _legacyAdapterIndex = adapter.InterfaceIndex;
                    _settings.NativeDnsMode = false;
                    _settings.SupportIPv6 = SupportIPv6CheckBox.IsChecked == true;
                    SaveSettings();
                    ThemedDialog.Show(this, "Legacy DNS включён. Локальный прокси работает, пока NetShift запущен; при выходе DNS адаптера будет восстановлен.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
                    RefreshAdapters();
                }
                else if (_legacyAdapterIndex is null && _legacyDohProxy is not null)
                {
                    await _legacyDohProxy.DisposeAsync();
                    _legacyDohProxy = null;
                    _legacyDohEndpoint = null;
                }
            }
            catch (Exception error)
            {
                if (_legacyAdapterIndex is null && _legacyDohProxy is not null)
                {
                    await _legacyDohProxy.DisposeAsync();
                    _legacyDohProxy = null;
                }
                ThemedDialog.Show(this, error.Message, "Не удалось включить Legacy DNS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        if (!await StopLegacyDnsAndRestoreAsync()) return;
        if (await RunElevatedWorkerAsync("dns-apply", adapter.InterfaceIndex.ToString(), string.Join(',', addresses), dohTemplate))
        {
            _settings.NativeDnsMode = true;
            _settings.SupportIPv6 = SupportIPv6CheckBox.IsChecked == true;
            SaveSettings();
            ThemedDialog.Show(this, "Native DNS применён и сохранён в Windows. DNS-служба восстановит этот профиль при запуске компьютера.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAdapters();
        }
    }

    private async Task<bool> StopLegacyDnsAndRestoreAsync()
    {
        if (_legacyAdapterIndex is not null)
        {
            if (!await RunElevatedWorkerAsync("dns-legacy-restore")) return false;
            _legacyAdapterIndex = null;
        }
        if (_legacyDohProxy is not null)
        {
            await _legacyDohProxy.DisposeAsync();
            _legacyDohProxy = null;
            _legacyDohEndpoint = null;
        }
        return true;
    }

    private async void ResetDns_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterComboBox.SelectedItem is not NetworkAdapterInfo adapter)
        {
            ThemedDialog.Show(this, "Сначала выберите сетевой адаптер.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = ThemedDialog.Show(this,
            $"Вернуть DNS по DHCP для «{adapter.Name}»? Это заменит вручную заданные DNS-адреса на автоматические.",
            "Вернуть DHCP", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK) return;

        if (!await StopLegacyDnsAndRestoreAsync()) return;
        if (await RunElevatedWorkerAsync("dns-reset", adapter.InterfaceIndex.ToString()))
        {
            _settings.NativeDnsMode = false;
            SaveSettings();
            ThemedDialog.Show(this, "Для интерфейса включено получение DNS по DHCP.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAdapters();
        }
    }

    private static IEnumerable<string> SplitAddresses(string value) => value
        .Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(address => IPAddress.TryParse(address, out var parsed) ? parsed.ToString() : address);

    private static (string Primary, string Alternate) SplitAddressPair(string value)
    {
        var addresses = value.Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (addresses.ElementAtOrDefault(0) ?? string.Empty, addresses.ElementAtOrDefault(1) ?? string.Empty);
    }

    private static bool HasFamily(IEnumerable<string> addresses, AddressFamily expectedFamily) =>
        addresses.All(address => IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == expectedFamily);

    private async void ChooseZapretFolder_Click(object sender, RoutedEventArgs e)
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
        if (!_settings.InitialZapretStrategyCheckCompleted)
            await CheckZapretStrategiesAsync(isInitialCheck: true);
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
                        ThemedDialog.Show(this,
                            $"Доступен релиз {latest.Tag}, а установлена версия {installed}. Запустить установленную стратегию? Нажмите «Нет», чтобы сначала скачать релиз через NetShift.",
                            "Доступно обновление", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                        return;
                }
                catch (Exception error)
                {
                    if (ThemedDialog.Show(this, $"Не удалось проверить релиз GitHub:\n{error.Message}\n\nЗапустить установленную стратегию?",
                            "Проверка релиза недоступна", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                }
            }
            var confirmation = ThemedDialog.Show(this,
                $"Запустить {strategy.Name}? Скрипт Flowseal изменит обработку сетевого трафика и может включить TCP timestamps. Windows запросит права администратора.",
                "Запуск zapret", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.OK) return;
            await RunElevatedWorkerAsync("zapret-start", strategy.Path);
            await RefreshServiceStatusAsync();
        }
        finally { StartStrategyButton.IsEnabled = StrategyComboBox.SelectedItem is StrategyItem; }
    }

    private async void StopStrategy_Click(object sender, RoutedEventArgs e)
    {
        var folder = ZapretFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) return;
        var confirmation = ThemedDialog.Show(this,
            "Остановить winws.exe, запущенный из выбранной папки? Зарегистрированная служба Windows этим действием не удаляется.",
            "Остановить ручной запуск", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation == MessageBoxResult.OK && await RunElevatedWorkerAsync("zapret-stop", folder))
            await RefreshServiceStatusAsync();
    }

    private async void InstallService_Click(object sender, RoutedEventArgs e)
    {
        if (StrategyComboBox.SelectedItem is not StrategyItem strategy) return;
        var folder = Path.GetDirectoryName(strategy.Path)!;
        var isInstalled = await QueryWindowsServiceAsync("zapret") is not null;
        var message = isInstalled
            ? $"Заменить установленную службу zapret стратегией {strategy.Name}? NetShift изменит её только если она установлена из выбранной папки или папки NetShift. Потребуются права администратора."
            : $"Установить стратегию {strategy.Name} как автоматическую службу Windows?\n\n{folder}\n\nПотребуются права администратора.";
        if (ThemedDialog.Show(this, message, "Установить службу zapret", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (await RunElevatedWorkerAsync("zapret-service-install", strategy.Path))
        {
            await RefreshServiceStatusAsync();
            _ = CheckZapretStrategiesAsync(isInitialCheck: !_settings.InitialZapretStrategyCheckCompleted);
        }
    }

    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (await RunElevatedWorkerAsync("zapret-service-start", folder))
        {
            await RefreshServiceStatusAsync();
            _ = CheckZapretStrategiesAsync(isInitialCheck: !_settings.InitialZapretStrategyCheckCompleted);
        }
    }

    private async void StopService_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (ThemedDialog.Show(this, "Остановить службу zapret? Это временно прекратит работу выбранной стратегии.",
                "Остановить службу", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (await RunElevatedWorkerAsync("zapret-service-stop", folder)) await RefreshServiceStatusAsync();
    }

    private async void RemoveService_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (await QueryWindowsServiceAsync("zapret") is null) { await RefreshServiceStatusAsync(); return; }
        if (ThemedDialog.Show(this,
                $"Остановить и удалить службу zapret из выбранной папки или папки NetShift?\n\nТекущий пакет: {folder}\n\nСлужбы и процессы других программ не затрагиваются.",
                "Удалить службу zapret", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (await RunElevatedWorkerAsync("zapret-service-remove", folder)) await RefreshServiceStatusAsync();
    }

    private async void RefreshServiceStatus_Click(object sender, RoutedEventArgs e) => await RefreshServiceStatusAsync();

    private async void CheckActiveStrategy_Click(object sender, RoutedEventArgs e)
    {
        await CheckZapretStrategiesAsync(isInitialCheck: !_settings.InitialZapretStrategyCheckCompleted);
    }

    private async Task CheckZapretStrategiesAsync(bool isInitialCheck)
    {
        if (_isZapretStrategyTestRunning) return;
        var folder = ZapretFolderTextBox.Text.Trim();
        if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly).Any())
        {
            ZapretHealthStatusText.Text = "Сначала скачайте пакет zapret, чтобы NetShift мог проверить варианты.";
            ZapretTestPhaseText.Text = "Ожидает пакет";
            ZapretTestPhaseText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            ZapretTestStrategyText.Text = "Проверка не требует установленной или запущенной службы zapret.";
            return;
        }

        string? serviceStatus = null;
        for (var attempt = 0; attempt < (isInitialCheck ? 5 : 1); attempt++)
        {
            serviceStatus = await QueryWindowsServiceAsync("zapret");
            if (serviceStatus?.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) == true)
            {
                await CheckActiveZapretStrategyAsync(isInitialCheck);
                return;
            }
            if (serviceStatus?.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) != true) break;
            await Task.Delay(1000);
        }
        if (serviceStatus?.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) == true ||
            serviceStatus?.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase) == true)
        {
            ZapretHealthStatusText.Text = "Служба меняет состояние. Повторите проверку через несколько секунд.";
            ZapretTestPhaseText.Text = "Служба запускается или останавливается";
            ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
            return;
        }

        await RunZapretStrategySweepAsync(folder, isInitialCheck);
    }

    private async Task CheckActiveZapretStrategyAsync(bool isInitialCheck)
    {
        string? activeStrategyPath;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            var strategyName = key?.GetValue("zapret-discord-youtube") as string;
            var imagePath = key?.GetValue("ImagePath") as string;
            var match = Regex.Match(imagePath ?? string.Empty, "^\\s*\\\"(?<path>[^\\\"]*winws\\.exe)\\\"|^\\s*(?<path>[^\\s]*winws\\.exe)", RegexOptions.IgnoreCase);
            var binPath = match.Success ? Path.GetDirectoryName(match.Groups["path"].Value) : null;
            var packageFolder = binPath is null ? null : Path.GetDirectoryName(binPath);
            activeStrategyPath = string.IsNullOrWhiteSpace(strategyName) || packageFolder is null
                ? null
                : Path.GetFullPath(Path.Combine(packageFolder, strategyName + ".bat"));
        }
        catch
        {
            activeStrategyPath = null;
        }

        if (activeStrategyPath is null)
        {
            ZapretHealthStatusText.Text = "Не удалось определить стратегию работающей службы zapret.";
            ZapretTestPhaseText.Text = "Стратегия не определена";
            ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.IndianRed;
            ZapretTestStrategyText.Text = "Не удалось прочитать путь активной стратегии из конфигурации службы.";
            return;
        }

        _settings.ZapretStrategyHealth.Clear();
        _settings.RecommendedZapretStrategyPath = null;
        SaveSettings();
        UpdateZapretInventory();
        CheckActiveStrategyButton.IsEnabled = false;
        var displayStrategyName = Path.GetFileNameWithoutExtension(activeStrategyPath);
        ZapretHealthStatusText.Text = $"Проверяю доступность Discord, YouTube и Google через {displayStrategyName}…";
        ZapretHealthStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        ZapretTestPhaseText.Text = "Проверка выполняется";
        ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
        ZapretTestStrategyText.Text = $"Активная служба: проверяю {displayStrategyName}. Её работа не прерывается.";
        ZapretTestProgressBar.Visibility = Visibility.Visible;
        SetZapretTargetStatus("Discord", "Ожидает", System.Windows.Media.Brushes.Gray);
        SetZapretTargetStatus("YouTube", "Ожидает", System.Windows.Media.Brushes.Gray);
        SetZapretTargetStatus("Google", "Ожидает", System.Windows.Media.Brushes.Gray);
        try
        {
            (string Name, string Url)[] targets =
            [
                ("Discord", "https://discord.com"),
                ("YouTube", "https://www.youtube.com"),
                ("Google", "https://www.google.com")
            ];
            var results = new List<(string Name, bool Reachable)>(targets.Length);
            foreach (var target in targets)
            {
                SetZapretTargetStatus(target.Name, "Проверяется…", System.Windows.Media.Brushes.DodgerBlue);
                results.Add(await ProbeZapretTargetWithStatusAsync(target.Name, target.Url));
            }
            var healthy = results.All(result => result.Reachable);
            _settings.ZapretStrategyHealth[HealthKey(activeStrategyPath)] = healthy;
            _settings.RecommendedZapretStrategyPath = healthy ? Path.GetFullPath(activeStrategyPath) : null;
            if (isInitialCheck) _settings.InitialZapretStrategyCheckCompleted = true;
            SaveSettings();
            UpdateZapretInventory();

            var passed = results.Where(result => result.Reachable).Select(result => result.Name).ToArray();
            var failed = results.Where(result => !result.Reachable).Select(result => result.Name).ToArray();
            ZapretHealthStatusText.Text = failed.Length == 0
                ? $"Все адреса доступны через {displayStrategyName}. Стратегия отмечена как рекомендуемая."
                : $"{displayStrategyName}: доступны {string.Join(", ", passed)}; нет ответа от {string.Join(", ", failed)}.";
            ZapretHealthStatusText.Foreground = healthy ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.IndianRed;
            ZapretTestPhaseText.Text = healthy ? "Все сайты доступны" : "Есть недоступные сайты";
            ZapretTestPhaseText.Foreground = healthy ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.IndianRed;
        }
        catch (Exception error)
        {
            ZapretHealthStatusText.Text = $"Не удалось проверить активную стратегию: {error.Message}";
            ZapretTestPhaseText.Text = "Ошибка проверки";
            ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        finally
        {
            ZapretTestProgressBar.Visibility = Visibility.Collapsed;
            CheckActiveStrategyButton.IsEnabled = !_isZapretStrategyTestRunning && Directory.Exists(ZapretFolderTextBox.Text.Trim());
        }
    }

    private async Task RunZapretStrategySweepAsync(string folder, bool isInitialCheck)
    {
        var strategyPaths = Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, Comparer<string>.Create(ZapretStrategyComparer.ComparePaths))
            .ToArray();
        if (strategyPaths.Length == 0) return;

        foreach (var strategyPath in strategyPaths) _settings.ZapretStrategyHealth.Remove(HealthKey(strategyPath));
        _settings.RecommendedZapretStrategyPath = null;
        SaveSettings();
        UpdateZapretInventory();

        var progressPath = Path.Combine(Path.GetTempPath(), $"NetShift-zapret-test-{Guid.NewGuid():N}.json");
        _lastZapretTestProgressJson = null;
        _isZapretStrategyTestRunning = true;
        CheckActiveStrategyButton.IsEnabled = false;
        StrategyComboBox.IsEnabled = false;
        DownloadLatestButton.IsEnabled = false;
        StartStrategyButton.IsEnabled = false;
        StopStrategyButton.IsEnabled = false;
        InstallServiceButton.IsEnabled = false;
        StartServiceButton.IsEnabled = false;
        StopServiceButton.IsEnabled = false;
        RemoveServiceButton.IsEnabled = false;
        ZapretHealthStatusText.Text = $"Проверяю стратегии по порядку: 0 из {strategyPaths.Length}.";
        ZapretHealthStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        ZapretTestPhaseText.Text = "Подготовка";
        ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
        ZapretTestStrategyText.Text = "Стратегии и сайты проверяются по очереди; каждый вариант запускается отдельно и останавливается после проверки. Служба Windows не изменяется.";
        ZapretTestProgressBar.Visibility = Visibility.Visible;
        SetZapretTargetStatus("Discord", "Ожидает", System.Windows.Media.Brushes.Gray);
        SetZapretTargetStatus("YouTube", "Ожидает", System.Windows.Media.Brushes.Gray);
        SetZapretTargetStatus("Google", "Ожидает", System.Windows.Media.Brushes.Gray);

        try
        {
            var testTask = RunElevatedWorkerAsync("zapret-test-strategies", folder, progressPath);
            while (!testTask.IsCompleted)
            {
                await ApplyZapretTestProgressAsync(progressPath);
                await Task.Delay(250);
            }

            if (!await testTask)
            {
                ZapretHealthStatusText.Text = "Проверка отменена или не завершилась.";
                ZapretTestPhaseText.Text = "Проверка не завершена";
                ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.IndianRed;
                return;
            }

            await ApplyZapretTestProgressAsync(progressPath);
            if (!string.IsNullOrWhiteSpace(_settings.RecommendedZapretStrategyPath))
            {
                if (isInitialCheck) _settings.InitialZapretStrategyCheckCompleted = true;
                SaveSettings();
            }
            else if (File.Exists(progressPath))
            {
                var finalProgress = await ReadZapretTestProgressAsync(progressPath);
                if (finalProgress?.Completed == true && isInitialCheck)
                {
                    _settings.InitialZapretStrategyCheckCompleted = true;
                    SaveSettings();
                }
            }
        }
        catch (Exception error)
        {
            ZapretHealthStatusText.Text = $"Не удалось проверить стратегии: {error.Message}";
            ZapretTestPhaseText.Text = "Ошибка проверки";
            ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        finally
        {
            _isZapretStrategyTestRunning = false;
            ZapretTestProgressBar.Visibility = Visibility.Collapsed;
            UpdateZapretInventory();
            try { File.Delete(progressPath); } catch { }
            try { File.Delete(progressPath + ".tmp"); } catch { }
        }
    }

    private async Task ApplyZapretTestProgressAsync(string progressPath)
    {
        string json;
        try { json = await File.ReadAllTextAsync(progressPath, Encoding.UTF8); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        if (json.Length == 0 || json == _lastZapretTestProgressJson) return;
        _lastZapretTestProgressJson = json;

        ZapretTestProgress? progress;
        try { progress = JsonSerializer.Deserialize<ZapretTestProgress>(json, ZapretProgressJsonOptions); }
        catch (JsonException) { return; }
        if (progress is null) return;

        ZapretTestPhaseText.Text = progress.Phase switch
        {
            "starting" => "Запуск стратегии",
            "checking" => "Проверка сайтов",
            "failed" => "Не прошла",
            "recommended" => "Рекомендация найдена",
            "completed" => progress.RecommendedPath is null ? "Подходящая не найдена" : "Проверка завершена",
            _ => progress.Phase
        };
        ZapretTestPhaseText.Foreground = progress.Phase switch
        {
            "checking" or "starting" => System.Windows.Media.Brushes.DodgerBlue,
            "recommended" => System.Windows.Media.Brushes.LightGreen,
            "failed" => System.Windows.Media.Brushes.IndianRed,
            "completed" => progress.RecommendedPath is null ? System.Windows.Media.Brushes.IndianRed : System.Windows.Media.Brushes.LightGreen,
            _ => (System.Windows.Media.Brush)FindResource("MutedBrush")
        };
        ZapretHealthStatusText.Text = progress.Message;
        ZapretHealthStatusText.Foreground = progress.Phase switch
        {
            "recommended" => System.Windows.Media.Brushes.LightGreen,
            "completed" when progress.RecommendedPath is null => System.Windows.Media.Brushes.IndianRed,
            "failed" when progress.Completed => System.Windows.Media.Brushes.IndianRed,
            _ => (System.Windows.Media.Brush)FindResource("MutedBrush")
        };
        ZapretTestStrategyText.Text = progress.StrategyPath is null
            ? $"Проверено {progress.StrategyIndex} из {progress.StrategyCount}. Служба Windows не изменяется."
            : $"{Path.GetFileNameWithoutExtension(progress.StrategyPath)} · вариант {progress.StrategyIndex} из {progress.StrategyCount}.";
        ZapretTestProgressBar.Visibility = progress.Completed ? Visibility.Collapsed : Visibility.Visible;

        foreach (var target in new[] { "Discord", "YouTube", "Google" })
        {
            var hasResult = progress.Targets.TryGetValue(target, out var reachable) && reachable.HasValue;
            var status = hasResult ? reachable == true ? "Доступен" : "Нет ответа"
                : progress.Phase == "checking" ? "Проверяется…" : "Ожидает";
            var color = hasResult ? reachable == true ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.IndianRed
                : progress.Phase == "checking" ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Gray;
            SetZapretTargetStatus(target, status, color);
        }

        var healthChanged = false;
        if (progress.StrategyPath is not null && (progress.Phase is "failed" or "recommended"))
        {
            _settings.ZapretStrategyHealth[HealthKey(progress.StrategyPath)] = progress.Phase == "recommended";
            healthChanged = true;
        }
        if (_settings.RecommendedZapretStrategyPath != progress.RecommendedPath)
        {
            _settings.RecommendedZapretStrategyPath = progress.RecommendedPath;
            healthChanged = true;
        }
        if (healthChanged)
        {
            SaveSettings();
            RefreshStrategyItems();
        }
    }

    private static async Task<ZapretTestProgress?> ReadZapretTestProgressAsync(string progressPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(progressPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<ZapretTestProgress>(json, ZapretProgressJsonOptions);
        }
        catch { return null; }
    }

    private static string HealthKey(string path) => Path.GetFullPath(path).ToUpperInvariant();

    private static async Task<(string Name, bool Reachable)> ProbeZapretTargetAsync(string name, string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return (name, true);
        }
        catch
        {
            return (name, false);
        }
    }

    private async void SavePackageSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (GameFilterModeComboBox.SelectedValue is not string mode || IpSetModeComboBox.SelectedValue is not string ipSetMode ||
            !IsValidPortRange(GameFilterTcpTextBox.Text) || !IsValidPortRange(GameFilterUdpTextBox.Text))
        {
            ThemedDialog.Show(this, "Укажите диапазоны TCP и UDP. Примеры: 80, 1024-65535 или 1024-1934,1936-65535.",
                "Проверьте параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ipSetMode == "loaded" && ReadIpSetMode(folder) != "loaded" && !File.Exists(Path.Combine(folder, "lists", "ipset-all.txt.backup")))
        {
            ThemedDialog.Show(this, "Резервного IPSet нет. Сначала нажмите «Обновить IPSet», затем выберите загруженный список.",
                "Нет списка IPSet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ThemedDialog.Show(this,
                "Сохранить игровой фильтр, режим IPSet и проверку последнего релиза перед запуском? Изменение фильтра вступит в силу после перезапуска стратегии.",
                "Сохранить параметры zapret", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (await RunElevatedWorkerAsync("zapret-settings-save", folder, mode, GameFilterTcpTextBox.Text.Trim(), GameFilterUdpTextBox.Text.Trim(), ipSetMode))
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
            if (await RunElevatedWorkerAsync("zapret-ipset-update", folder, path, ComputeFileSha256(path)))
            {
                SetToolsOutput($"Список IPSet обновлён: {content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length:N0} строк. Предыдущий список сохранён рядом.");
                UpdateZapretInventory();
            }
        }
        catch (Exception error) { ThemedDialog.Show(this, error.Message, "Не удалось обновить IPSet", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { DownloadStatusText.Text = ""; }
    }

    private async void ReplaceFake_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder) || FakeTargetComboBox.SelectedValue is not string target || FakeFileComboBox.SelectedItem is not string fakeFile) return;
        var targetName = target == "discord" ? "Discord UDP" : "игровой UDP";
        if (ThemedDialog.Show(this, $"Заменить активный fake-файл для {targetName} на {fakeFile}? Текущий активный файл будет перезаписан.",
                "Заменить fake-файл", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (await RunElevatedWorkerAsync("zapret-fake-replace", folder, target, fakeFile)) SetToolsOutput($"Активный файл для {targetName} заменён на {fakeFile}.");
    }

    private async void DownloadLatest_Click(object sender, RoutedEventArgs e)
    {
        DownloadLatestButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        try
        {
            DownloadStatusText.Text = "Проверяю последний GitHub-релиз…";
            var latestRelease = await ZapretReleaseService.GetLatestReleaseDownloadInfoAsync(CancellationToken.None);
            var latestInstallPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "NetShift", "zapret-" + latestRelease.Version);
            if (IsCompleteZapretPackage(latestInstallPath))
            {
                SelectZapretFolder(latestInstallPath);
                DownloadStatusText.Text = $"Последний релиз {latestRelease.Tag} уже установлен; выбрана его папка.";
                if (!_settings.InitialZapretStrategyCheckCompleted)
                    await CheckZapretStrategiesAsync(isInitialCheck: true);
                return;
            }

            DownloadStatusText.Text = "Скачиваю архив последнего релиза…";
            var progress = new Progress<double>(value => DownloadProgressBar.Value = value);
            var release = await ZapretReleaseService.DownloadLatestAsync(latestRelease, progress, CancellationToken.None);
            var installPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "NetShift", "zapret-" + release.Version);
            if (Directory.Exists(installPath))
            {
                if (!IsCompleteZapretPackage(installPath))
                    throw new InvalidDataException($"Папка {installPath} уже существует, но пакет неполный. Архив сохранён; выберите другой путь или исправьте папку.");
                File.Delete(release.ArchivePath);
                SelectZapretFolder(installPath);
                DownloadStatusText.Text = $"Последний релиз {release.Tag} уже установлен; выбрана его папка.";
                if (!_settings.InitialZapretStrategyCheckCompleted)
                    await CheckZapretStrategiesAsync(isInitialCheck: true);
                return;
            }

            DownloadStatusText.Text = $"ZIP {release.Tag} загружен и проверен SHA-256. Распаковываю…";
            if (await RunElevatedWorkerAsync("zapret-package-install", release.ArchivePath, release.Tag, release.Sha256))
            {
                File.Delete(release.ArchivePath);
                SelectZapretFolder(installPath);
                DownloadStatusText.Text = $"Релиз {release.Tag} установлен в {installPath}.";
                if (!_settings.InitialZapretStrategyCheckCompleted)
                    await CheckZapretStrategiesAsync(isInitialCheck: true);
            }
            else DownloadStatusText.Text = "Установка не завершена; ZIP оставлен в локальных загрузках NetShift.";
        }
        catch (Exception error)
        {
            DownloadStatusText.Text = "Загрузка или проверка релиза не завершилась.";
            ThemedDialog.Show(this, error.Message, "Не удалось скачать релиз zapret", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DownloadLatestButton.IsEnabled = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsCompleteZapretPackage(string folder) =>
        Directory.Exists(folder) &&
        File.Exists(Path.Combine(folder, "service.bat")) &&
        File.Exists(Path.Combine(folder, "bin", "winws.exe")) &&
        Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly).Any();

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
            ThemedDialog.Show(this, message, "Проверка обновлений", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { ThemedDialog.Show(this, error.Message, "Не удалось проверить релиз", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        try
        {
            var lines = await ZapretDiagnosticsService.CollectAsync(folder);
            SetToolsOutput(string.Join(Environment.NewLine, lines));
        }
        catch (Exception error)
        {
            ThemedDialog.Show(this, error.Message, "Не удалось выполнить диагностику", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RunUpstreamTests_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetZapretFolder(out var folder)) return;
        var script = Path.Combine(folder, "utils", "test zapret.ps1");
        if (!File.Exists(script))
        {
            ThemedDialog.Show(this, "В выбранном пакете не найден официальный скрипт utils/test zapret.ps1.", "Тесты недоступны", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ThemedDialog.Show(this,
                "Откроется PowerShell с официальными тестами Flowseal. Скрипт проверяет сетевые адреса, может временно менять IPSet и останавливать/перезапускать winws, затем сохраняет отчёт. Для записи в папку пакета потребуется подтверждение администратора.",
                "Запустить тесты", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (await RunElevatedWorkerAsync("zapret-run-tests", folder))
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
            var choice = ThemedDialog.Show(this,
                $"В системный hosts будут добавлены отсутствующие записи: {missing.Length} из {entries.Length}. Это изменит разрешение адресов перечисленных доменов. NetShift сначала создаст резервную копию.\n\n{preview}{(missing.Length > 8 ? "\n…" : string.Empty)}\n\nДобавить?",
                "Обновить hosts", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) { DownloadStatusText.Text = "Обновление hosts отменено."; return; }
            if (await RunElevatedWorkerAsync("zapret-hosts-append", downloadedPath, ComputeFileSha256(downloadedPath)))
            {
                SetToolsOutput($"Добавлено записей hosts: {missing.Length}. Резервная копия создана рядом с системным hosts.");
                DownloadStatusText.Text = "Hosts обновлён";
            }
        }
        catch (Exception error)
        {
            DownloadStatusText.Text = "Проверка hosts не завершилась.";
            ThemedDialog.Show(this, error.Message, "Не удалось проверить hosts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RestoreHostsBackup_Click(object sender, RoutedEventArgs e)
    {
        var hostsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc");
        var backup = Directory.Exists(hostsDirectory)
            ? Directory.GetFiles(hostsDirectory, "hosts.NetShift-*.bak").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (backup is null)
        {
            ThemedDialog.Show(this, "Резервные копии hosts, созданные NetShift, не найдены.", "Нет резервной копии", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ThemedDialog.Show(this,
                $"Восстановить системный hosts из последней резервной копии? Текущий hosts также будет сохранён перед восстановлением.\n\n{Path.GetFileName(backup)}",
                "Восстановить hosts", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
        if (await RunElevatedWorkerAsync("zapret-hosts-restore", backup, ComputeFileSha256(backup)))
                SetToolsOutput($"Системный hosts восстановлен из {Path.GetFileName(backup)}. Предыдущее содержимое также сохранено.");
        }
        catch (Exception error) { ThemedDialog.Show(this, error.Message, "Не удалось восстановить hosts", MessageBoxButton.OK, MessageBoxImage.Error); }
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
            ThemedDialog.Show(this, "Сначала укажите папку с установленным пакетом zapret или скачайте последний релиз.",
                "Пакет не выбран", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    private void SetToolsOutput(string value) => ToolsOutputTextBox.Text = value;

    private async Task<bool> RunElevatedWorkerAsync(params string[] arguments)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            ThemedDialog.Show(this, "Не удалось определить путь к приложению.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                ThemedDialog.Show(this, "Административная операция не завершилась за 10 минут и была остановлена.",
                    "NetShift", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (process.ExitCode == 0) return true;
            ThemedDialog.Show(this, "Действие завершилось с ошибкой. Проверьте сообщение административного процесса и статус сети.",
                "NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            // The user declined the Windows elevation prompt.
        }
        catch (Exception error)
        {
            ThemedDialog.Show(this, error.Message, "NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
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
        public bool InitialZapretStrategyCheckCompleted { get; set; }
        public Dictionary<string, bool> ZapretStrategyHealth { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? RecommendedZapretStrategyPath { get; set; }
        public bool NativeDnsMode { get; set; } = true;
        public bool SupportIPv6 { get; set; } = true;
        public bool ConfigureVpnAdapters { get; set; }
        public string? DnsProviderName { get; set; }
    }

    private sealed record PackageChoice(string Name, string Value)
    {
        public override string ToString() => Name;
    }
    private sealed record StrategyItem(string Name, string Path, bool? IsHealthy, bool IsRecommended)
    {
        public Visibility HealthVisibility => IsHealthy.HasValue ? Visibility.Visible : Visibility.Collapsed;
        public System.Windows.Media.Brush HealthBrush => IsHealthy == true ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.IndianRed;
        public string HealthTooltip => IsRecommended ? "Рекомендуется: все проверки пройдены" : IsHealthy == true ? "Все проверки пройдены" : "Проверка не пройдена";
        public override string ToString() => Name;
    }
}
