using System.Diagnostics;
using System.Runtime.InteropServices;
using Anka.LoadTest;

const int    port             = 18080;
const int    kestrelPort      = 18081;
const int    durationSeconds  = 10;
const int    warmupSeconds    = 2;
const int    wrkThreads       = 16;   // was 4 — use more threads so wrk isn't the ceiling
var          concurrencyLevels = new[] { 1, 10, 50, 100, 400 };

var solutionRoot = FindSolutionRoot();
var luaScript    = Path.Combine(AppContext.BaseDirectory, "scripts", "post_echo.lua");
var rid          = GetRid();

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("=== Anka vs Kestrel Load Test ===");
Console.ResetColor();

// ── Anka (Native AOT) ────────────────────────────────────────────────────────

var ankaCsproj = Path.Combine(solutionRoot, "Anka.HttpConsole", "Anka.HttpConsole.csproj");
var ankaExe    = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Anka.HttpConsole.exe" : "Anka.HttpConsole";
var ankaPath   = Path.Combine(solutionRoot, "Anka.HttpConsole", "bin", "Release", "net8.0", rid, "publish", ankaExe);

Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine("\n── Anka (Native AOT) ──");
Console.ResetColor();

Console.Write($"Publishing Anka.HttpConsole (AOT, {rid})... ");
var ankaPublish = await RunProcessAsync("dotnet", $"publish \"{ankaCsproj}\" -c Release -r {rid} /nologo");
if (ankaPublish.ExitCode != 0)
{
    PrintError("FAILED\n" + ankaPublish.Output);
    return 1;
}
Console.WriteLine("OK");

var ankaData = await RunServerBenchmarks(ankaPath, port, "Anka");

// ── Kestrel (JIT self-contained) ─────────────────────────────────────────────

var kestrelCsproj = Path.Combine(solutionRoot, "Kestrel.HttpConsole", "Kestrel.HttpConsole.csproj");
var kestrelExe    = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Kestrel.HttpConsole.exe" : "Kestrel.HttpConsole";
var kestrelPath   = Path.Combine(solutionRoot, "Kestrel.HttpConsole", "bin", "Release", "net8.0", rid, "publish", kestrelExe);

Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine("\n── Kestrel (JIT self-contained) ──");
Console.ResetColor();

Console.Write($"Publishing Kestrel.HttpConsole ({rid})... ");
var kestrelPublish = await RunProcessAsync("dotnet",
    $"publish \"{kestrelCsproj}\" -c Release -r {rid} -p:PublishSingleFile=true --self-contained /nologo");
if (kestrelPublish.ExitCode != 0)
{
    PrintError("FAILED\n" + kestrelPublish.Output);
    return 1;
}
Console.WriteLine("OK");

var kestrelData = await RunServerBenchmarks(kestrelPath, kestrelPort, "Kestrel");

// ── Report ───────────────────────────────────────────────────────────────────

ReportWriter.Write(solutionRoot, ankaData, kestrelData, concurrencyLevels, durationSeconds);
return 0;


// ── Local functions ───────────────────────────────────────────────────────────

async Task<IReadOnlyList<(Scenario, IReadOnlyList<WrkResult>)>> RunServerBenchmarks(
    string binaryPath, int serverPort, string label)
{
    var baseUrl = $"http://127.0.0.1:{serverPort}";

    var serverProc = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName               = binaryPath,
            Arguments              = serverPort.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        },
    };
    serverProc.Start();

    Console.Write($"Waiting for {label} on port {serverPort}... ");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    var ready = false;
    for (var attempt = 0; attempt < 30; attempt++)
    {
        await Task.Delay(300);
        try
        {
            var resp = await http.GetAsync($"{baseUrl}/health");
            if (resp.IsSuccessStatusCode) { ready = true; break; }
        }
        catch { /* not up yet */ }
    }

    if (!ready)
    {
        PrintError($"TIMEOUT — {label} did not start.");
        serverProc.Kill(entireProcessTree: true);
        throw new InvalidOperationException($"{label} failed to start.");
    }
    Console.WriteLine("OK");

    Console.Write($"Warming up ({warmupSeconds}s)... ");
    await WrkRunner.RunAsync($"{baseUrl}/plain", connections: 10, durationSeconds: warmupSeconds, threads: 4);
    Console.WriteLine("done");
    Console.WriteLine();

    var data = new List<(Scenario, IReadOnlyList<WrkResult>)>();

    foreach (var scenario in Scenarios.All)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── {scenario.Name} ──");
        Console.ResetColor();

        var results = new List<WrkResult>();

        foreach (var c in concurrencyLevels)
        {
            Console.Write($"  c={c,4}  ");

            var lua = scenario.Path == "/echo" ? luaScript : null;
            var r   = await WrkRunner.RunAsync(
                scenario.Url(serverPort),
                connections:     c,
                durationSeconds: durationSeconds,
                threads:         wrkThreads,
                luaScript:       lua);

            results.Add(r);
            PrintRow(r);
        }

        data.Add((scenario, results));
        Console.WriteLine();
    }

    try { serverProc.Kill(entireProcessTree: true); } catch { /* already stopped */ }
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
    var rpsStr = r.RequestsPerSec >= 1_000_000 ? $"{r.RequestsPerSec / 1_000_000:F2}M req/s"
               : r.RequestsPerSec >= 1_000     ? $"{r.RequestsPerSec / 1_000:F1}k req/s"
               :                                 $"{r.RequestsPerSec:F0} req/s";

    var errColor = r.ErrorPct > 0 ? ConsoleColor.Red : ConsoleColor.Green;

    Console.ForegroundColor = ConsoleColor.White;
    Console.Write($"{rpsStr,14}  avg {r.LatencyAvgMs:F2} ms  p99 {r.P99Ms:F2} ms  ");
    Console.ForegroundColor = errColor;
    Console.Write($"{r.TransferMbSec:F2} MB/s  err {r.ErrorPct:F1}%");
    Console.ResetColor();
    Console.WriteLine();
}

static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string args)
{
    using var p = new Process();

    p.StartInfo = new ProcessStartInfo
    {
        FileName               = fileName,
        Arguments              = args,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
    };

    p.Start();

    var output = await p.StandardOutput.ReadToEndAsync()
                 + await p.StandardError.ReadToEndAsync();

    await p.WaitForExitAsync();

    return (p.ExitCode, output);
}

static string FindSolutionRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
            return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static string GetRid()
{
    var os   = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
             : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "osx"
             :                                                         "linux";
    var arch = RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64   => "x64",
        Architecture.X86   => "x86",
        _                  => "x64",
    };
    return $"{os}-{arch}";
}
