using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NetShift.App.Models;

namespace NetShift.App;

public partial class YouTubeSpeedTestWindow : Window
{
    private const string VirtualHost = "netshift.youtube.test";
    private static readonly TimeSpan MaximumMeasurementDuration = TimeSpan.FromSeconds(105);
    private static readonly TimeSpan PlayerReadyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MediaStartTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TransferSampleDuration = TimeSpan.FromSeconds(30);
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "NetShift-YouTube-SpeedTest");
    private const string TestPage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <style>html,body,#player{width:100%;height:100%;margin:0;background:#000;overflow:hidden}</style>
        <script>
        let player;
        function notify(value){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(value);}
        function onYouTubeIframeAPIReady(){
          player=new YT.Player('player',{
            width:'640',height:'360',videoId:'M7lc1UVf-VE',
            playerVars:{autoplay:1,controls:0,disablekb:1,fs:0,playsinline:1,rel:0,origin:location.origin},
            events:{
              onReady:event=>{event.target.mute();event.target.setVolume(0);notify('ready');event.target.playVideo();},
              onStateChange:event=>notify('state:'+event.data),
              onError:event=>notify('error:'+event.data)
            }
          });
        }
        </script><script src="https://www.youtube.com/iframe_api"></script>
        </head><body><div id="player"></div></body></html>
        """;

    private readonly object _transferLock = new();
    private WebView2? _browser;
    private TaskCompletionSource<bool>? _playerReady;
    private TaskCompletionSource<bool>? _playerPlaying;
    private Stopwatch? _transferStopwatch;
    private long _videoBytes;
    private string? _mediaReadError;
    private string? _runtimeUnavailableError;
    private string? _currentTestDirectory;

    public YouTubeSpeedTestWindow()
    {
        InitializeComponent();
    }

    internal bool RuntimeUnavailable => _runtimeUnavailableError is not null;

    internal async Task<YouTubeSpeedTestMeasurement> MeasureAsync(string strategyName, CancellationToken cancellationToken = default)
    {
        if (_runtimeUnavailableError is not null)
            return YouTubeSpeedTestMeasurement.Unsupported(_runtimeUnavailableError);

        StatusText.Text = $"Скорость видеопотока · {strategyName} · звук отключён";
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(MaximumMeasurementDuration);
        var operationToken = operationTimeout.Token;
        try
        {
            ResetMeasurement();
            await CreateMutedBrowserAsync(operationToken);
            await NavigateToTestVideoAsync(operationToken);
            await _playerReady!.Task.WaitAsync(PlayerReadyTimeout, operationToken);
            await _playerPlaying!.Task.WaitAsync(MediaStartTimeout, operationToken);

            var mediaDeadline = Stopwatch.StartNew();
            while (mediaDeadline.Elapsed < MediaStartTimeout)
            {
                operationToken.ThrowIfCancellationRequested();
                lock (_transferLock)
                {
                    if (_transferStopwatch is not null) break;
                }
                await Task.Delay(100, operationToken);
            }

            Stopwatch? sample;
            string? mediaReadError;
            lock (_transferLock)
            {
                sample = _transferStopwatch;
                mediaReadError = _mediaReadError;
            }
            if (sample is null)
                return YouTubeSpeedTestMeasurement.Failed(mediaReadError ?? "YouTube-плеер запустился, но видеопоток не начал передаваться.");

            StatusText.Text = $"Измеряю видеопоток 30 секунд · {strategyName} · звук отключён";
            while (sample.Elapsed < TransferSampleDuration)
            {
                operationToken.ThrowIfCancellationRequested();
                await Task.Delay(100, operationToken);
            }

            long bytes;
            lock (_transferLock) bytes = _videoBytes;
            var durationMilliseconds = (long)sample.Elapsed.TotalMilliseconds;
            await StopPlayerAsync();
            return YouTubeSpeedTestMeasurement.FromTransfer(bytes, durationMilliseconds);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _runtimeUnavailableError = "Не найден Microsoft Edge WebView2 Runtime. Установите Evergreen WebView2 Runtime и повторите проверку.";
            return YouTubeSpeedTestMeasurement.Unsupported(_runtimeUnavailableError);
        }
        catch (TimeoutException)
        {
            return YouTubeSpeedTestMeasurement.Failed("YouTube не начал воспроизведение за отведённое время.");
        }
        catch (OperationCanceledException) when (operationTimeout.IsCancellationRequested)
        {
            var message = cancellationToken.IsCancellationRequested
                ? "Проверка скорости YouTube была отменена."
                : "YouTube не завершил замер за 105 секунд.";
            return YouTubeSpeedTestMeasurement.Failed(message);
        }
        catch (Exception error)
        {
            return YouTubeSpeedTestMeasurement.Failed($"Не удалось измерить скорость YouTube: {error.Message}");
        }
        finally
        {
            await StopPlayerAsync();
            await DisposeBrowserAsync();
        }
    }

    private void ResetMeasurement()
    {
        _playerReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _playerPlaying = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_transferLock)
        {
            _transferStopwatch = null;
            _videoBytes = 0;
            _mediaReadError = null;
        }
    }

    private async Task CreateMutedBrowserAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw;
        }

        Directory.CreateDirectory(TestRoot);
        var testDirectory = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        _currentTestDirectory = testDirectory;
        var pageDirectory = Path.Combine(testDirectory, "page");
        var profileDirectory = Path.Combine(testDirectory, "profile");
        Directory.CreateDirectory(pageDirectory);
        Directory.CreateDirectory(profileDirectory);
        await File.WriteAllTextAsync(Path.Combine(pageDirectory, "index.html"), TestPage, cancellationToken);

        var environment = await CoreWebView2Environment.CreateAsync(null, profileDirectory);
        _browser = new WebView2();
        BrowserHost.Children.Add(_browser);
        await _browser.EnsureCoreWebView2Async(environment);
        var core = _browser.CoreWebView2;
        core.IsMuted = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, pageDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals(VirtualHost, StringComparison.OrdinalIgnoreCase))
                args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.WebMessageReceived += HandlePlayerMessage;
        core.WebResourceResponseReceived += HandleMediaResponse;
    }

    private async Task NavigateToTestVideoAsync(CancellationToken cancellationToken)
    {
        if (_browser?.CoreWebView2 is null) throw new InvalidOperationException("Не удалось создать WebView2 для проверки YouTube.");
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) navigation.TrySetResult(true);
            else navigation.TrySetException(new InvalidOperationException($"WebView2 не загрузил тестовую страницу YouTube ({args.WebErrorStatus})."));
        }

        _browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        try
        {
            _browser.CoreWebView2.Navigate($"https://{VirtualHost}/index.html?run={Guid.NewGuid():N}");
            await navigation.Task.WaitAsync(PlayerReadyTimeout, cancellationToken);
        }
        finally
        {
            _browser.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private void HandlePlayerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) ||
            !source.Host.Equals(VirtualHost, StringComparison.OrdinalIgnoreCase)) return;

        string message;
        try { message = args.TryGetWebMessageAsString(); }
        catch (InvalidOperationException) { return; }

        if (message == "ready")
        {
            if (_browser?.CoreWebView2 is { IsMuted: false } core) core.IsMuted = true;
            _playerReady?.TrySetResult(true);
        }
        else if (message == "state:1") _playerPlaying?.TrySetResult(true);
        else if (message.StartsWith("error:", StringComparison.Ordinal))
            _playerPlaying?.TrySetException(new InvalidOperationException($"YouTube отказал в воспроизведении тестового ролика ({message[6..]})."));
    }

    private async void HandleMediaResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (!IsGoogleVideoPlayback(args.Request.Uri) || args.Response.StatusCode >= 400) return;
        try
        {
            using var stream = await args.Response.GetContentAsync();
            if (stream is null) return;
            await Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                    if (bytesRead == 0) break;
                    lock (_transferLock)
                    {
                        _transferStopwatch ??= Stopwatch.StartNew();
                        _videoBytes += bytesRead;
                    }
                }
            });
        }
        catch (Exception error)
        {
            lock (_transferLock) _mediaReadError ??= error.Message;
        }
    }

    private static bool IsGoogleVideoPlayback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase)) &&
        uri.AbsolutePath.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase);

    private async Task StopPlayerAsync()
    {
        if (_browser?.CoreWebView2 is not { } core) return;
        try { await core.ExecuteScriptAsync("if(typeof player!=='undefined'&&player){player.pauseVideo();}"); }
        catch (InvalidOperationException) { }
    }

    private async Task DisposeBrowserAsync()
    {
        var browser = _browser;
        _browser = null;
        var testDirectory = _currentTestDirectory;
        _currentTestDirectory = null;
        if (browser is not null)
        {
            var core = browser.CoreWebView2;
            if (core is not null)
            {
                try { core.Stop(); } catch (InvalidOperationException) { }
                core.WebMessageReceived -= HandlePlayerMessage;
                core.WebResourceResponseReceived -= HandleMediaResponse;
            }

            BrowserHost.Children.Remove(browser);
            browser.Dispose();
        }

        if (testDirectory is not null) _ = DeleteTestDirectoryWhenReleasedAsync(testDirectory);
    }

    private static async Task DeleteTestDirectoryWhenReleasedAsync(string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) { await Task.Delay(500); }
            catch (UnauthorizedAccessException) { await Task.Delay(500); }
            catch (Exception) { return; }
        }
    }
}
