using System.Windows;

namespace NetShift.App;

public partial class MainWindow
{
    private void SetZapretTargetStatus(string name, string status, System.Windows.Media.Brush color)
    {
        var (indicator, statusText) = name switch
        {
            "Discord" => (DiscordTestIndicator, DiscordTestStatusText),
            "YouTube" => (YouTubeTestIndicator, YouTubeTestStatusText),
            "Google" => (GoogleTestIndicator, GoogleTestStatusText),
            "Cloudflare" => (CloudflareTestIndicator, CloudflareTestStatusText),
            "DNS" => (DnsTestIndicator, DnsTestStatusText),
            "Other" => (OtherTestIndicator, OtherTestStatusText),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Неизвестный тестируемый сайт.")
        };
        indicator.Fill = color;
        statusText.Text = status;
        statusText.Foreground = color;
    }

    private void SetYouTubeSpeedStatus(Models.YouTubeSpeedTestMeasurement measurement)
    {
        var color = measurement.Mbps switch
        {
            double mbps when mbps >= Models.YouTubeSpeedTestMeasurement.MinimumRecommended720pMbps => System.Windows.Media.Brushes.LightGreen,
            > 0 => System.Windows.Media.Brushes.Goldenrod,
            0 => System.Windows.Media.Brushes.IndianRed,
            _ => System.Windows.Media.Brushes.Goldenrod
        };
        YouTubeTestIndicator.Fill = color;
        YouTubeTestStatusText.Text = measurement.Mbps is > 0 and double displayedMbps
            ? $"{Models.YouTubeSpeedTestMeasurement.FormatMbps(displayedMbps)} Мбит/с · без звука"
            : measurement.Error ?? "Скорость не измерена";
        YouTubeTestStatusText.Foreground = color;
    }
}
