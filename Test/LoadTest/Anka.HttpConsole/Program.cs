using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anka;
using Npgsql;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8080;
var startupStopwatch = Stopwatch.StartNew();
var startupAllocatedBefore = GC.GetTotalAllocatedBytes(true);

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// ── Pre-allocated static response bodies ─────────────────────────────────────

var plainBody     = "Hello from Anka!"u8.ToArray();
var helloWorld    = "Hello, World!"u8.ToArray();
var largeBody     = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Anka is a minimal, zero-allocation HTTP/1.x server for .NET. ", 36))); // ~2 KB
var okBody        = "OK"u8.ToArray();

 ReadOnlyMemory<byte> textPlainCt = "text/plain; charset=utf-8"u8.ToArray();
 ReadOnlyMemory<byte> appJsonCt     = "application/json; charset=utf-8"u8.ToArray();
 ReadOnlyMemory<byte> textHtmlCt    = "text/html; charset=utf-8"u8.ToArray();
ReadOnlyMemory<byte> textPlainNoCt = "text/plain"u8.ToArray();

 // ── PostgreSQL data source ────────────────────────────────────────────────────

 var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ??
             "Host=localhost;Database=hello_world;Username=benchmarkdbuser;Password=benchmarkdbpass";

// NpgsqlSlimDataSourceBuilder is required for Native AOT (no reflection-based type discovery).
 var dataSourceBuilder = new NpgsqlSlimDataSourceBuilder(dbUrl);
 await using var dataSource = dataSourceBuilder.Build();

 // ── World cache (TFB: Cached Queries) ────────────────────────────────────────
 //Populate once at startup: world id is 1-10000, store randomnumber per id.

 var worldCache = new ConcurrentDictionary<int, int>();
 var worldCacheWarmupTask = PopulateWorldCacheAsync();

 var rng = Random.Shared;

// ── Request handler ───────────────────────────────────────────────────────────

