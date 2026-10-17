using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using NetShift.App.Models;

namespace NetShift.App.Services;

internal static class ZapretConnectivityTester
{
    private const int CurlTimeoutSeconds = 4;
    private const int PingTimeoutMilliseconds = 1000;
    private const int ProcessTimeoutSeconds = 5;
    private static readonly Regex TargetLineRegex = new("^\\s*(?<name>\\w+)\\s*=\\s*\"(?<value>.+)\"\\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex UnsupportedOutputRegex = new(
        @"does not support|not supported|protocol\s+'?.+'?\s+not\s+supported|unsupported protocol|TLS.*not supported|Unrecognized option|Unknown option|unsupported option|unsupported feature|schannel",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string Label, string[] Arguments)[] Modes =
    [
        ("HTTP", ["--http1.1"]),
        ("TLS1.2", ["--tlsv1.2", "--tls-max", "1.2"]),
        ("TLS1.3", ["--tlsv1.3", "--tls-max", "1.3"])
    ];

    private static readonly (string Name, string Value)[] DefaultTargets =
    [
        ("Discord Main", "https://discord.com"),
        ("Discord Gateway", "https://gateway.discord.gg"),
        ("Discord CDN", "https://cdn.discordapp.com"),
        ("Discord Updates", "https://updates.discord.com"),
        ("YouTube Web", "https://www.youtube.com"),
        ("YouTube Short", "https://youtu.be"),
        ("YouTube Image", "https://i.ytimg.com"),
        ("YouTube Video Redirect", "https://redirector.googlevideo.com"),
        ("Google Main", "https://www.google.com"),
        ("Google Gstatic", "https://www.gstatic.com"),
        ("Cloudflare Web", "https://www.cloudflare.com"),
        ("Cloudflare CDN", "https://cdnjs.cloudflare.com"),
        ("Cloudflare DNS 1.1.1.1", "PING:1.1.1.1"),
        ("Cloudflare DNS 1.0.0.1", "PING:1.0.0.1"),
        ("Google DNS 8.8.8.8", "PING:8.8.8.8"),
        ("Google DNS 8.8.4.4", "PING:8.8.4.4"),
        ("Quad9 DNS 9.9.9.9", "PING:9.9.9.9")
    ];

    internal static IReadOnlyList<ZapretConnectivityTarget> LoadTargets(string folder)
    {
        var targetFile = Path.Combine(folder, "utils", "targets.txt");
        var contents = File.Exists(targetFile) ? File.ReadAllText(targetFile) : string.Empty;
        return ParseTargets(contents);
    }

