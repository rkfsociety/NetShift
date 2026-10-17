using NetShift.App.Services;

namespace NetShift.App.Models;

internal sealed record ZapretStrategyRanking(
    string Path,
    int HttpPassed,
    int HttpTotal,
    int PingPassed,
    int PingTotal,
    double? YouTubeSpeedMbps = null)
{
    internal static double? NormalizeYouTubeSpeed(double? speedMbps) => speedMbps is > 0 ? speedMbps : null;

    internal static IReadOnlyList<ZapretStrategyRanking> Order(IEnumerable<ZapretStrategyRanking> strategies) => strategies
        .OrderByDescending(strategy => NormalizeYouTubeSpeed(strategy.YouTubeSpeedMbps).HasValue)
        .ThenByDescending(strategy => NormalizeYouTubeSpeed(strategy.YouTubeSpeedMbps))
        .ThenByDescending(strategy => strategy.HttpPassed)
        .ThenByDescending(strategy => strategy.PingPassed)
        .ThenBy(strategy => strategy.Path, Comparer<string>.Create(ZapretStrategyComparer.ComparePaths))
        .ToArray();
}