var server = new Server(handler: async (req, res, ct) =>
{
    var keepAlive = req.IsKeepAlive;

    if (req.PathEquals("/plain"u8) || req.PathEquals("/"u8))
    {
        await res.WriteAsync(200, plainBody, textPlainCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    
    // ── TFB: Plaintext ────────────────────────────────────────────────
    else if (req.PathEquals("/plaintext"u8))
    {
        await res.WriteAsync(200, helloWorld, textPlainNoCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    
    else if (req.PathEquals("/json"u8))
    {
        // TFB: JSON Serialization — must serialize per-request, not pre-cached.
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new TfbJsonMessage("Hello, World!"),
            AppJsonContext.Default.TfbJsonMessage);
        await res.WriteAsync(200, json, appJsonCt, keepAlive: keepAlive, cancellationToken: ct);
    }

    else if (req.PathEquals("/echo"u8))
    {
        await res.WriteAsync(200, req.Body, appJsonCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    
    else if (req.PathEquals("/health"u8))
    {
        await res.WriteAsync(200, okBody, textPlainCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    
    else if (req.PathEquals("/large"u8))
    {
        await res.WriteAsync(200, largeBody, textPlainCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    
    else if (req.PathEquals("/headers"u8))
    {
        await res.WriteAsync(200, plainBody, textPlainCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    // ── TFB: Single DB Query ──────────────────────────────────────────
    else if (req.PathEquals("/db"u8))
    {
        var id  = rng.Next(1, 10001);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, randomnumber FROM world WHERE id = $1";
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var body = BuildWorldJson(reader.GetInt32(0), reader.GetInt32(1));
            await res.WriteAsync(200, body, appJsonCt, keepAlive: keepAlive, cancellationToken: ct);
        }
        else
        {
            await res.WriteAsync(404, keepAlive: keepAlive, cancellationToken: ct);
        }
    }
    // ── TFB: Multiple DB Queries ──────────────────────────────────────
    else if (req.Path.StartsWith("/queries", StringComparison.InvariantCultureIgnoreCase))
    {
        var count = ParseQueryCount(req.QueryString, "queries", 1, 500, 1);
        var worlds = new (int Id, int RandomNumber)[count];
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        for (var i = 0; i < count; i++)
        {
            var qid = rng.Next(1, 10001);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, randomnumber FROM world WHERE id = $1";
            cmd.Parameters.AddWithValue(qid);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                worlds[i] = (reader.GetInt32(0), reader.GetInt32(1));
        }
        var body = BuildWorldArrayJson(worlds);
        await res.WriteAsync(200, body, appJsonCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    // // ── TFB: Fortunes ─────────────────────────────────────────────────
    else if (req.PathEquals("/fortunes"u8))
    {
        var fortunes = new List<(int Id, string Message)>
        {
            (0, "Additional fortune added at request time.")
        };
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, message FROM fortune";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            fortunes.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        
        fortunes.Sort((a, b) => string.Compare(a.Message, b.Message, StringComparison.Ordinal));
        var body = BuildFortunesHtml(fortunes);
        await res.WriteAsync(200, body, textHtmlCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    // // ── TFB: DB Updates ───────────────────────────────────────────────
    else if (req.Path.StartsWith("/updates", StringComparison.InvariantCultureIgnoreCase))
    {
        var count = ParseQueryCount(req.QueryString, "queries", 1, 500, 1);
        var worlds = new (int Id, int RandomNumber)[count];
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        for (var i = 0; i < count; i++)
        {
            var qid    = rng.Next(1, 10001);
            var newRnd = rng.Next(1, 10001);
            await using var selectCmd = conn.CreateCommand();
            selectCmd.CommandText = "SELECT id, randomnumber FROM world WHERE id = $1";
            selectCmd.Parameters.AddWithValue(qid);
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            worlds[i] = (qid, newRnd);
        }
        // Batch updates ordered by id to reduce lock contention
        Array.Sort(worlds, (a, b) => a.Id.CompareTo(b.Id));
        for (var i = 0; i < count; i++)
        {
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE world SET randomnumber = $1 WHERE id = $2";
            updateCmd.Parameters.AddWithValue(worlds[i].RandomNumber);
            updateCmd.Parameters.AddWithValue(worlds[i].Id);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }
        var body = BuildWorldArrayJson(worlds);
        await res.WriteAsync(200, body, appJsonCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    // // ── TFB: Cached Queries ───────────────────────────────────────────
    else if (req.Path.StartsWith("/cached-queries", StringComparison.InvariantCultureIgnoreCase))
    {
        await worldCacheWarmupTask.WaitAsync(ct);
        var count  = ParseQueryCount(req.QueryString, "count", 1, 500, 1);
        var worlds = new (int Id, int RandomNumber)[count];
        for (var i = 0; i < count; i++)
        {
            var cid = rng.Next(1, 10001);
            worlds[i] = (cid, worldCache.GetValueOrDefault(cid, 0));
        }
        var body = BuildWorldArrayJson(worlds);
        await res.WriteAsync(200, body, appJsonCt, keepAlive: keepAlive, cancellationToken: ct);
    }
    else
    {
        await res.WriteAsync(200, plainBody, textPlainCt, keepAlive: keepAlive, cancellationToken: ct);
    }
}, port: port);

server.ListeningStarted += _ =>
{
    var allocatedBytes = GC.GetTotalAllocatedBytes(true) - startupAllocatedBefore;
    Console.WriteLine(
        $"[startup-metrics] ready_ms={startupStopwatch.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)} allocated_bytes={allocatedBytes}");
};

await server.StartAsync(cts.Token);

// ── Helpers ───────────────────────────────────────────────────────────────────

static byte[] BuildWorldJson(int id, int randomNumber)
{
    // {"id":XXXXX,"randomNumber":XXXXX}  — max 34 bytes, stack-alloc safe
    Span<byte> buf = stackalloc byte[64];
    var pos = 0;
    "{\"id\":"u8.CopyTo(buf[pos..]); pos += 6;
    System.Buffers.Text.Utf8Formatter.TryFormat(id, buf[pos..], out var w); pos += w;
    ",\"randomNumber\":"u8.CopyTo(buf[pos..]); pos += 16;
    System.Buffers.Text.Utf8Formatter.TryFormat(randomNumber, buf[pos..], out w); pos += w;
    buf[pos++] = (byte)'}';
    return buf[..pos].ToArray();
}

static byte[] BuildWorldArrayJson((int Id, int RandomNumber)[] worlds)
{
    var sb = new StringBuilder("[");
    for (var i = 0; i < worlds.Length; i++)
    {
        if (i > 0) sb.Append(',');
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

static string HtmlEncode(string s) =>
    s.Replace("&", "&amp;")
     .Replace("<", "&lt;")
     .Replace(">", "&gt;")
     .Replace("\"", "&quot;")
     .Replace("'", "&#x27;");

static int ParseQueryCount(string? queryString, string key, int min, int max, int defaultValue)
{
    if (queryString is null) return defaultValue;
    var prefix = key + "=";
    var idx    = queryString.IndexOf(prefix, StringComparison.Ordinal);
    if (idx < 0) return defaultValue;
    var start = idx + prefix.Length;
    var end   = queryString.IndexOf('&', start);
    var token = end < 0 ? queryString[start..] : queryString[start..end];
    if (!int.TryParse(token, out var n)) return defaultValue;
    return Math.Clamp(n, min, max);
}

async Task PopulateWorldCacheAsync()
{
    try
    {
        await using var cacheConn = await dataSource.OpenConnectionAsync();
        await using var cacheCmd  = cacheConn.CreateCommand();
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
internal partial class AppJsonContext : JsonSerializerContext { }