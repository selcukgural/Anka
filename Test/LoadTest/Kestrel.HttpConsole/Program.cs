using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

var port = args.Length > 0 && int.TryParse(args[0], out var parsedPort) ? parsedPort : 8080;
var url = $"http://127.0.0.1:{port}";
var startupStopwatch = Stopwatch.StartNew();
var startupAllocatedBefore = GC.GetTotalAllocatedBytes(true);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var plainBody = "Hello from Anka!"u8.ToArray();
var helloWorld = "Hello, World!"u8.ToArray();
var largeBody = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Anka is a minimal, zero-allocation HTTP/1.x server for .NET. ", 36)));
var okBody = "OK"u8.ToArray();

const string TextPlainUtf8 = "text/plain; charset=utf-8";
const string AppJsonUtf8 = "application/json; charset=utf-8";
const string TextHtmlUtf8 = "text/html; charset=utf-8";
const string TextPlain = "text/plain";

var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Database=hello_world;Username=benchmarkdbuser;Password=benchmarkdbpass";

var dataSourceBuilder = new NpgsqlSlimDataSourceBuilder(dbUrl);
await using var dataSource = dataSourceBuilder.Build();

var worldCache = new ConcurrentDictionary<int, int>();
var worldCacheWarmupTask = PopulateWorldCacheAsync();

var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Listen(IPAddress.Loopback, port);
});

// Suppress all console output: no appsettings.json means the default log level is
// Information, which emits two lines per request. At 75k req/s for 10 s that is
// ~1.5 M lines (~930 MB) pumped into the orchestrator's outputBuffer, causing a
// fatal OutOfMemoryException when StringBuilder tries a 1 GB contiguous LOH alloc.
builder.Logging.ClearProviders();

var app = builder.Build();
var rng = Random.Shared;

app.MapGet("/", ctx => WriteBytesAsync(ctx, plainBody, TextPlainUtf8));
app.MapGet("/plain", ctx => WriteBytesAsync(ctx, plainBody, TextPlainUtf8));
// TFB: Plaintext
app.MapGet("/plaintext", ctx => WriteBytesAsync(ctx, helloWorld, TextPlain));
// TFB: JSON Serialization — serialize per-request, not pre-cached
app.MapGet("/json", ctx =>
{
    var json = JsonSerializer.SerializeToUtf8Bytes(
        new TfbJsonMessage("Hello, World!"),
        KestrelAppJsonContext.Default.TfbJsonMessage);
    return WriteBytesAsync(ctx, json, AppJsonUtf8);
});
app.MapGet("/headers", ctx => WriteBytesAsync(ctx, plainBody, TextPlainUtf8));
app.MapGet("/large", ctx => WriteBytesAsync(ctx, largeBody, TextPlainUtf8));
app.MapGet("/health", ctx => WriteBytesAsync(ctx, okBody, TextPlain));

