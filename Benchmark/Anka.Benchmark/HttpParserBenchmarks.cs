using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace Anka.Benchmark;

/// <summary>
/// Measures end-to-end throughput and allocations of HttpParser.TryParse
/// across request shapes ranging from a tiny GET to a 64 KB POST body.
/// </summary>
[MemoryDiagnoser]
public class HttpParserBenchmarks
{
    // ── Pre-built request byte arrays (allocated once in GlobalSetup) ─────────

    private byte[] _simpleGet        = null!;
    private byte[] _getWithHeaders   = null!;
    private byte[] _postSmallBody    = null!;
    private byte[] _postLargeBody    = null!;

    // Reusable request instance — mimics connection-level reuse.
    private HttpRequest _req = null!;

    [GlobalSetup]
    public void Setup()
    {
        _req = new HttpRequest();

        _simpleGet = Encode("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");

        _getWithHeaders = Encode(
            "GET /api/users HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Accept: application/json\r\n" +
            "Accept-Encoding: gzip, deflate\r\n" +
            "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: keep-alive\r\n" +
            "User-Agent: Anka-Benchmark/1.0\r\n" +
            "X-Request-ID: 550e8400-e29b-41d4-a716-446655440000\r\n" +
            "X-Forwarded-For: 192.168.1.100\r\n" +
            "X-Custom-Header: some-value\r\n" +
            "\r\n");

        var smallBody = new string('x', 128);
        _postSmallBody = Encode(
            $"POST /submit HTTP/1.1\r\n" +
            $"Host: example.com\r\n" +
            $"Content-Type: application/octet-stream\r\n" +
            $"Content-Length: {smallBody.Length}\r\n" +
            $"\r\n" +
            smallBody);

        var largeBody = new string('x', 64 * 1024);
        _postLargeBody = Encode(
            $"POST /upload HTTP/1.1\r\n" +
            $"Host: example.com\r\n" +
            $"Content-Type: application/octet-stream\r\n" +
            $"Content-Length: {largeBody.Length}\r\n" +
            $"\r\n" +
            largeBody);
    }

    // ── Benchmarks ────────────────────────────────────────────────────────────

    /// <summary>Minimal GET request — best-case latency.</summary>
    [Benchmark(Baseline = true)]
    public bool SimpleGet()
    {
        var seq    = new ReadOnlySequence<byte>(_simpleGet);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req);
    }

    /// <summary>GET with 10 headers — typical browser-like request.</summary>
    [Benchmark]
    public bool GetWithManyHeaders()
    {
        var seq    = new ReadOnlySequence<byte>(_getWithHeaders);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req);
    }

    /// <summary>POST with a 128-byte body — common API payload size.</summary>
    [Benchmark]
    public bool PostWithSmallBody()
    {
        var seq    = new ReadOnlySequence<byte>(_postSmallBody);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req);
    }

    /// <summary>POST with a 64 KB body — measures body copy overhead.</summary>
    [Benchmark]
    public bool PostWithLargeBody()
    {
        var seq    = new ReadOnlySequence<byte>(_postLargeBody);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req);
    }

    private static byte[] Encode(string s) => Encoding.ASCII.GetBytes(s);
}
