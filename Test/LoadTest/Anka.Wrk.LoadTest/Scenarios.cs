namespace Anka.LoadTest;

/// <summary>Describes one benchmark scenario.</summary>
internal sealed record Scenario(
    string Name,
    string Description,
    string Path,
    string? LuaScript = null)
{
    public string Url(int port) => $"http://127.0.0.1:{port}{Path}";
}

internal static class Scenarios
{
    /// <summary>Framework-level tests: no DB measures raw HTTP pipeline throughput.</summary>
    public static readonly IReadOnlyList<Scenario> Framework =
    [
        new("Plain Text GET",
            "Minimal `GET /plain` → 16 B `text/plain` response. Establishes the baseline throughput with the lowest possible overhead.",
            "/plain"),

        new("JSON API GET",
            "`GET /json` → ~90 B `application/json` response. Reflects a typical read-only REST API endpoint.",
            "/json"),

        new("GET with Multiple Headers",
            "`GET /headers` with 5 custom request headers. Exercises the header-parsing path on every request.",
            "/headers"),

        new("POST Echo",
            "`POST /echo` with a 256 B JSON body. Tests request-body read cost plus the response write path for echoed payloads.",
            "/echo"),

        new("Large Response GET",
            "`GET /large` → ~2 KB `text/plain` response. Exercises a larger response body write path than the tiny baseline payloads.",
            "/large"),
    ];

    /// <summary>TechEmpower-style database tests: exercises the full stack including PostgreSQL I/O.</summary>
    public static readonly IReadOnlyList<Scenario> Database =
    [
        new("TFB: Single DB Query",
            "`GET /db` — fetches one random row from the `world` table and returns it as JSON. Mirrors TechEmpower's *Single Database Query* test.",
            "/db"),

        new("TFB: Multiple DB Queries",
            "`GET /queries?queries=20` — fetches 20 random rows in separate queries and returns a JSON array. Mirrors TechEmpower's *Multiple Queries* test.",
            "/queries?queries=20"),

        new("TFB: Fortunes",
            "`GET /fortunes` — loads all Fortune rows, appends one static entry, sorts by message, and renders an HTML table. Mirrors TechEmpower's *Fortunes* test.",
            "/fortunes"),

        new("TFB: DB Updates",
            "`GET /updates?queries=20` — reads 20 random rows, assigns new random numbers, and updates them in the DB. Mirrors TechEmpower's *Updates* test.",
            "/updates?queries=20"),

        new("TFB: Cached Queries",
            "`GET /cached-queries?count=100` — returns 100 World objects served from an in-memory cache populated at startup. Mirrors TechEmpower's *Cached Queries* test.",
            "/cached-queries?count=100"),
    ];

    /// <summary>All scenarios combined (for backwards-compat use if needed).</summary>
    public static IReadOnlyList<Scenario> All => [.. Framework, .. Database];
}