    internal static void EnsureCurlAvailable()
    {
        var searchPaths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Environment.GetFolderPath(Environment.SpecialFolder.System));
        if (searchPaths.Any(path => File.Exists(Path.Combine(path, "curl.exe")))) return;
        throw new InvalidOperationException("Оригинальная проверка требует curl.exe, но он не найден в PATH.");
    }

    internal static IReadOnlyList<ZapretConnectivityTarget> ParseTargets(string? contents)
    {
        var rawTargets = new List<(string Name, string Value)>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in (contents ?? string.Empty).Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var match = TargetLineRegex.Match(line);
            if (!match.Success) continue;

            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            if (positions.TryGetValue(name, out var position)) rawTargets[position] = (name, value);
            else
            {
                positions.Add(name, rawTargets.Count);
                rawTargets.Add((name, value));
            }
        }

        var parsed = rawTargets.Select(target => ParseTarget(target.Name, target.Value))
            .Where(target => target is not null).Cast<ZapretConnectivityTarget>().ToArray();
        if (parsed.Length > 0) return parsed;
        return DefaultTargets.Select(target => ParseTarget(target.Name, target.Value)!).ToArray();
    }

    internal static string[] BuildCurlArguments(string url, string mode)
    {
        var test = Modes.FirstOrDefault(candidate => candidate.Label.Equals(mode, StringComparison.OrdinalIgnoreCase));
        if (test.Label is null) throw new ArgumentOutOfRangeException(nameof(mode), mode, "Неизвестный режим curl.");

        return [
            "-I", "-s", "-m", CurlTimeoutSeconds.ToString(),
            "--connect-timeout", Math.Min(2, CurlTimeoutSeconds).ToString(),
            "-o", "NUL", "-w", "%{http_code}", "--show-error",
            .. test.Arguments,
            url
        ];
    }

    internal static int CountProbeOperations(IReadOnlyList<ZapretConnectivityTarget> targets) =>
        targets.Sum(target => (target.Url is null ? 0 : Modes.Length) + (target.PingTarget is null ? 0 : 1));

    internal static async Task<ZapretConnectivitySummary> CheckTargetsAsync(
        IReadOnlyList<ZapretConnectivityTarget> targets,
        CancellationToken cancellationToken = default,
        Action? probeCompleted = null)
    {
        var maxParallel = Math.Min(16, Math.Max(8, Environment.ProcessorCount * 2));
        using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var tasks = targets.Select(async target =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await CheckTargetAsync(target, cancellationToken, probeCompleted).ConfigureAwait(false); }
            finally { semaphore.Release(); }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Summarize(results);
    }

    internal static ZapretConnectivitySummary Summarize(IReadOnlyList<ZapretConnectivityTargetResult> results)
    {
        var groups = results.GroupBy(result => result.Target.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new ZapretTestGroupSummary(
                    group.Sum(result => result.HttpPassed),
                    group.Sum(result => result.HttpTotal),
                    group.Count(result => result.Target.PingTarget is not null && result.PingSucceeded),
                    group.Count(result => result.Target.PingTarget is not null)),
                StringComparer.OrdinalIgnoreCase);

        return new ZapretConnectivitySummary(
            results.Sum(result => result.HttpPassed),
            results.Sum(result => result.HttpTotal),
            results.Count(result => result.Target.PingTarget is not null && result.PingSucceeded),
            results.Count(result => result.Target.PingTarget is not null),
            groups,
            results);
    }

    internal static int CompareScores(
        ZapretConnectivitySummary left,
        ZapretConnectivitySummary right,
        double? leftYouTubeMbps = null,
        double? rightYouTubeMbps = null)
    {
        if (leftYouTubeMbps.HasValue || rightYouTubeMbps.HasValue)
        {
            var measuredComparison = leftYouTubeMbps.HasValue.CompareTo(rightYouTubeMbps.HasValue);
            if (measuredComparison != 0) return measuredComparison;

            var speedComparison = Nullable.Compare(leftYouTubeMbps, rightYouTubeMbps);
            if (speedComparison != 0) return speedComparison;
        }

        var httpComparison = left.HttpPassed.CompareTo(right.HttpPassed);
        return httpComparison != 0 ? httpComparison : left.PingPassed.CompareTo(right.PingPassed);
    }

    internal static bool ShouldRetryFullSweep(
        ZapretConnectivitySummary? summary,
        YouTubeSpeedTestMeasurement? youtubeSpeed = null) =>
        summary is null ||
        summary.HttpTotal + summary.PingTotal == 0 ||
        summary.HttpPassed != summary.HttpTotal ||
        summary.PingPassed != summary.PingTotal ||
        youtubeSpeed?.Mbps is double mbps && mbps < YouTubeSpeedTestMeasurement.MinimumRecommended720pMbps;

    private static ZapretConnectivityTarget? ParseTarget(string name, string value)
    {
        if (value.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
        {
            var host = value[5..].Trim();
            if (host.Length == 0 || host.Any(char.IsWhiteSpace)) return null;
            return new ZapretConnectivityTarget(name, value, null, host, GetGroup(name, isPingOnly: true));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            return null;

        return new ZapretConnectivityTarget(name, value, value, uri.Authority, GetGroup(name, isPingOnly: false));
    }

    private static string GetGroup(string name, bool isPingOnly)
    {
        if (name.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (name.StartsWith("YouTube", StringComparison.OrdinalIgnoreCase)) return "YouTube";
        if (name.StartsWith("Google", StringComparison.OrdinalIgnoreCase)) return "Google";
        if (name.StartsWith("Cloudflare", StringComparison.OrdinalIgnoreCase)) return "Cloudflare";
        return isPingOnly ? "DNS" : "Other";
    }

    private static async Task<ZapretConnectivityTargetResult> CheckTargetAsync(
        ZapretConnectivityTarget target,
        CancellationToken cancellationToken,
        Action? probeCompleted)
    {
        var probes = new List<ZapretCurlProbeResult>(Modes.Length);
        if (target.Url is not null)
        {
            foreach (var mode in Modes)
            {
                probes.Add(await ProbeUrlAsync(target.Url, mode.Label, cancellationToken).ConfigureAwait(false));
                probeCompleted?.Invoke();
            }
        }

        var ping = new ZapretPingResult(false, null);
        if (target.PingTarget is not null)
        {
            ping = await ProbePingAsync(target.PingTarget, cancellationToken).ConfigureAwait(false);
            probeCompleted?.Invoke();
        }
        return new ZapretConnectivityTargetResult(target, probes, ping.Succeeded, ping.RoundtripMilliseconds);
    }

    private static async Task<ZapretCurlProbeResult> ProbeUrlAsync(string url, string mode, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("curl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildCurlArguments(url, mode)) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить curl.exe.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(ProcessTimeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                try { await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false); } catch { }
                return new ZapretCurlProbeResult(mode, ZapretCurlProbeStatus.Error, null, "Таймаут curl.");
            }

            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            int? httpCode = int.TryParse(output, out var parsedCode) ? parsedCode : null;
            var status = process.ExitCode == 0
                ? ZapretCurlProbeStatus.Success
                : process.ExitCode == 35 || UnsupportedOutputRegex.IsMatch(error)
                    ? ZapretCurlProbeStatus.Unsupported
                    : ZapretCurlProbeStatus.Error;
            return new ZapretCurlProbeResult(mode, status, httpCode, error.Length == 0 ? null : error);
        }
        catch (Exception error)
        {
            return new ZapretCurlProbeResult(mode, ZapretCurlProbeStatus.Error, null, error.Message);
        }
    }

    private static async Task<ZapretPingResult> ProbePingAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, PingTimeoutMilliseconds).WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new ZapretPingResult(true, reply.RoundtripTime)
                : new ZapretPingResult(false, null);
        }
        catch
        {
            return new ZapretPingResult(false, null);
        }
    }
}

internal sealed record ZapretConnectivityTarget(string Name, string Value, string? Url, string? PingTarget, string Group);

internal enum ZapretCurlProbeStatus
{
    Success,
    Error,
    Unsupported
}

internal sealed record ZapretCurlProbeResult(string Mode, ZapretCurlProbeStatus Status, int? HttpCode, string? Error);
internal sealed record ZapretPingResult(bool Succeeded, long? RoundtripMilliseconds);
internal sealed record ZapretConnectivityTargetResult(
    ZapretConnectivityTarget Target,
    IReadOnlyList<ZapretCurlProbeResult> Probes,
    bool PingSucceeded,
    long? PingRoundtripMilliseconds)
{
    public int HttpPassed => Probes.Count(probe => probe.Status == ZapretCurlProbeStatus.Success);
    public int HttpTotal => Probes.Count;
}

internal sealed record ZapretConnectivitySummary(
    int HttpPassed,
    int HttpTotal,
    int PingPassed,
    int PingTotal,
    Dictionary<string, ZapretTestGroupSummary> Groups,
    IReadOnlyList<ZapretConnectivityTargetResult> Targets);