app.MapPost("/echo", async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status200OK;
    ctx.Response.ContentType = AppJsonUtf8;
    if (ctx.Request.ContentLength.HasValue)
    {
        ctx.Response.ContentLength = ctx.Request.ContentLength.Value;
    }

    await ctx.Request.Body.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

app.MapGet("/db", async ctx =>
{
    var id = rng.Next(1, 10001);
    await using var conn = await dataSource.OpenConnectionAsync(ctx.RequestAborted);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, randomnumber FROM world WHERE id = $1";
    cmd.Parameters.AddWithValue(id);
    await using var reader = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
    if (await reader.ReadAsync(ctx.RequestAborted))
    {
        await WriteBytesAsync(ctx, BuildWorldJson(reader.GetInt32(0), reader.GetInt32(1)), AppJsonUtf8);
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
});

app.MapGet("/queries", async ctx =>
{
    var count = ParseQueryCount(ctx.Request.Query, "queries", 1, 500, 1);
    var worlds = new (int Id, int RandomNumber)[count];
    await using var conn = await dataSource.OpenConnectionAsync(ctx.RequestAborted);
    for (var i = 0; i < count; i++)
    {
        var queryId = rng.Next(1, 10001);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, randomnumber FROM world WHERE id = $1";
        cmd.Parameters.AddWithValue(queryId);
        await using var reader = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
        if (await reader.ReadAsync(ctx.RequestAborted))
        {
            worlds[i] = (reader.GetInt32(0), reader.GetInt32(1));
        }
    }

    await WriteBytesAsync(ctx, BuildWorldArrayJson(worlds), AppJsonUtf8);
});

app.MapGet("/fortunes", async ctx =>
{
    var fortunes = new List<(int Id, string Message)>
    {
        (0, "Additional fortune added at request time.")
    };

    await using var conn = await dataSource.OpenConnectionAsync(ctx.RequestAborted);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, message FROM fortune";
    await using var reader = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
    while (await reader.ReadAsync(ctx.RequestAborted))
    {
        fortunes.Add((reader.GetInt32(0), reader.GetString(1)));
    }

    fortunes.Sort((a, b) => string.Compare(a.Message, b.Message, StringComparison.Ordinal));
    await WriteBytesAsync(ctx, BuildFortunesHtml(fortunes), TextHtmlUtf8);
});

app.MapGet("/updates", async ctx =>
{
    var count = ParseQueryCount(ctx.Request.Query, "queries", 1, 500, 1);
    var worlds = new (int Id, int RandomNumber)[count];

    await using var conn = await dataSource.OpenConnectionAsync(ctx.RequestAborted);
    for (var i = 0; i < count; i++)
    {
        var id = rng.Next(1, 10001);
        var newRandom = rng.Next(1, 10001);
        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT id, randomnumber FROM world WHERE id = $1";
        selectCmd.Parameters.AddWithValue(id);
        await using var reader = await selectCmd.ExecuteReaderAsync(ctx.RequestAborted);
        await reader.ReadAsync(ctx.RequestAborted);
        worlds[i] = (id, newRandom);
    }

    Array.Sort(worlds, (a, b) => a.Id.CompareTo(b.Id));
    for (var i = 0; i < count; i++)
    {
        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = "UPDATE world SET randomnumber = $1 WHERE id = $2";
        updateCmd.Parameters.AddWithValue(worlds[i].RandomNumber);
        updateCmd.Parameters.AddWithValue(worlds[i].Id);
        await updateCmd.ExecuteNonQueryAsync(ctx.RequestAborted);
    }

    await WriteBytesAsync(ctx, BuildWorldArrayJson(worlds), AppJsonUtf8);
});

app.MapGet("/cached-queries", async ctx =>
{
    await worldCacheWarmupTask.WaitAsync(ctx.RequestAborted);
    var count = ParseQueryCount(ctx.Request.Query, "count", 1, 500, 1);
    var worlds = new (int Id, int RandomNumber)[count];
    for (var i = 0; i < count; i++)
    {
        var id = rng.Next(1, 10001);
        worlds[i] = (id, worldCache.GetValueOrDefault(id, 0));
    }

    await WriteBytesAsync(ctx, BuildWorldArrayJson(worlds), AppJsonUtf8);
});

app.MapFallback(ctx => WriteBytesAsync(ctx, plainBody, TextPlainUtf8));

app.Lifetime.ApplicationStarted.Register(() =>
{
    var allocatedBytes = GC.GetTotalAllocatedBytes(true) - startupAllocatedBefore;
    Console.WriteLine($"Listening on {url}");
    Console.WriteLine(
        $"[startup-metrics] ready_ms={startupStopwatch.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)} allocated_bytes={allocatedBytes}");
});

await app.RunAsync(cts.Token);

static async Task WriteBytesAsync(HttpContext ctx, byte[] body, string contentType)
{
    ctx.Response.StatusCode = StatusCodes.Status200OK;
    ctx.Response.ContentType = contentType;
    ctx.Response.ContentLength = body.Length;
    await ctx.Response.BodyWriter.WriteAsync(body, ctx.RequestAborted);
}

static byte[] BuildWorldJson(int id, int randomNumber)
{
    Span<byte> buf = stackalloc byte[64];
    var pos = 0;
    "{\"id\":"u8.CopyTo(buf[pos..]); pos += 6;
    Utf8Formatter.TryFormat(id, buf[pos..], out var written); pos += written;
    ",\"randomNumber\":"u8.CopyTo(buf[pos..]); pos += 16;
    Utf8Formatter.TryFormat(randomNumber, buf[pos..], out written); pos += written;
    buf[pos++] = (byte)'}';
    return buf[..pos].ToArray();
}

static byte[] BuildWorldArrayJson((int Id, int RandomNumber)[] worlds)
{
    var sb = new StringBuilder("[");
    for (var i = 0; i < worlds.Length; i++)
    {
        if (i > 0)
        {
            sb.Append(',');
        }

        sb.Append("{\"id\":").Append(worlds[i].Id)
            .Append(",\"randomNumber\":").Append(worlds[i].RandomNumber).Append('}');
    }

    sb.Append(']');
    return Encoding.UTF8.GetBytes(sb.ToString());
}

static byte[] BuildFortunesHtml(List<(int Id, string Message)> fortunes)
{
    var sb = new StringBuilder(
        "<!DOCTYPE html><html><head><title>Fortunes</title></head><body><table><tr><th>id</th><th>message</th></tr>");

    foreach (var (id, message) in fortunes)
    {
        sb.Append("<tr><td>").Append(id).Append("</td><td>")
            .Append(HtmlEncode(message)).Append("</td></tr>");
    }

    sb.Append("</table></body></html>");
    return Encoding.UTF8.GetBytes(sb.ToString());
}

static string HtmlEncode(string value) =>
    value.Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#x27;");

static int ParseQueryCount(IQueryCollection query, string key, int min, int max, int defaultValue)
{
    if (!query.TryGetValue(key, out var rawValue))
    {
        return defaultValue;
    }

    return int.TryParse(rawValue, out var parsedValue)
        ? Math.Clamp(parsedValue, min, max)
        : defaultValue;
}

async Task PopulateWorldCacheAsync()
{
    try
    {
        await using var cacheConn = await dataSource.OpenConnectionAsync();
        await using var cacheCmd = cacheConn.CreateCommand();
        cacheCmd.CommandText = "SELECT id, randomnumber FROM cachedworld";
        await using var cacheReader = await cacheCmd.ExecuteReaderAsync();
        while (await cacheReader.ReadAsync())
        {
            worldCache[cacheReader.GetInt32(0)] = cacheReader.GetInt32(1);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[warn] World cache population failed (DB not available?): {ex.Message}");
    }
}

// ── TFB JSON Serialization model ─────────────────────────────────────────────

internal record struct TfbJsonMessage([property: JsonPropertyName("message")] string Message);

[JsonSerializable(typeof(TfbJsonMessage))]
internal partial class KestrelAppJsonContext : JsonSerializerContext { }
