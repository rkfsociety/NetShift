using System.Diagnostics;
using System.IO;
using System.ComponentModel;

namespace NetShift.App.Services;

internal sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessExecutionService
{
    public static async Task<ProcessOutput> CaptureAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Не удалось запустить {Path.GetFileName(executable)}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(executable)} не завершился за {timeout.TotalSeconds:0} секунд.");
        }

        await Task.WhenAll(stdout, stderr);
        return new ProcessOutput(process.ExitCode, stdout.Result, stderr.Result);
    }
}
