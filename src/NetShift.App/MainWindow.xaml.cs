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
    private bool _isZapretServiceInstalled;
    private bool _isZapretServiceRunning;
    private bool _isZapretServiceTransitioning;
    private bool _isZapretServiceActionRunning;
    private bool _isManualZapretRunning;
    private string? _activeZapretServiceStrategyName;
    private string? _activeZapretServiceFolder;
    private string? _activeManualZapretStrategyName;
    private string? _activeManualZapretFolder;
    private bool _isBasicConnectionActionRunning;
    private bool _isSyncingBasicSelectors;
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
        BasicDnsProviderComboBox.ItemsSource = _dnsProviders;
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
        _isSyncingBasicSelectors = true;
        BasicDnsProviderComboBox.SelectedItem = ProviderComboBox.SelectedItem;
        BasicDnsToggle.IsChecked = _settings.BasicDnsEnabled;
        BasicZapretToggle.IsChecked = _settings.BasicZapretEnabled;
        BasicZapretServiceToggle.IsChecked = _settings.BasicZapretUseService;
        _isSyncingBasicSelectors = false;
        LegacyModeRadio.IsChecked = !_settings.NativeDnsMode;
        NativeModeRadio.IsChecked = _settings.NativeDnsMode;
        SupportIPv6CheckBox.IsChecked = _settings.SupportIPv6;
        ConfigureVpnAdaptersCheckBox.IsChecked = _settings.ConfigureVpnAdapters;
        AppAutoStartCheckBox.IsChecked = IsAppAutoStartEnabled();
        UpdateAppAutoStartStatus();
        var initialPage = _settings.LastSelectedPage is "basic" or "dns" or "zapret" or "settings"
            ? _settings.LastSelectedPage
            : "basic";
        NavigateToPage(initialPage, persistSelection: false);
        InstallDnsServiceButton.IsEnabled = NetShiftBootstrapper.HasEmbeddedDnsService();
        _ = RefreshDnsServiceStatusAsync();
        RefreshAdapters(selectFirst: true);
        RefreshZapretFolder();
    }

    private void NavigatePage_Click(object sender, RoutedEventArgs e)
    {
        var page = (sender as FrameworkElement)?.Tag?.ToString() ?? "dns";
        NavigateToPage(page);
    }

    private void ToggleMode_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPage(BasicPage.Visibility == Visibility.Visible ? "dns" : "basic");
    }

    private void NavigateToPage(string page, bool persistSelection = true)
    {
        if (page is not ("basic" or "dns" or "zapret" or "settings"))
            page = "dns";

        BasicPage.Visibility = page == "basic" ? Visibility.Visible : Visibility.Collapsed;
        DnsPage.Visibility = page == "dns" ? Visibility.Visible : Visibility.Collapsed;
        ZapretPage.Visibility = page == "zapret" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = page switch
        {
            "basic" => "Базовый режим",
            "zapret" => "zapret",
            "settings" => "Настройки",
            _ => "DNS"
        };
        ModeToggleButton.Content = page == "basic" ? "Расширенный режим →" : "Базовый режим ←";

        SetNavigationButtonState(BasicNavigationButton, page == "basic");
        SetNavigationButtonState(DnsNavigationButton, page == "dns");
        SetNavigationButtonState(ZapretNavigationButton, page == "zapret");
        SetNavigationButtonState(SettingsNavigationButton, page == "settings");
        if (page == "zapret") _ = RefreshServiceStatusAsync();
        if (page == "basic")
        {
            UpdateBasicModeStatus();
            if (IsLoaded) _ = RefreshServiceStatusAsync();
        }

        if (persistSelection && !string.Equals(_settings.LastSelectedPage, page, StringComparison.Ordinal))
        {
            _settings.LastSelectedPage = page;
            SaveSettings();
        }
    }

    private void SetNavigationButtonState(System.Windows.Controls.Button button, bool isSelected)
    {
        var transparentIdle = button == SettingsNavigationButton;
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

    private void UpdateBasicModeStatus()
    {
        if (BasicConnectionButton is null || BasicDnsToggle is null || BasicZapretToggle is null) return;
        if (_isBasicConnectionActionRunning)
        {
            BasicModeStatusText.Text = _isZapretStrategyTestRunning ? "Проверяю стратегии zapret…" : "Подготавливаю подключение…";
            BasicModeStatusText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
            BasicConnectionButton.Content = _isZapretStrategyTestRunning ? "ПРОВЕРКА…" : "ПОДГОТОВКА…";
            BasicConnectionButton.IsEnabled = false;
            BasicDnsToggle.IsEnabled = false;
            BasicZapretToggle.IsEnabled = false;
            BasicZapretServiceToggle.IsEnabled = false;
            BasicDnsProviderComboBox.IsEnabled = false;
            BasicStrategyComboBox.IsEnabled = false;
            return;
        }
        if (_isZapretStrategyTestRunning || _isZapretServiceActionRunning)
        {
            BasicModeStatusText.Text = _isZapretStrategyTestRunning ? "Проверяю стратегии zapret…" : "Выполняется операция zapret…";
            BasicModeStatusText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
            BasicConnectionButton.Content = _isZapretStrategyTestRunning ? "ПРОВЕРКА…" : "ОПЕРАЦИЯ…";
            BasicConnectionButton.IsEnabled = false;
            BasicDnsToggle.IsEnabled = false;
            BasicZapretToggle.IsEnabled = false;
            BasicZapretServiceToggle.IsEnabled = false;
            BasicDnsProviderComboBox.IsEnabled = false;
            BasicStrategyComboBox.IsEnabled = false;
            return;
        }
        var dnsSelected = BasicDnsToggle.IsChecked == true;
        var zapretSelected = BasicZapretToggle.IsChecked == true;
        var hasSelection = dnsSelected || zapretSelected;
        var dnsActive = IsNetShiftDnsActive();
        var zapretActive = _isZapretServiceRunning || _isManualZapretRunning;
        var anyActive = dnsActive || zapretActive;
        var zapretModeMatches = !zapretSelected || (_settings.BasicZapretUseService
            ? _isZapretServiceRunning && !_isManualZapretRunning
            : _isManualZapretRunning && !_isZapretServiceRunning);
        var selectionMatches = hasSelection && dnsSelected == dnsActive && zapretSelected == zapretActive && zapretModeMatches;

        BasicModeStatusText.Text = !hasSelection && !anyActive ? "Ничего не выбрано"
            : selectionMatches ? "Подключено"
            : anyActive ? "Выбор ещё не применён"
            : "Отключено";
        BasicModeStatusText.Foreground = selectionMatches
            ? System.Windows.Media.Brushes.LightGreen
            : anyActive
                ? System.Windows.Media.Brushes.Goldenrod
                : (System.Windows.Media.Brush)FindResource("MutedBrush");
        BasicConnectionButton.Content = !hasSelection && !anyActive ? "ВЫБЕРИТЕ КОМПОНЕНТЫ"
            : selectionMatches ? "ОТКЛЮЧИТЬ"
            : anyActive && !hasSelection ? "ОТКЛЮЧИТЬ ВСЁ"
            : anyActive ? "ПРИМЕНИТЬ ВЫБОР"
            : "ВКЛЮЧИТЬ";
        BasicConnectionButton.Background = selectionMatches
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 217, 130))
            : anyActive
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 163, 70))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(88, 103, 122));
        BasicConnectionButton.Foreground = selectionMatches || anyActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 20, 29))
            : System.Windows.Media.Brushes.White;
        var busy = _isBasicConnectionActionRunning || _isZapretStrategyTestRunning || _isZapretServiceActionRunning;
        BasicConnectionButton.IsEnabled = (hasSelection || anyActive) && !busy;

        var canEdit = !busy;
        BasicDnsToggle.IsEnabled = canEdit;
        BasicZapretToggle.IsEnabled = canEdit;
        BasicZapretServiceToggle.IsEnabled = canEdit;
        BasicDnsProviderComboBox.IsEnabled = canEdit && !dnsActive;
        BasicStrategyComboBox.IsEnabled = canEdit && !zapretActive && BasicStrategyComboBox.Items.Count > 1;

        var folder = ZapretFolderTextBox?.Text.Trim() ?? string.Empty;
        var hasZapretPackage = Directory.Exists(folder) && File.Exists(Path.Combine(folder, "service.bat"));
        BasicModeDetailText.Text = zapretSelected && !hasZapretPackage
            ? "Для zapret сначала скачайте пакет в расширенном режиме. DNS можно подключить отдельно."
            : selectionMatches
                ? zapretSelected && !string.IsNullOrWhiteSpace(_activeZapretServiceStrategyName)
                    ? $"Подключено. Запущена стратегия {_activeZapretServiceStrategyName}."
                    : zapretSelected && !string.IsNullOrWhiteSpace(_activeManualZapretStrategyName)
                        ? $"Подключено без службы. Запущена стратегия {_activeManualZapretStrategyName}."
                        : "Выбранные компоненты работают. Нажмите кнопку, чтобы отключить их."
                : anyActive
                    ? "Нажмите кнопку, чтобы привести DNS и zapret к выбранным переключателям."
                    : zapretSelected
                        ? $"При подключении {(dnsSelected ? "сначала применится выбранный DNS. Затем " : string.Empty)}zapret проверит все стратегии и запустит лучшую {(BasicZapretServiceToggle.IsChecked == true ? "как службу Windows" : "в фоне без службы")}."
                        : dnsSelected
                            ? "При подключении применится выбранный DNS-провайдер."
                            : "Выберите DNS и/или zapret, затем нажмите большую кнопку.";
    }

    private static bool IsNetShiftDnsActive()
    {
        return IsDnsRegistryProfileActive(@"SOFTWARE\NetShift\Dns") ||
               IsDnsRegistryProfileActive(@"SOFTWARE\NetShift\LegacyDns");
    }

    private static bool IsDnsRegistryProfileActive(string subKeyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
            return Convert.ToInt32(key?.GetValue("Enabled", 0) ?? 0) == 1;
        }
        catch { return false; }
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
        var hasService = File.Exists(Path.Combine(folder, "service.bat"));
        var valid = hasService && strategies.Length > 0;
        var pathWarning = folder.Any(character => character > 127 || char.IsWhiteSpace(character))
            ? "Для стабильной работы выберите путь без кириллицы и пробелов."
            : string.Empty;
        SetZapretStatus(valid
            ? pathWarning
            : "Пакет zapret не найден или неполный.");
        StartStrategyButton.IsEnabled = valid && StrategyComboBox.SelectedItem is not null && !_isZapretStrategyTestRunning;
        StopStrategyButton.IsEnabled = valid && !_isZapretStrategyTestRunning;
        ZapretServiceToggle.IsEnabled = valid && !_isZapretStrategyTestRunning && !_isZapretServiceActionRunning;
        ServiceRunButton.IsEnabled = valid && _isZapretServiceInstalled && !_isZapretServiceTransitioning && !_isZapretStrategyTestRunning && !_isZapretServiceActionRunning;
        ApplyServiceStrategyButton.IsEnabled = valid && _isZapretServiceInstalled && StrategyComboBox.SelectedItem is not null && !_isZapretStrategyTestRunning && !_isZapretServiceActionRunning;
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
        RefreshPackageSettings(valid ? folder : string.Empty);
        RefreshFakeFiles(valid ? folder : string.Empty);
        UpdateBasicModeStatus();
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
        var items = strategies.Select(path =>
        {
            var fullPath = Path.GetFullPath(path);
            var healthKey = HealthKey(fullPath);
            _settings.ZapretStrategyHealth.TryGetValue(healthKey, out var legacyHealthy);
            _settings.ZapretStrategyStatuses.TryGetValue(healthKey, out var savedStatus);
            var healthStatus = ZapretStrategyHealthRules.FromStoredStatus(savedStatus, legacyHealthy);
            var isRecommended = fullPath.Equals(_settings.RecommendedZapretStrategyPath, StringComparison.OrdinalIgnoreCase);
            return new StrategyItem(Path.GetFileName(path), fullPath, healthStatus, isRecommended);
        })
            .OrderBy(item => ZapretStrategyHealthRules.SortOrder(item.HealthStatus))
            .ThenBy(item => item.Path, Comparer<string>.Create(ZapretStrategyComparer.ComparePaths))
            .ToArray();
        StrategyComboBox.ItemsSource = items;
        StrategyComboBox.SelectedValuePath = nameof(StrategyItem.Path);
        StrategyComboBox.SelectedValue = strategies.FirstOrDefault(path => path == selectedPath) ?? strategies.FirstOrDefault();
        RefreshBasicStrategyChoices(items);
        return strategies;
    }

    private void RefreshBasicStrategyChoices(IReadOnlyList<StrategyItem> strategies)
    {
        var choices = new List<BasicStrategyChoice>
        {
            new("Автовыбор — лучшая после проверки", null, true)
        };
        choices.AddRange(strategies.Select(item => new BasicStrategyChoice(item.Name, item.Path, false)));

        _isSyncingBasicSelectors = true;
        BasicStrategyComboBox.ItemsSource = choices;
        BasicStrategyComboBox.SelectedItem = _settings.BasicZapretAutoStrategy
            ? choices[0]
            : choices.FirstOrDefault(choice => string.Equals(choice.Path, _settings.BasicZapretStrategyPath, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        if (BasicStrategyComboBox.SelectedItem is BasicStrategyChoice selected && selected.IsAutomatic != _settings.BasicZapretAutoStrategy)
        {
            _settings.BasicZapretAutoStrategy = true;
            _settings.BasicZapretStrategyPath = null;
            SaveSettings();
        }
        _isSyncingBasicSelectors = false;
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
        var hasPackage = Directory.Exists(ZapretFolderTextBox.Text.Trim());
        if (output is null)
        {
            _isZapretServiceInstalled = false;
            _isZapretServiceRunning = false;
            _isZapretServiceTransitioning = false;
            _activeZapretServiceStrategyName = null;
            _activeZapretServiceFolder = null;
            ZapretServiceToggle.IsChecked = false;
            ZapretServiceToggle.IsEnabled = hasPackage && File.Exists(Path.Combine(ZapretFolderTextBox.Text.Trim(), "service.bat")) &&
                StrategyComboBox.SelectedItem is StrategyItem && !_isZapretStrategyTestRunning && !_isZapretServiceActionRunning;
            ServiceStatusText.Text = "Не установлена";
            ServiceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            ServiceStatusText.ToolTip = null;
            ServiceRunButton.Visibility = Visibility.Collapsed;
            ApplyServiceStrategyButton.Visibility = Visibility.Collapsed;
            UpdateBasicModeStatus();
            return;
        }
        var state = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ? "работает"
            : output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase) ? "останавливается"
            : output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) ? "запускается"
            : output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ? "остановлена"
            : "установлена";
        _isZapretServiceInstalled = true;
        _isZapretServiceRunning = state == "работает";
        _isZapretServiceTransitioning = state is "останавливается" or "запускается";
        ZapretServiceToggle.IsChecked = true;
        ZapretServiceToggle.IsEnabled = hasPackage && !_isZapretStrategyTestRunning && !_isZapretServiceActionRunning;
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
        ServiceStatusText.Text = $"{char.ToUpperInvariant(state[0])}{state[1..]}{(string.IsNullOrWhiteSpace(strategy) ? string.Empty : $" · {strategy}")}";
        _activeZapretServiceStrategyName = strategy;
        _activeZapretServiceFolder = serviceFolder;
        ServiceStatusText.ToolTip = string.IsNullOrWhiteSpace(serviceFolder) ? null : serviceFolder;
        ServiceStatusText.Foreground = _isZapretServiceRunning
            ? System.Windows.Media.Brushes.LightGreen
            : _isZapretServiceTransitioning
                ? System.Windows.Media.Brushes.Goldenrod
                : (System.Windows.Media.Brush)FindResource("MutedBrush");
        ServiceRunButton.Content = _isZapretServiceRunning ? "Остановить" : _isZapretServiceTransitioning ? "Выполняется…" : "Запустить";
        ServiceRunButton.Visibility = Visibility.Visible;
        ServiceRunButton.IsEnabled = hasPackage && !_isZapretServiceTransitioning && !_isZapretStrategyTestRunning && !_isZapretServiceActionRunning;
        ApplyServiceStrategyButton.Visibility = Visibility.Visible;
        ApplyServiceStrategyButton.IsEnabled = hasPackage && StrategyComboBox.SelectedItem is StrategyItem && !_isZapretStrategyTestRunning && !_isZapretServiceActionRunning;
        UpdateBasicModeStatus();
    }

    private void RefreshManualStrategyStatus()
    {
        var folder = ZapretFolderTextBox?.Text.Trim() ?? string.Empty;
        var executable = string.IsNullOrWhiteSpace(folder) ? string.Empty : Path.GetFullPath(Path.Combine(folder, "bin", "winws.exe"));
        var processes = Process.GetProcessesByName("winws")
            .Select(process =>
            {
                using (process)
                {
                    try
                    {
                        const string titlePrefix = "zapret: ";
                        var title = process.MainWindowTitle.Trim();
                        if (!title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase)) return (Name: (string?)null, ImagePath: (string?)null);
                        string? imagePath = null;
                        try { imagePath = process.MainModule?.FileName; }
                        catch (System.ComponentModel.Win32Exception) { }
                        catch (InvalidOperationException) { }
                        return (Name: title[titlePrefix.Length..], ImagePath: imagePath);
                    }
                    catch (InvalidOperationException) { return (Name: (string?)null, ImagePath: (string?)null); }
                    catch (System.ComponentModel.Win32Exception) { return (Name: (string?)null, ImagePath: (string?)null); }
                }
            })
            .Where(process => !string.IsNullOrWhiteSpace(process.Name))
            .ToArray();

        var strategies = processes.Select(process => process.Name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matchingProcess = processes.FirstOrDefault(process =>
        {
            if (string.IsNullOrWhiteSpace(executable)) return false;
            if (!string.IsNullOrWhiteSpace(process.ImagePath))
                return Path.GetFullPath(process.ImagePath).Equals(executable, StringComparison.OrdinalIgnoreCase);
            var candidate = Path.Combine(folder, process.Name + ".bat");
            return File.Exists(candidate) &&
                   (string.Equals(candidate, _settings.BasicZapretStrategyPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate, _settings.RecommendedZapretStrategyPath, StringComparison.OrdinalIgnoreCase));
        });
        _isManualZapretRunning = matchingProcess.Name is not null;
        _activeManualZapretStrategyName = matchingProcess.Name;
        _activeManualZapretFolder = _isManualZapretRunning ? folder : null;

        if (strategies.Length == 0)
        {
            ManualStrategyStatusText.Text = "Не запущена вручную";
            ManualStrategyStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            return;
        }

        ManualStrategyStatusText.Text = $"Ручной запуск: {string.Join(", ", strategies)}";
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

    private void SetZapretStatus(string text)
    {
        ZapretStatusText.Text = text;
        ZapretStatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Ipv4TextBox is null || ProviderComboBox.SelectedItem is not DnsProvider provider) return;
        if (!_isSyncingBasicSelectors)
        {
            _isSyncingBasicSelectors = true;
            BasicDnsProviderComboBox.SelectedItem = provider;
            _isSyncingBasicSelectors = false;
        }
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

    private void BasicDnsProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingBasicSelectors || BasicDnsProviderComboBox.SelectedItem is not DnsProvider provider) return;
        _isSyncingBasicSelectors = true;
        ProviderComboBox.SelectedItem = provider;
        _isSyncingBasicSelectors = false;
        UpdateBasicModeStatus();
    }

    private void BasicStrategyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingBasicSelectors || BasicStrategyComboBox.SelectedItem is not BasicStrategyChoice choice) return;
        _settings.BasicZapretAutoStrategy = choice.IsAutomatic;
        _settings.BasicZapretStrategyPath = choice.Path;
        if (!choice.IsAutomatic) StrategyComboBox.SelectedValue = choice.Path;
        SaveSettings();
    }

    private void BasicModuleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isBasicConnectionActionRunning) return;

        _settings.BasicDnsEnabled = BasicDnsToggle.IsChecked == true;
        _settings.BasicZapretEnabled = BasicZapretToggle.IsChecked == true;
        SaveSettings();
        UpdateBasicModeStatus();
    }

    private void BasicZapretServiceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isBasicConnectionActionRunning) return;
        _settings.BasicZapretUseService = BasicZapretServiceToggle.IsChecked == true;
        SaveSettings();
        UpdateBasicModeStatus();
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
        await RefreshServiceStatusAsync();
        UpdateBasicModeStatus();
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
            UpdateAppAutoStartStatus();
        }
        catch (Exception error)
        {
            AppAutoStartCheckBox.IsChecked = !enabled;
            UpdateAppAutoStartStatus();
            ThemedDialog.Show(this, error.Message, "Автозапуск NetShift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateAppAutoStartStatus()
    {
        var enabled = AppAutoStartCheckBox.IsChecked == true;
        AppAutoStartStatusText.Text = enabled ? "Включён" : "Отключён";
        AppAutoStartStatusText.Foreground = enabled
            ? System.Windows.Media.Brushes.LightGreen
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
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
        try
        {
            var updatedProvider = await DnsAiProviderService.GetCurrentAsync();
            var providerIndex = _dnsProviders.ToList().FindIndex(provider => provider.Name.Equals("DNS-AI", StringComparison.OrdinalIgnoreCase));
            if (providerIndex < 0) return;

            var wasSelected = ProviderComboBox.SelectedItem is DnsProvider selected && selected.Name.Equals("DNS-AI", StringComparison.OrdinalIgnoreCase);
            _dnsProviders[providerIndex] = updatedProvider;
            if (wasSelected) ProviderComboBox.SelectedItem = updatedProvider;
        }
        catch (Exception error)
        {
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

    private async void ApplyDns_Click(object sender, RoutedEventArgs e) => await ApplySelectedDnsAsync(showSuccess: true);

    private async Task<bool> ApplySelectedDnsAsync(bool showSuccess)
    {
        if (AdapterComboBox.SelectedItem is not NetworkAdapterInfo adapter)
        {
            ThemedDialog.Show(this, "Сначала выберите сетевой адаптер.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
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
            return false;
        }

        if ((legacyMode || DohEnabledCheckBox.IsChecked == true) &&
            (!Uri.TryCreate(dohTemplate, UriKind.Absolute, out var dohUri) || dohUri.Scheme != Uri.UriSchemeHttps ||
             !string.IsNullOrEmpty(dohUri.UserInfo) || !string.IsNullOrEmpty(dohUri.Fragment)))
        {
            ThemedDialog.Show(this, "Укажите корректный HTTPS-шаблон DoH.", "Проверьте шаблон DoH", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var confirmation = ThemedDialog.Show(this,
            $"Заменить DNS на выбранном интерфейсе «{adapter.Name}»?\n\n{string.Join("\n", addresses)}" +
            (legacyMode ? $"\n\nРежим Legacy: локальный прокси и DoH {dohTemplate}. При выходе NetShift прежние DNS-настройки будут восстановлены." :
                dohTemplate.Length == 0 ? "" : $"\n\nDoH через Windows DNS Client: {dohTemplate}\nОткат на незашифрованный DNS выключен.") +
            "\n\nWindows запросит подтверждение администратора.",
            "Применить DNS", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK) return false;

        if (legacyMode)
        {
            try
            {
                if (_legacyDohProxy is null || _legacyAdapterIndex != adapter.InterfaceIndex || !string.Equals(_legacyDohEndpoint, dohTemplate, StringComparison.OrdinalIgnoreCase))
                {
                    if (!await StopLegacyDnsAndRestoreAsync()) return false;
                    _legacyDohProxy = await LegacyDohProxy.CreateAsync(dohTemplate);
                    _legacyDohEndpoint = dohTemplate;
                }
                if (await RunElevatedWorkerAsync("dns-legacy-apply", adapter.InterfaceIndex.ToString(), (SupportIPv6CheckBox.IsChecked == true).ToString()))
                {
                    _legacyAdapterIndex = adapter.InterfaceIndex;
                    _settings.NativeDnsMode = false;
                    _settings.SupportIPv6 = SupportIPv6CheckBox.IsChecked == true;
                    SaveSettings();
                    if (showSuccess)
                        ThemedDialog.Show(this, "Legacy DNS включён. Локальный прокси работает, пока NetShift запущен; при выходе DNS адаптера будет восстановлен.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
                    RefreshAdapters();
                    return true;
                }
                else if (_legacyAdapterIndex is null && _legacyDohProxy is not null)
                {
                    await _legacyDohProxy.DisposeAsync();
                    _legacyDohProxy = null;
                    _legacyDohEndpoint = null;
                }
                return false;
            }
            catch (Exception error)
            {
                if (_legacyAdapterIndex is null && _legacyDohProxy is not null)
                {
                    await _legacyDohProxy.DisposeAsync();
                    _legacyDohProxy = null;
                }
                ThemedDialog.Show(this, error.Message, "Не удалось включить Legacy DNS", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        if (!await StopLegacyDnsAndRestoreAsync()) return false;
        if (await RunElevatedWorkerAsync("dns-apply", adapter.InterfaceIndex.ToString(), string.Join(',', addresses), dohTemplate))
        {
            _settings.NativeDnsMode = true;
            _settings.SupportIPv6 = SupportIPv6CheckBox.IsChecked == true;
            SaveSettings();
            if (showSuccess)
                ThemedDialog.Show(this, "Native DNS применён и сохранён в Windows. DNS-служба восстановит этот профиль при запуске компьютера.", "NetShift", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAdapters();
            return true;
        }
        return false;
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

    private async void BasicConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBasicConnectionActionRunning) return;
        var dnsSelected = BasicDnsToggle.IsChecked == true;
        var zapretSelected = BasicZapretToggle.IsChecked == true;

        _isBasicConnectionActionRunning = true;
        UpdateBasicModeStatus();
        try
        {
            await RefreshServiceStatusAsync();
            var dnsActive = IsNetShiftDnsActive();
            var zapretActive = _isZapretServiceRunning || _isManualZapretRunning;
            var anyActive = dnsActive || zapretActive;
            var hasSelection = dnsSelected || zapretSelected;
            var zapretModeMatches = !zapretSelected || (_settings.BasicZapretUseService
                ? _isZapretServiceRunning && !_isManualZapretRunning
                : _isManualZapretRunning && !_isZapretServiceRunning);
            var selectionMatches = hasSelection && dnsSelected == dnsActive && zapretSelected == zapretActive && zapretModeMatches;

            if (selectionMatches && anyActive)
            {
                if (ThemedDialog.Show(this,
                        "Отключить выбранные компоненты? DNS будет восстановлен, а zapret остановлен.",
                        "Отключить подключение", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;

                if (zapretActive && !await StopBasicZapretAsync()) return;
                if (dnsActive && !await ResetBasicDnsAsync()) return;
                return;
            }

            var folder = ZapretFolderTextBox.Text.Trim();
            if (zapretSelected && (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "service.bat"))))
            {
                ThemedDialog.Show(this, "Сначала скачайте пакет zapret в расширенном режиме.",
                    "Пакет zapret не установлен", MessageBoxButton.OK, MessageBoxImage.Information);
                NavigateToPage("zapret");
                return;
            }

            var currentService = await QueryWindowsServiceAsync("zapret");
            var needsServiceOwnership = _isZapretServiceRunning || (zapretSelected && _settings.BasicZapretUseService);
            if (needsServiceOwnership && currentService is not null && !ZapretServiceCanBeManagedFrom(folder))
            {
                ThemedDialog.Show(this,
                    "Служба zapret установлена из другой папки. NetShift не будет её останавливать или заменять.",
                    "Служба занята другим пакетом", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var stoppingUnselected = dnsActive && !dnsSelected || zapretActive && !zapretSelected;
            if (stoppingUnselected && ThemedDialog.Show(this,
                    "Отключить службы, выключенные в переключателях, и применить выбранную комбинацию?",
                    "Применить выбор", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            if (zapretActive && !zapretSelected && !await StopBasicZapretAsync()) return;
            if (dnsActive && !dnsSelected && !await ResetBasicDnsAsync()) return;

            var dnsAppliedNow = false;
            if (dnsSelected && !dnsActive)
            {
                if (!await ApplySelectedDnsAsync(showSuccess: false)) return;
                dnsAppliedNow = true;
            }
            if (zapretSelected && (!zapretActive || dnsAppliedNow || !zapretModeMatches) && !await ConnectBasicZapretAsync(folder)) return;
        }
        finally
        {
            _isBasicConnectionActionRunning = false;
            await RefreshServiceStatusAsync();
            UpdateBasicModeStatus();
        }
    }

    private async Task<bool> ConnectBasicZapretAsync(string folder)
    {
        var requestedStrategyPath = _settings.BasicZapretAutoStrategy ? string.Empty : _settings.BasicZapretStrategyPath ?? string.Empty;
        var runtimeMode = _settings.BasicZapretUseService ? "службой Windows" : "фоновым процессом без службы";
        var prompt = _settings.BasicZapretAutoStrategy
            ? $"Проверить весь список стратегий zapret, выбрать лучшую и запустить её {runtimeMode}? Пакетные скрипты могут изменить обработку сетевого трафика. Windows запросит подтверждение администратора."
            : $"Проверить весь список стратегий, затем запустить выбранную {Path.GetFileName(requestedStrategyPath)} {runtimeMode}? Пакетные скрипты могут изменить обработку сетевого трафика. Windows запросит подтверждение администратора.";
        if (ThemedDialog.Show(this, prompt, "Подключить zapret", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;

        var started = await RunZapretStrategySweepAsync(folder, isInitialCheck: false,
            startAfterCheck: true, requestedStrategyPath: requestedStrategyPath, useService: _settings.BasicZapretUseService);
        return started && (_settings.BasicZapretUseService ? _isZapretServiceRunning : _isManualZapretRunning);
    }

    private async Task<bool> ApplyZapretServiceStrategyAsync(string strategyPath)
    {
        var folder = Path.GetDirectoryName(strategyPath);
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var isInstalled = await QueryWindowsServiceAsync("zapret") is not null;
        var message = isInstalled
            ? $"Заменить установленную службу zapret стратегией {Path.GetFileName(strategyPath)}? NetShift изменит её только если она установлена из выбранной папки или папки NetShift. Потребуются права администратора."
            : $"Установить стратегию {Path.GetFileName(strategyPath)} как автоматическую службу Windows?\n\n{folder}\n\nПотребуются права администратора.";
        if (ThemedDialog.Show(this, message, isInstalled ? "Изменить стратегию службы" : "Установить службу zapret",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return false;

        var installed = await RunElevatedWorkerAsync("zapret-service-install", strategyPath);
        if (installed) installed = await WaitForZapretServiceRunningAsync();
        await RefreshServiceStatusAsync();
        return installed && _isZapretServiceRunning;
    }

    private async Task<bool> StopBasicZapretAsync()
    {
        var folder = ZapretFolderTextBox.Text.Trim();
        if (!Directory.Exists(folder)) folder = _activeZapretServiceFolder ?? _activeManualZapretFolder ?? string.Empty;
        if (!Directory.Exists(folder))
        {
            ThemedDialog.Show(this, "Папка активного пакета zapret недоступна. Откройте расширенный режим для проверки.",
                "Нельзя остановить zapret", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var stopped = await RunElevatedWorkerAsync("zapret-basic-stop", folder);
        await RefreshServiceStatusAsync();
        return stopped && !_isZapretServiceRunning && !_isManualZapretRunning;
    }

    private async Task<bool> ResetBasicDnsAsync()
    {
        var legacyActive = IsDnsRegistryProfileActive(@"SOFTWARE\NetShift\LegacyDns");
        if (legacyActive && !await StopLegacyDnsAndRestoreAsync()) return false;
        if (IsDnsRegistryProfileActive(@"SOFTWARE\NetShift\Dns"))
        {
            if (!await RunElevatedWorkerAsync("dns-reset-managed")) return false;
            _settings.NativeDnsMode = false;
            SaveSettings();
            RefreshAdapters();
        }
        return !IsNetShiftDnsActive();
    }

    private async Task<bool> WaitForZapretServiceRunningAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var status = await QueryWindowsServiceAsync("zapret");
            if (status?.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) == true) return true;
            if (status is null || !status.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)) return false;
            await Task.Delay(500);
        }
        return false;
    }

    private static bool ZapretServiceCanBeManagedFrom(string selectedFolder)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            var imagePath = key?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(imagePath)) return true;
            var match = Regex.Match(imagePath, "^\\s*\\\"(?<path>[^\\\"]*winws\\.exe)\\\"|^\\s*(?<path>[^\\s]*winws\\.exe)", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            var executable = Path.GetFullPath(match.Groups["path"].Value);
            if (executable.Equals(Path.GetFullPath(Path.Combine(selectedFolder, "bin", "winws.exe")), StringComparison.OrdinalIgnoreCase))
                return true;

            var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var managedRoot = Path.GetFullPath(Path.Combine(drive, "NetShift")) + Path.DirectorySeparatorChar;
            if (!executable.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase)) return false;
            var binFolder = Path.GetDirectoryName(executable);
            var packageFolder = binFolder is null ? null : Path.GetDirectoryName(binFolder);
            return binFolder is not null && Path.GetFileName(binFolder).Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                   packageFolder is not null && Path.GetFileName(packageFolder).StartsWith("zapret-", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(Path.Combine(packageFolder, "service.bat"));
        }
        catch { return false; }
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

    private async void ZapretServiceToggle_Click(object sender, RoutedEventArgs e)
    {
        await RunZapretServiceActionAsync(async () =>
        {
            if (ZapretServiceToggle.IsChecked == true)
                await InstallZapretServiceAsync();
            else
                await RemoveZapretServiceAsync();
        });
    }

    private async void InstallService_Click(object sender, RoutedEventArgs e) =>
        await RunZapretServiceActionAsync(InstallZapretServiceAsync);

    private async Task RunZapretServiceActionAsync(Func<Task> action)
    {
        if (_isZapretServiceActionRunning || _isZapretStrategyTestRunning) return;
        _isZapretServiceActionRunning = true;
        StrategyComboBox.IsEnabled = false;
        StartStrategyButton.IsEnabled = false;
        StopStrategyButton.IsEnabled = false;
        CheckActiveStrategyButton.IsEnabled = false;
        ZapretServiceToggle.IsEnabled = false;
        ServiceRunButton.IsEnabled = false;
        ApplyServiceStrategyButton.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            _isZapretServiceActionRunning = false;
            UpdateZapretInventory();
        }
    }

    private async Task InstallZapretServiceAsync()
    {
        if (StrategyComboBox.SelectedItem is not StrategyItem strategy) return;
        var installed = await ApplyZapretServiceStrategyAsync(strategy.Path);
        if (installed) _ = CheckZapretStrategiesAsync(isInitialCheck: !_settings.InitialZapretStrategyCheckCompleted);
    }

    private async void ToggleServiceRun_Click(object sender, RoutedEventArgs e) =>
        await RunZapretServiceActionAsync(() => _isZapretServiceRunning ? StopZapretServiceAsync() : StartZapretServiceAsync());

    private async Task StartZapretServiceAsync()
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (await RunElevatedWorkerAsync("zapret-service-start", folder))
            _ = CheckZapretStrategiesAsync(isInitialCheck: !_settings.InitialZapretStrategyCheckCompleted);
    }

    private async Task StopZapretServiceAsync()
    {
        if (!TryGetZapretFolder(out var folder)) return;
        if (ThemedDialog.Show(this, "Остановить службу zapret? Это временно прекратит работу выбранной стратегии.",
                "Остановить службу", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunElevatedWorkerAsync("zapret-service-stop", folder);
    }

    private async Task RemoveZapretServiceAsync()
    {
        if (!TryGetZapretFolder(out var folder))
            return;
        if (await QueryWindowsServiceAsync("zapret") is null) return;
        if (ThemedDialog.Show(this,
                $"Остановить и удалить службу zapret из выбранной папки или папки NetShift?\n\nТекущий пакет: {folder}\n\nСлужбы и процессы других программ не затрагиваются.",
                "Удалить службу zapret", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunElevatedWorkerAsync("zapret-service-remove", folder);
    }

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
        _settings.ZapretStrategyStatuses.Clear();
        _settings.RecommendedZapretStrategyPath = null;
        SaveSettings();
        UpdateZapretInventory();
        CheckActiveStrategyButton.IsEnabled = false;
        var displayStrategyName = Path.GetFileNameWithoutExtension(activeStrategyPath);
        ZapretHealthStatusText.Text = $"Проверяю цели из targets.txt через {displayStrategyName}…";
        ZapretHealthStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        ZapretTestPhaseText.Text = "Проверка выполняется";
        ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
        ZapretTestStrategyText.Text = $"Активная служба: {displayStrategyName}. Тестирую HTTP/1.1, TLS 1.2, TLS 1.3 и ping; службу не прерываю.";
        ZapretTestProgressBar.IsIndeterminate = true;
        ZapretTestProgressBar.Visibility = Visibility.Visible;
        ResetZapretTargetStatuses("Проверяется…", System.Windows.Media.Brushes.DodgerBlue);
        try
        {
            ZapretConnectivityTester.EnsureCurlAvailable();
            var targets = ZapretConnectivityTester.LoadTargets(Path.GetDirectoryName(activeStrategyPath)!);
            var result = await ZapretConnectivityTester.CheckTargetsAsync(targets);
            var healthStatus = ZapretStrategyHealthRules.FromResults(result.HttpPassed, result.HttpTotal, result.PingPassed, result.PingTotal);
            var healthy = healthStatus == ZapretStrategyHealthStatus.Green;
            SetZapretTargetStatuses(result.Groups);
            _settings.ZapretStrategyHealth.Clear();
            _settings.ZapretStrategyStatuses.Clear();
            SaveStrategyHealth(activeStrategyPath, healthStatus);
            _settings.RecommendedZapretStrategyPath = Path.GetFullPath(activeStrategyPath);
            if (isInitialCheck) _settings.InitialZapretStrategyCheckCompleted = true;
            SaveSettings();
            UpdateZapretInventory();

            ZapretHealthStatusText.Text = $"{displayStrategyName}: HTTP/TLS {result.HttpPassed}/{result.HttpTotal}; ping {result.PingPassed}/{result.PingTotal}.";
            var hasAnySuccess = result.HttpPassed + result.PingPassed > 0;
            ZapretHealthStatusText.Foreground = healthy ? System.Windows.Media.Brushes.LightGreen
                : hasAnySuccess ? System.Windows.Media.Brushes.Goldenrod
                : System.Windows.Media.Brushes.IndianRed;
            ZapretTestPhaseText.Text = healthy ? "Все проверки пройдены"
                : hasAnySuccess ? "Есть успешные проверки"
                : "Нет успешных проверок";
            ZapretTestPhaseText.Foreground = healthy ? System.Windows.Media.Brushes.LightGreen
                : hasAnySuccess ? System.Windows.Media.Brushes.Goldenrod
                : System.Windows.Media.Brushes.IndianRed;
        }
        catch (Exception error)
        {
            ZapretHealthStatusText.Text = $"Не удалось проверить активную стратегию: {error.Message}";
            ZapretTestPhaseText.Text = "Ошибка проверки";
            ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        finally
        {
            ZapretTestProgressBar.IsIndeterminate = false;
            ZapretTestProgressBar.Visibility = Visibility.Collapsed;
            CheckActiveStrategyButton.IsEnabled = !_isZapretStrategyTestRunning && Directory.Exists(ZapretFolderTextBox.Text.Trim());
        }
    }

    private async Task<bool> RunZapretStrategySweepAsync(
        string folder,
        bool isInitialCheck,
        bool startAfterCheck = false,
        string? requestedStrategyPath = null,
        bool useService = true)
    {
        var strategyPaths = Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, Comparer<string>.Create(ZapretStrategyComparer.ComparePaths))
            .ToArray();
        if (strategyPaths.Length == 0) return false;

        foreach (var strategyPath in strategyPaths)
        {
            var healthKey = HealthKey(strategyPath);
            _settings.ZapretStrategyHealth.Remove(healthKey);
            _settings.ZapretStrategyStatuses.Remove(healthKey);
        }
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
        ZapretServiceToggle.IsEnabled = false;
        ServiceRunButton.IsEnabled = false;
        ApplyServiceStrategyButton.IsEnabled = false;
        ZapretHealthStatusText.Text = "Подготовка проверки…";
        ZapretHealthStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        ZapretTestPhaseText.Text = "Подготовка";
        ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
        ZapretTestStrategyText.Text = startAfterCheck
            ? $"Оригинальные general*.bat проверяются по очереди; затем стратегия запустится {(useService ? "как служба Windows" : "в фоне без службы")} ."
            : "Оригинальные general*.bat запускаются по очереди и останавливаются после проверки. Служба Windows не меняется.";
        ZapretTestStrategyText.Visibility = Visibility.Collapsed;
        ZapretTestProgressBar.IsIndeterminate = false;
        ZapretTestProgressBar.Minimum = 0;
        ZapretTestProgressBar.Maximum = 1;
        ZapretTestProgressBar.Value = 0;
        ZapretTestProgressBar.Visibility = Visibility.Visible;
        ResetZapretTargetStatuses("Ожидает", System.Windows.Media.Brushes.Gray);

        try
        {
            var testTask = startAfterCheck
                ? RunElevatedWorkerAsync("zapret-autoconnect", folder, progressPath,
                    _settings.BasicZapretAutoStrategy ? "auto" : "manual",
                    requestedStrategyPath ?? string.Empty,
                    useService ? "service" : "manual")
                : RunElevatedWorkerAsync("zapret-test-strategies", folder, progressPath);
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
                return false;
            }

            await ApplyZapretTestProgressAsync(progressPath);
            if (startAfterCheck) await RefreshServiceStatusAsync();
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
            return true;
        }
        catch (Exception error)
        {
            ZapretHealthStatusText.Text = $"Не удалось проверить стратегии: {error.Message}";
            ZapretTestPhaseText.Text = "Ошибка проверки";
            ZapretTestPhaseText.Foreground = System.Windows.Media.Brushes.IndianRed;
            return false;
        }
        finally
        {
            _isZapretStrategyTestRunning = false;
            ZapretTestStrategyText.Visibility = Visibility.Visible;
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
            "checking" => "Проверка",
            "failed" => "Не прошла",
            "scored" => "Результат получен",
            "completed" => progress.RecommendedPath is null ? "Подходящая не найдена" : "Проверка завершена",
            _ => progress.Phase
        };
        ZapretTestPhaseText.Foreground = progress.Phase switch
        {
            "checking" or "starting" or "scored" => System.Windows.Media.Brushes.DodgerBlue,
            "failed" => System.Windows.Media.Brushes.IndianRed,
            "completed" when progress.RecommendedPath is null => System.Windows.Media.Brushes.IndianRed,
            "completed" when progress.HttpTotal > 0 && progress.HttpPassed == progress.HttpTotal && progress.PingPassed == progress.PingTotal => System.Windows.Media.Brushes.LightGreen,
            "completed" => System.Windows.Media.Brushes.Goldenrod,
            _ => (System.Windows.Media.Brush)FindResource("MutedBrush")
        };
        ZapretHealthStatusText.Text = progress.TotalProbeCount > 0 && (progress.Phase is "checking" or "scored")
            ? $"Проверяю {progress.CompletedProbeCount}/{progress.TotalProbeCount}"
            : progress.Message;
        ZapretHealthStatusText.Foreground = progress.Phase switch
        {
            "checking" or "starting" or "scored" => System.Windows.Media.Brushes.DodgerBlue,
            "completed" when progress.RecommendedPath is null => System.Windows.Media.Brushes.IndianRed,
            "completed" when progress.HttpTotal > 0 && progress.HttpPassed == progress.HttpTotal && progress.PingPassed == progress.PingTotal => System.Windows.Media.Brushes.LightGreen,
            "completed" => System.Windows.Media.Brushes.Goldenrod,
            "failed" when progress.Completed => System.Windows.Media.Brushes.IndianRed,
            _ => (System.Windows.Media.Brush)FindResource("MutedBrush")
        };
        ZapretTestStrategyText.Text = progress.StrategyPath is null
            ? _isBasicConnectionActionRunning && BasicZapretToggle.IsChecked == true
                ? $"Проверено {progress.StrategyIndex} из {progress.StrategyCount}. Запускаю {(BasicZapretServiceToggle.IsChecked == true ? "службу Windows" : "фоновый процесс")}."
                : $"Проверено {progress.StrategyIndex} из {progress.StrategyCount}. Служба Windows не меняется."
            : $"{Path.GetFileNameWithoutExtension(progress.StrategyPath)} · вариант {progress.StrategyIndex} из {progress.StrategyCount}.";
        ZapretTestStrategyText.Visibility = progress.Completed ? Visibility.Visible : Visibility.Collapsed;
        ZapretTestProgressBar.Minimum = 0;
        ZapretTestProgressBar.Maximum = Math.Max(1, progress.TotalProbeCount);
        ZapretTestProgressBar.Value = Math.Clamp(progress.CompletedProbeCount, 0, Math.Max(1, progress.TotalProbeCount));
        ZapretTestProgressBar.Visibility = progress.Completed ? Visibility.Collapsed : Visibility.Visible;
        if (_isBasicConnectionActionRunning)
        {
            BasicModeStatusText.Text = progress.TotalProbeCount > 0
                ? $"Проверка zapret: {progress.CompletedProbeCount}/{progress.TotalProbeCount}"
                : "Подготовка проверки zapret…";
            BasicModeStatusText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
            BasicConnectionButton.Content = "ПРОВЕРКА…";
        }

        var pendingStatus = progress.Phase == "checking" ? "Проверяется…" : progress.Phase == "failed" ? "Не проверено" : "Ожидает";
        SetZapretTargetStatuses(progress.Groups, pendingStatus);

        var healthChanged = false;
        if (progress.StrategyPath is not null && (progress.Phase is "failed" or "scored"))
        {
            var healthStatus = progress.Phase == "scored"
                ? ZapretStrategyHealthRules.FromResults(progress.HttpPassed, progress.HttpTotal, progress.PingPassed, progress.PingTotal)
                : ZapretStrategyHealthStatus.Red;
            SaveStrategyHealth(progress.StrategyPath, healthStatus);
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

    private void ResetZapretTargetStatuses(string status, System.Windows.Media.Brush color)
    {
        foreach (var target in new[] { "Discord", "YouTube", "Google", "Cloudflare", "DNS", "Other" })
            SetZapretTargetStatus(target, status, color);
    }

    private void SetZapretTargetStatuses(IReadOnlyDictionary<string, ZapretTestGroupSummary> groups, string pendingStatus = "Ожидает")
    {
        foreach (var target in new[] { "Discord", "YouTube", "Google", "Cloudflare", "DNS", "Other" })
        {
            if (!groups.TryGetValue(target, out var summary) || summary.HttpTotal + summary.PingTotal == 0)
            {
                SetZapretTargetStatus(target, pendingStatus, pendingStatus == "Проверяется…"
                    ? System.Windows.Media.Brushes.DodgerBlue
                    : System.Windows.Media.Brushes.Gray);
                continue;
            }

            var hasAnySuccess = summary.HttpPassed + summary.PingPassed > 0;
            var allPassed = summary.HttpPassed == summary.HttpTotal && summary.PingPassed == summary.PingTotal;
            var color = allPassed ? System.Windows.Media.Brushes.LightGreen
                : hasAnySuccess ? System.Windows.Media.Brushes.Goldenrod
                : System.Windows.Media.Brushes.IndianRed;
            SetZapretTargetStatus(target,
                $"{summary.HttpPassed}/{summary.HttpTotal} HTTP · {summary.PingPassed}/{summary.PingTotal} ping", color);
        }
    }

    private static string HealthKey(string path) => Path.GetFullPath(path).ToUpperInvariant();

    private void SaveStrategyHealth(string path, ZapretStrategyHealthStatus status)
    {
        var healthKey = HealthKey(path);
        _settings.ZapretStrategyStatuses[healthKey] = status.ToString();
        _settings.ZapretStrategyHealth[healthKey] = status == ZapretStrategyHealthStatus.Green;
    }

    private void ToggleZapretAdvanced_Click(object sender, RoutedEventArgs e)
    {
        var isExpanded = PackageSettingsPanel.Visibility != Visibility.Visible;
        var visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
        PackageSettingsPanel.Visibility = visibility;
        ToolsAndDiagnosticsPanel.Visibility = visibility;
        ZapretAdvancedToggleButton.Content = isExpanded
            ? "Дополнительные настройки и диагностика ▲"
            : "Дополнительные настройки и диагностика ▼";
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
                DownloadStatusText.Text = string.Empty;
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
                DownloadStatusText.Text = string.Empty;
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
        public Dictionary<string, string> ZapretStrategyStatuses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? RecommendedZapretStrategyPath { get; set; }
        public bool NativeDnsMode { get; set; } = true;
        public bool SupportIPv6 { get; set; } = true;
        public bool ConfigureVpnAdapters { get; set; }
        public string? DnsProviderName { get; set; }
        public bool BasicDnsEnabled { get; set; } = true;
        public bool BasicZapretEnabled { get; set; } = true;
        public bool BasicZapretUseService { get; set; } = true;
        public bool BasicZapretAutoStrategy { get; set; } = true;
        public string? BasicZapretStrategyPath { get; set; }
        public string LastSelectedPage { get; set; } = "basic";
    }

    private sealed record PackageChoice(string Name, string Value)
    {
        public override string ToString() => Name;
    }
    private sealed record BasicStrategyChoice(string Name, string? Path, bool IsAutomatic);
    private sealed record StrategyItem(string Name, string Path, ZapretStrategyHealthStatus HealthStatus, bool IsRecommended)
    {
        public Visibility HealthVisibility => HealthStatus == ZapretStrategyHealthStatus.Unknown ? Visibility.Collapsed : Visibility.Visible;
        public System.Windows.Media.Brush HealthBrush => HealthStatus switch
        {
            ZapretStrategyHealthStatus.Green => System.Windows.Media.Brushes.LightGreen,
            ZapretStrategyHealthStatus.Yellow => System.Windows.Media.Brushes.Goldenrod,
            ZapretStrategyHealthStatus.Red => System.Windows.Media.Brushes.IndianRed,
            _ => System.Windows.Media.Brushes.Gray
        };
        public string HealthTooltip => HealthStatus switch
        {
            ZapretStrategyHealthStatus.Green => "Все HTTP/TLS проверки и ping пройдены",
            ZapretStrategyHealthStatus.Yellow => "Часть проверок прошла",
            ZapretStrategyHealthStatus.Red => "Ни одна проверка не прошла",
            _ => "Стратегия ещё не проверялась"
        } + (IsRecommended ? ". Лучшая по числу успешных проверок" : string.Empty);
        public override string ToString() => Name;
    }
}
