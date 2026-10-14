using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetShift.App;

namespace NetShift.UiScreenshots;

internal static class Program
{
    private static readonly string[] Pages = ["basic", "dns", "zapret", "settings"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Length > 2)
        {
            Console.Error.WriteLine("Usage: NetShift.UiScreenshots <output-directory> [basic|dns|zapret|settings]");
            return 2;
        }

        var outputDirectory = Path.GetFullPath(args[0]);
        var requestedPages = args.Length == 2 ? [args[1]] : Pages;
        if (requestedPages.Any(page => !Pages.Contains(page, StringComparer.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Unknown page. Choose: {string.Join(", ", Pages)}");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);
        var app = new NetShift.App.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        foreach (var page in requestedPages)
        {
            var window = new MainWindow(screenshotPreview: true, screenshotPage: page);
            var width = page.Equals("basic", StringComparison.OrdinalIgnoreCase) ? 560 : 1120;
            const int height = 760;
            window.Width = width;
            window.Height = height;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -15000;
            window.Top = -15000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(width * dpi.DpiScaleX),
                (int)Math.Ceiling(height * dpi.DpiScaleY),
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            bitmap.Render(window);

            var outputPath = Path.Combine(outputDirectory, $"{page.ToLowerInvariant()}.png");
            using var stream = File.Create(outputPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            window.Close();
            Console.WriteLine($"{page}: {outputPath} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
        }

        app.Shutdown();
        return 0;
    }
}
