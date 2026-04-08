namespace Anka.LoadTest;

internal sealed record LaunchTarget(
    string Name,
    string RuntimeDescription,
    string Command,
    string Arguments);

internal sealed record StartupMetrics(
    double TimeToListenMs,
    double TimeToReadyMs,
    double FirstResponseMs,
    long   StartupAllocatedBytes,
    double WorkingSetMb);

internal sealed record ServerBenchmarkResult(
    LaunchTarget Target,
    StartupMetrics Startup,
    IReadOnlyList<(Scenario Scenario, IReadOnlyList<WrkResult> Results)> FrameworkData,
    IReadOnlyList<(Scenario Scenario, IReadOnlyList<WrkResult> Results)> DbData);
