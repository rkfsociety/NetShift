using System.Windows;

namespace NetShift.App;

public partial class MainWindow
{
    private async Task<(string Name, bool Reachable)> ProbeZapretTargetWithStatusAsync(string name, string url)
    {
        var result = await ProbeZapretTargetAsync(name, url);
        SetZapretTargetStatus(name, result.Reachable ? "Доступен" : "Нет ответа",
            result.Reachable ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.IndianRed);
        return result;
    }

    private void SetZapretTargetStatus(string name, string status, System.Windows.Media.Brush color)
    {
        var (indicator, statusText) = name switch
        {
            "Discord" => (DiscordTestIndicator, DiscordTestStatusText),
            "YouTube" => (YouTubeTestIndicator, YouTubeTestStatusText),
            "Google" => (GoogleTestIndicator, GoogleTestStatusText),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Неизвестный тестируемый сайт.")
        };
        indicator.Fill = color;
        statusText.Text = status;
        statusText.Foreground = color;
    }
}
