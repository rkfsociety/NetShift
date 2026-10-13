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
    bool Completed = false);

internal sealed record ZapretTestGroupSummary(int HttpPassed, int HttpTotal, int PingPassed, int PingTotal);
