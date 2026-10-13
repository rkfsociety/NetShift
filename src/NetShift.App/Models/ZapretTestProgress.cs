namespace NetShift.App.Models;

internal sealed record ZapretTestProgress(
    string? StrategyPath,
    string Phase,
    int StrategyIndex,
    int StrategyCount,
    Dictionary<string, bool?> Targets,
    string Message,
    string? RecommendedPath = null,
    bool Completed = false);
