using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Anka.LoadTest;

/// <summary>
/// Runs <c>wrk</c> as a subprocess for a single scenario / concurrency combination
/// and parses the summary output into a <see cref="WrkResult"/>.
/// </summary>
internal static partial class WrkRunner
{
    private static readonly Regex RpsPattern =
        RequestRegex();

    private static readonly Regex TransferPattern =
        TransferRegex();

    private static readonly Regex LatencyAvgPattern =
        LatencyRegex();

    // wrk --latency prints a percentile table like:
    //   50.000% 123.45us
    //   75.000% 456.78us
    //   90.000% 789.01us
    //   99.000% 1.23ms
    private static readonly Regex LatencyPercentilePattern =
        UsMsSRegex();

    private static readonly Regex ErrorPattern =
        Non2XOr3XResponsesRegex();

    private static readonly Regex RequestsTotalPattern =
        RequestInRegex();


    /// <summary>
    /// Runs wrk against <paramref name="url"/> with the specified parameters.
    /// </summary>
    /// <param name="url">Full URL to benchmark (e.g. http://127.0.0.1:8080/plain).</param>
    /// <param name="connections">Number of concurrent connections.</param>
    /// <param name="durationSeconds">How many seconds to run the test.</param>
    /// <param name="threads">Number of wrk threads (default 4).</param>
    /// <param name="luaScript">Optional path to a wrk Lua script (for POST etc.).</param>
    public static async Task<WrkResult> RunAsync(
        string url,
        int    connections,
        int    durationSeconds = 10,
        int    threads         = 4,
        string? luaScript      = null)
    {
        var args = BuildArgs(url, connections, durationSeconds, threads, luaScript);

        using var proc = new Process();

        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = "wrk",
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var full = stdout + "\n" + stderr;
        return Parse(full, connections, url);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildArgs(string url, int connections, int duration, int threads, string? lua)
    {
        // wrk auto-caps threads at connection count
        threads = Math.Min(threads, connections);
        threads = Math.Max(threads, 1);

        var sb = new System.Text.StringBuilder();
        sb.Append($"-t{threads} -c{connections} -d{duration}s --latency");
        if (lua is not null)
        {
            sb.Append($" -s \"{lua}\"");
        }
        sb.Append($" {url}");
        return sb.ToString();
    }

    private static WrkResult Parse(string output, int connections, string url)
    {
        var rps          = ParseDouble(RpsPattern, output, 1);
        var totalReqs    = ParseLong(RequestsTotalPattern, output, 1);
        var nonOk        = ParseLong(ErrorPattern, output, 1);
        var latencyAvgMs = ParseLatencyMs(LatencyAvgPattern, output);
        var transferMbs  = ParseTransferMbs(TransferPattern, output);
        var p99          = ParsePercentileMs(LatencyPercentilePattern, output, 99.0);
        var p999         = ParsePercentileMs(LatencyPercentilePattern, output, 99.9);
        var p90          = ParsePercentileMs(LatencyPercentilePattern, output, 90.0);
        var p50          = ParsePercentileMs(LatencyPercentilePattern, output, 50.0);

        return new WrkResult(
            Url:           url,
            Connections:   connections,
            RequestsPerSec: rps,
            TotalRequests: totalReqs,
            NonOkResponses: nonOk,
            LatencyAvgMs:  latencyAvgMs,
            P50Ms:         p50,
            P90Ms:         p90,
            P99Ms:         p99,
            P999Ms:        p999,
            TransferMbSec: transferMbs,
            RawOutput:     output);
    }

    private static double ParseDouble(Regex re, string text, int group)
    {
        var m = re.Match(text);
        return m.Success && double.TryParse(m.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static long ParseLong(Regex re, string text, int group)
    {
        var m = re.Match(text);
        return m.Success && long.TryParse(m.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static double ParseLatencyMs(Regex re, string text)
    {
        var m = re.Match(text);
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)) return 0;
        return ToMs(val, m.Groups[2].Value);
    }

    private static double ParseTransferMbs(Regex re, string text)
    {
        var m = re.Match(text);
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)) return 0;
        return m.Groups[2].Value switch
        {
            "GB" => val * 1024,
            "MB" => val,
            "KB" => val / 1024.0,
            "B"  => val / (1024.0 * 1024),
            _    => val,
        };
    }

    private static double ParsePercentileMs(Regex re, string text, double targetPct)
    {
        // Find the line whose percentile is closest to targetPct
        var best     = double.MaxValue;
        var bestMs   = 0.0;
        foreach (Match m in re.Matches(text))
        {
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) continue;
            var diff = Math.Abs(pct - targetPct);

            if (!(diff < best))
            {
                continue;
            }

            best = diff;
            if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            {
                bestMs = ToMs(val, m.Groups[3].Value);
            }
        }
        return bestMs;
    }

    private static double ToMs(double val, string unit) => unit switch
    {
        "us" => val / 1000.0,
        "ms" => val,
        "s"  => val * 1000.0,
        _    => val,
    };
    
    [GeneratedRegex(@"Transfer/sec:\s+([\d.]+)(KB|MB|GB|B)", RegexOptions.Compiled)]
    private static partial Regex TransferRegex();
    
    
    [GeneratedRegex(@"Requests/sec:\s+([\d.]+)", RegexOptions.Compiled)]
    private static partial Regex RequestRegex();
    
    
    [GeneratedRegex(@"Latency\s+([\d.]+)(us|ms|s)\s+([\d.]+)(us|ms|s)", RegexOptions.Compiled)]
    private static partial Regex LatencyRegex();
    
    
    [GeneratedRegex(@"^\s*([\d.]+)%\s+([\d.]+)(us|ms|s)\b", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex UsMsSRegex();
    
    
    [GeneratedRegex(@"Non-2xx or 3xx responses:\s+(\d+)", RegexOptions.Compiled)]
    private static partial Regex Non2XOr3XResponsesRegex();
    
    
    [GeneratedRegex(@"(\d+) requests in", RegexOptions.Compiled)]
    private static partial Regex RequestInRegex();
}
