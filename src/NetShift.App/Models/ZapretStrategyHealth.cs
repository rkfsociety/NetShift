namespace NetShift.App.Models;

internal enum ZapretStrategyHealthStatus
{
    Green,
    Yellow,
    Red,
    Unknown
}

internal static class ZapretStrategyHealthRules
{
    public static ZapretStrategyHealthStatus FromResults(int httpPassed, int httpTotal, int pingPassed, int pingTotal)
    {
        if (httpTotal + pingTotal > 0 && httpPassed == httpTotal && pingPassed == pingTotal)
            return ZapretStrategyHealthStatus.Green;

        return httpPassed + pingPassed > 0
            ? ZapretStrategyHealthStatus.Yellow
            : ZapretStrategyHealthStatus.Red;
    }

    public static ZapretStrategyHealthStatus FromStoredStatus(string? status, bool? legacyHealthy)
    {
        if (Enum.TryParse<ZapretStrategyHealthStatus>(status, ignoreCase: true, out var parsed) &&
            parsed != ZapretStrategyHealthStatus.Unknown)
            return parsed;

        return legacyHealthy switch
        {
            true => ZapretStrategyHealthStatus.Green,
            false => ZapretStrategyHealthStatus.Yellow,
            null => ZapretStrategyHealthStatus.Unknown
        };
    }

    public static int SortOrder(ZapretStrategyHealthStatus status) => status switch
    {
        ZapretStrategyHealthStatus.Green => 0,
        ZapretStrategyHealthStatus.Yellow => 1,
        ZapretStrategyHealthStatus.Red => 2,
        _ => 3
    };
}
