using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Anka.LoadTest;

const int port = 18080;
const int durationSeconds = 10;
const int warmupSeconds = 2;
const int wrkThreads = 16;
var concurrencyLevels = new[] { 1, 10, 50, 100, 400 };
var dbConcurrencyLevels = new[] { 8, 16, 32, 64, 128, 256 };

var solutionRoot = FindSolutionRoot();
var luaScript = Path.Combine(AppContext.BaseDirectory, "scripts", "post_echo.lua");
var rid = GetRid();

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("=== Anka Load Test (TFB-Style) ===");
Console.ResetColor();

var targets = await PublishTargetsAsync();
var benchmarkRuns = new List<ServerBenchmarkResult>(targets.Count);

foreach (var target in targets)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n── {target.Name} ({target.RuntimeDescription}) ──");
    Console.ResetColor();

    benchmarkRuns.Add(await RunTargetBenchmarks(target, port));
}

ReportWriter.Write(solutionRoot, benchmarkRuns, durationSeconds);

return 0;

#region local functions

async Task<IReadOnlyList<LaunchTarget>> PublishTargetsAsync()
{


    var ankaCsproj = Path.Combine(solutionRoot, "Test", "LoadTest", "Anka.HttpConsole", "Anka.HttpConsole.csproj");
    var ankaExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Anka.HttpConsole.exe" : "Anka.HttpConsole";
    var ankaPath = Path.Combine(solutionRoot, "Test", "LoadTest", "Anka.HttpConsole", "bin", "Release", "net8.0", rid, "publish", ankaExe);

    Console.Write($"Publishing Anka.HttpConsole (AOT, {rid})... ");
    var ankaPublish = await RunProcessAsync("dotnet", $"publish \"{ankaCsproj}\" -c Release -r {rid} /nologo");

    if (ankaPublish.ExitCode != 0)
    {
        PrintError("FAILED\n" + ankaPublish.Output);
        throw new InvalidOperationException("Anka.HttpConsole publish failed.");
    }

    Console.WriteLine("OK");
    
    var launchTargets = new List<LaunchTarget>
    {
        new("Anka", $"Native AOT ({rid})", ankaPath, port.ToString(CultureInfo.InvariantCulture))
    };

    var kestrelCsproj = Path.Combine(solutionRoot, "Test", "LoadTest", "Kestrel.HttpConsole", "Kestrel.HttpConsole.csproj");

    var kestrelDll = Path.Combine(solutionRoot, "Test", "LoadTest", "Kestrel.HttpConsole", "bin", "Release", "net8.0", "publish", "Kestrel.HttpConsole.dll");

    Console.Write("Publishing Kestrel.HttpConsole (Release)... ");
    var kestrelPublish = await RunProcessAsync("dotnet", $"publish \"{kestrelCsproj}\" -c Release /nologo");

    if (kestrelPublish.ExitCode != 0)
    {
        PrintError("FAILED\n" + kestrelPublish.Output);
        throw new InvalidOperationException("Kestrel.HttpConsole publish failed.");
    }

    Console.WriteLine("OK");
    launchTargets.Add(new LaunchTarget("Kestrel", "ASP.NET Core / JIT", "dotnet", $"\"{kestrelDll}\" {port.ToString(CultureInfo.InvariantCulture)}"));

    return launchTargets;
}

