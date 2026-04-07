using BenchmarkDotNet.Attributes;

namespace Anka.Benchmark;

/// <summary>
/// Measures HttpHeaders struct performance:
/// insertion throughput and zero-allocation lookup by byte span and string.
/// </summary>
[MemoryDiagnoser]
public class HttpHeadersBenchmarks
{
    private const int HeaderCount = 10;

    // Pre-built name/value byte arrays
    private static readonly byte[][] Names =
    [
        "host"u8.ToArray(),
        "accept"u8.ToArray(),
        "accept-encoding"u8.ToArray(),
        "authorization"u8.ToArray(),
        "cache-control"u8.ToArray(),
        "connection"u8.ToArray(),
        "user-agent"u8.ToArray(),
        "x-request-id"u8.ToArray(),
        "x-forwarded-for"u8.ToArray(),
        "content-type"u8.ToArray(),
    ];

    private static readonly byte[][] Values =
    [
        "example.com"u8.ToArray(),
        "application/json"u8.ToArray(),
        "gzip, deflate"u8.ToArray(),
        "Bearer token123"u8.ToArray(),
        "no-cache"u8.ToArray(),
        "keep-alive"u8.ToArray(),
        "Anka-Benchmark/1.0"u8.ToArray(),
        "550e8400-e29b-41d4-a716-446655440000"u8.ToArray(),
        "192.168.1.100"u8.ToArray(),
        "application/octet-stream"u8.ToArray(),
    ];

    private byte[] _buf = null!;

    [GlobalSetup]
    public void Setup() => _buf = new byte[8192];

    // ── Add ───────────────────────────────────────────────────────────────────

    /// <summary>Insert 10 headers into a fresh HttpHeaders struct.</summary>
    [Benchmark]
    public int Add_TenHeaders()
    {
        var headers = new HttpHeaders();
        headers.InitBuffer(_buf, 0);

        for (var i = 0; i < HeaderCount; i++)
        {
            headers.Add(Names[i], Values[i]);
        }

        return headers.Count;
    }

    // ── TryGetValue — byte span overload ─────────────────────────────────────

    /// <summary>Lookup the first header (best case — exits on first iteration).</summary>
    [Benchmark]
    public bool TryGetValue_ByteSpan_FirstEntry()
    {
        var headers = BuildPopulatedHeaders();
        return headers.TryGetValue("host"u8, out _);
    }

    /// <summary>Lookup the last header (worst case — full linear scan).</summary>
    [Benchmark]
    public bool TryGetValue_ByteSpan_LastEntry()
    {
        var headers = BuildPopulatedHeaders();
        return headers.TryGetValue("content-type"u8, out _);
    }

    /// <summary>Lookup a header that does not exist — always full scan.</summary>
    [Benchmark]
    public bool TryGetValue_ByteSpan_Missing()
    {
        var headers = BuildPopulatedHeaders();
        return headers.TryGetValue("x-nonexistent"u8, out _);
    }

    // ── TryGetValue — string overload (stackalloc path) ──────────────────────

    /// <summary>Lookup using the string overload (uses stackalloc internally).</summary>
    [Benchmark]
    public bool TryGetValue_String_CaseInsensitive()
    {
        var headers = BuildPopulatedHeaders();
        return headers.TryGetValue("Authorization", out _);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private HttpHeaders BuildPopulatedHeaders()
    {
        var headers = new HttpHeaders();
        headers.InitBuffer(_buf, 0);

        for (var i = 0; i < HeaderCount; i++)
        {
            headers.Add(Names[i], Values[i]);
        }

        return headers;
    }
}
