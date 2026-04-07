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
    public static readonly IReadOnlyList<Scenario> All =
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
            "`POST /echo` with a 256 B JSON body. Tests request-body buffering and the two-`SendAsync` response path.",
            "/echo"),

        new("Large Response GET",
            "`GET /large` → ~2 KB `text/plain` response. Triggers the separate body `SendAsync` inside `HttpResponseWriter`.",
            "/large"),
    ];
}