async Task<ServerBenchmarkResult> RunTargetBenchmarks(LaunchTarget target, int serverPort)
{
    var baseUrl = $"http://127.0.0.1:{serverPort}";

    var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ??
                "Host=localhost;Database=hello_world;Username=benchmarkdbuser;Password=benchmarkdbpass";

    var outputBuffer = new StringBuilder();
    var listenTcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
    var startupMetricsTcs = new TaskCompletionSource<(double ReadyMs, long AllocatedBytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
    var startupStopwatch = Stopwatch.StartNew();

    var serverProc = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = target.Command,
            Arguments = target.Arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        },
    };
    serverProc.StartInfo.Environment["DATABASE_URL"] = dbUrl;
    serverProc.OutputDataReceived += (_, e) => HandleServerOutputLine(e.Data, outputBuffer, listenTcs, startupMetricsTcs, startupStopwatch);
    serverProc.ErrorDataReceived += (_, e) => HandleServerOutputLine(e.Data, outputBuffer, listenTcs, startupMetricsTcs, startupStopwatch);
    serverProc.Start();
    serverProc.BeginOutputReadLine();
    serverProc.BeginErrorReadLine();

    Console.Write($"Waiting for {target.Name} on port {serverPort}... ");

    while (!listenTcs.Task.IsCompleted)
    {
        if (serverProc.HasExited)
        {
            PrintError($"FAILED\n{outputBuffer}");
            throw new InvalidOperationException($"{target.Name} exited before it started listening.");
        }

        if (startupStopwatch.Elapsed > TimeSpan.FromSeconds(30))
        {
            try
            {
                serverProc.Kill(entireProcessTree: true);
            }
            catch
            {
                /* ignore */
            }

            PrintError($"TIMEOUT\n{outputBuffer}");
            throw new InvalidOperationException($"{target.Name} did not start listening within the timeout.");
        }

        await Task.Delay(50);
    }

    var timeToListenMs = await listenTcs.Task;
    var firstResponseMs = await MeasureFirstResponseAsync(baseUrl, serverProc, outputBuffer);
    serverProc.Refresh();

    var startupMetrics = await TryGetStartupMetricsAsync(startupMetricsTcs.Task);

    var startup = new StartupMetrics(TimeToListenMs: timeToListenMs, TimeToReadyMs: startupMetrics?.ReadyMs ?? timeToListenMs,
                                     FirstResponseMs: firstResponseMs, StartupAllocatedBytes: startupMetrics?.AllocatedBytes ?? 0,
                                     WorkingSetMb: serverProc.WorkingSet64 / (1024.0 * 1024.0));

    Console.WriteLine("OK");
    PrintStartupMetrics(startup);

    Console.Write($"Warming up ({warmupSeconds}s)... ");
    await WrkRunner.RunAsync($"{baseUrl}/plain", connections: 10, durationSeconds: warmupSeconds, threads: 4);
    Console.WriteLine("done");
    Console.WriteLine();

    var frameworkData = await RunScenarioSet(baseUrl, Scenarios.Framework, concurrencyLevels);
    var dbData = await RunScenarioSet(baseUrl, Scenarios.Database, dbConcurrencyLevels);

    try
    {
        serverProc.Kill(entireProcessTree: true);
    }
    catch
    {
        /* already stopped */
    }

    return new ServerBenchmarkResult(target, startup, frameworkData, dbData);
}

async Task<IReadOnlyList<(Scenario, IReadOnlyList<WrkResult>)>> RunScenarioSet(string baseUrl, IReadOnlyList<Scenario> scenarios, int[] conLevels)
{
    var data = new List<(Scenario, IReadOnlyList<WrkResult>)>();

    foreach (var scenario in scenarios)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── {scenario.Name} ──");
        Console.ResetColor();

        var results = new List<WrkResult>();

        foreach (var con in conLevels)
        {
            Console.Write($"  c={con,4}  ");

            var lua = scenario.Path == "/echo" ? luaScript : scenario.LuaScript;

            var r = await WrkRunner.RunAsync($"{baseUrl}{scenario.Path}", connections: con, durationSeconds: durationSeconds, threads: wrkThreads, luaScript: lua);

            results.Add(r);
            
            PrintRow(r);
        }

        data.Add((scenario, results));
        Console.WriteLine();
    }

    return data;
}

static void PrintError(string msg)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(msg);
    Console.ResetColor();
}

static void PrintRow(WrkResult r)
{
    var rpsStr = r.RequestsPerSec >= 1_000_000 ? $"{r.RequestsPerSec / 1_000_000:F2}M req/s" :
                 r.RequestsPerSec >= 1_000     ? $"{r.RequestsPerSec / 1_000:F1}k req/s" : $"{r.RequestsPerSec:F0} req/s";

    var errColor = r.ErrorPct > 0 ? ConsoleColor.Red : ConsoleColor.DarkCyan;

    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.Write($"{rpsStr,20}  avg {r.LatencyAvgMs:F2} ms  p99 {r.P99Ms:F2} ms  p99.9 {r.P999Ms:F2} ms  ");
    Console.ForegroundColor = errColor;
    Console.Write($"{r.TransferMbSec:F2} MB/s  err {r.ErrorPct:F1}%");
    Console.ResetColor();
    Console.WriteLine();
}

