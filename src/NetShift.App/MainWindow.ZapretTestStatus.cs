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
}
