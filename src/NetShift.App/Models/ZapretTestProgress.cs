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
    IReadOnlyList<ZapretTestStrategyResult>? StrategyResults = null);

internal sealed record ZapretTestGroupSummary(int HttpPassed, int HttpTotal, int PingPassed, int PingTotal);
internal sealed record ZapretTestStrategyResult(
    string StrategyPath,
    string Phase,
    int HttpPassed,
    int HttpTotal,
    int PingPassed,
    int PingTotal);