static void PrintStartupMetrics(StartupMetrics startup)
{
    Console.ForegroundColor = ConsoleColor.DarkCyan;

    Console.WriteLine(
        $"  startup listen {startup.TimeToListenMs:F2} ms  ready {startup.TimeToReadyMs:F2} ms  first {startup.FirstResponseMs:F2} ms  alloc {FormatBytes(startup.StartupAllocatedBytes)}  rss {startup.WorkingSetMb:F2} MB");
    Console.ResetColor();
}

static void HandleServerOutputLine(string? line, StringBuilder outputBuffer, TaskCompletionSource<double> listenTcs,
                                   TaskCompletionSource<(double ReadyMs, long AllocatedBytes)> startupMetricsTcs, Stopwatch startupStopwatch)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        return;
    }

    lock (outputBuffer)
    {
        // Cap at 1 MB to prevent runaway server logging from causing an OOM in the
        // orchestrator. A server that emits per-request logs at high concurrency can
        // produce hundreds of MB; we only need the first few lines for diagnostics.
        if (outputBuffer.Length < 1024 * 1024)
        {
            outputBuffer.AppendLine(line);
        }
    }

    if (line.StartsWith("Listening on ", StringComparison.OrdinalIgnoreCase))
    {
        listenTcs.TrySetResult(startupStopwatch.Elapsed.TotalMilliseconds);
    }

    if (TryParseStartupMetrics(line, out var readyMs, out var allocatedBytes))
    {
        startupMetricsTcs.TrySetResult((readyMs, allocatedBytes));
    }
}

static bool TryParseStartupMetrics(string line, out double readyMs, out long allocatedBytes)
{
    readyMs = 0;
    allocatedBytes = 0;

    if (!line.StartsWith("[startup-metrics] ", StringComparison.Ordinal))
    {
        return false;
    }

    var parts = line["[startup-metrics] ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (parts.Length < 2)
    {
        return false;
    }

    foreach (var part in parts)
    {
        var separatorIndex = part.IndexOf('=');

        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = part[..separatorIndex];
        var value = part[(separatorIndex + 1)..];

        switch (key)
        {
            case "ready_ms":
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out readyMs);
                break;
            case "allocated_bytes":
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out allocatedBytes);
                break;
        }
    }

    return readyMs > 0 || allocatedBytes > 0;
}

static async Task<double> MeasureFirstResponseAsync(string baseUrl, Process serverProc, StringBuilder outputBuffer)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };

    for (var attempt = 0; attempt < 20; attempt++)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/health");
            sw.Stop();

            if (resp.IsSuccessStatusCode)
            {
                return sw.Elapsed.TotalMilliseconds;
            }
        }
        catch when (!serverProc.HasExited)
        {
            // The server may have started listening just before the app pipeline became ready.
        }

        if (serverProc.HasExited)
        {
            PrintError($"FAILED\n{outputBuffer}");
            throw new InvalidOperationException("Server exited before responding to the first health request.");
        }

        await Task.Delay(50);
    }

    PrintError($"TIMEOUT\n{outputBuffer}");
    throw new InvalidOperationException("Timed out waiting for the first health response.");
}

static async Task<(double ReadyMs, long AllocatedBytes)?> TryGetStartupMetricsAsync(Task<(double ReadyMs, long AllocatedBytes)> startupMetricsTask)
{
    var completed = await Task.WhenAny(startupMetricsTask, Task.Delay(1000));
    return completed == startupMetricsTask ? await startupMetricsTask : null;
}

static string FormatBytes(long bytes)
{
    return bytes switch
    {
        <= 0          => "—",
        < 1024        => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _             => $"{bytes / (1024.0 * 1024.0):F2} MB"
    };
}

static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string args)
{
    using var p = new Process();

    p.StartInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    p.Start();

    var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();

    await p.WaitForExitAsync();

    return (p.ExitCode, output);
}

static string FindSolutionRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);

    while (dir is not null)
    {
        if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static string GetRid()
{
    var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";

    var arch = RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64   => "x64",
        Architecture.X86   => "x86",
        _                  => "x64",
    };
    return $"{os}-{arch}";
}

#endregion
