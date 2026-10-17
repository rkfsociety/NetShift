namespace NetShift.App.Models;

internal sealed record ZapretTestProgress(
    string? StrategyPath,
    string Phase,
    int StrategyIndex,
    int StrategyCount,
    Dictionary<string, ZapretTestGroupSummary> Groups,
    int HttpPassed,
    int HttpTotal,
    int PingPassed,
    int PingTotal,
    string Message,
    string? RecommendedPath = null,
    bool Completed = false,
    int CompletedProbeCount = 0,
    int TotalProbeCount = 0,
    IReadOnlyList<ZapretTestStrategyResult>? StrategyResults = null,
    string? YouTubeSpeedRequestId = null,
    double? YouTubeSpeedMbps = null,
    string? YouTubeSpeedError = null);

internal sealed record ZapretTestGroupSummary(int HttpPassed, int HttpTotal, int PingPassed, int PingTotal);
internal sealed record ZapretTestStrategyResult(
    string StrategyPath,
    string Phase,
    int HttpPassed,
    int HttpTotal,
    int PingPassed,
    int PingTotal,
    double? YouTubeSpeedMbps = null,
    string? YouTubeSpeedError = null);

internal sealed record YouTubeSpeedTestMeasurement(
    double? Mbps,
    long Bytes,
    long DurationMilliseconds,
    string? Error)
{
    internal const double MinimumRecommended720pMbps = 2.5;

    internal static YouTubeSpeedTestMeasurement FromTransfer(long bytes, long durationMilliseconds)
    {
        if (bytes <= 0 || durationMilliseconds <= 0)
            return new YouTubeSpeedTestMeasurement(0, Math.Max(0, bytes), Math.Max(0, durationMilliseconds), "YouTube не передал видеоданные.");

        var mbps = bytes * 8d / durationMilliseconds / 1000d;
        return new YouTubeSpeedTestMeasurement(mbps, bytes, durationMilliseconds, null);
    }

    internal static YouTubeSpeedTestMeasurement Failed(string error) => new(0, 0, 0, error);
    internal static YouTubeSpeedTestMeasurement Unsupported(string error) => new(null, 0, 0, error);
}

internal sealed record YouTubeSpeedTestReply(string RequestId, YouTubeSpeedTestMeasurement Measurement);
