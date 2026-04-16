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
    private byte[] _absoluteFormGet  = null!;
    private byte[] _absoluteFormWithDefaultPort = null!;
    private byte[] _connectAuthorityForm = null!;
    private byte[] _chunkedHeaders = null!;
    private byte[] _chunkedHeadersWithContentLength = null!;

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

        _absoluteFormGet = Encode(
            "GET http://example.com/search?q=benchmark HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");

        _absoluteFormWithDefaultPort = Encode(
            "GET https://example.com:443/search?q=benchmark HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");

        _connectAuthorityForm = Encode(
            "CONNECT example.com:443 HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");

        _chunkedHeaders = Encode(
            "POST /chunked HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");

        _chunkedHeadersWithContentLength = Encode(
            "POST /chunked HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Length: 999\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");
    }

    // ── Benchmarks ────────────────────────────────────────────────────────────

    /// <summary>Minimal GET request — best-case latency.</summary>
    [Benchmark(Baseline = true)]
    public bool SimpleGet()
    {
        var seq    = new ReadOnlySequence<byte>(_simpleGet);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    /// <summary>GET with 10 headers — typical browser-like request.</summary>
    [Benchmark]
    public bool GetWithManyHeaders()
    {
        var seq    = new ReadOnlySequence<byte>(_getWithHeaders);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    /// <summary>POST with a 128-byte body — common API payload size.</summary>
    [Benchmark]
    public bool PostWithSmallBody()
    {
        var seq    = new ReadOnlySequence<byte>(_postSmallBody);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    /// <summary>POST with a 64 KB body — measures body copy overhead.</summary>
    [Benchmark]
    public bool PostWithLargeBody()
    {
        var seq    = new ReadOnlySequence<byte>(_postLargeBody);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    /// <summary>GET in absolute-form — exercises URI normalization and authority validation.</summary>
    [Benchmark]
    public bool AbsoluteFormGet()
    {
        var seq = new ReadOnlySequence<byte>(_absoluteFormGet);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    /// <summary>absolute-form with explicit default port — exercises Host/authority equivalence logic.</summary>
    [Benchmark]
    public bool AbsoluteFormGet_DefaultPortEquivalence()
    {
        var seq = new ReadOnlySequence<byte>(_absoluteFormWithDefaultPort);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    /// <summary>CONNECT authority-form — exercises CONNECT-specific request-target validation.</summary>
    [Benchmark]
    public bool ConnectAuthorityForm()
    {
        var seq = new ReadOnlySequence<byte>(_connectAuthorityForm);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    /// <summary>Header-only parse for chunked request framing.</summary>
    [Benchmark]
    public bool ChunkedHeaders()
    {
        var seq = new ReadOnlySequence<byte>(_chunkedHeaders);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    /// <summary>Header-only parse where Transfer-Encoding overrides Content-Length metadata.</summary>
    [Benchmark]
    public bool ChunkedHeaders_WithContentLengthPrecedence()
    {
        var seq = new ReadOnlySequence<byte>(_chunkedHeadersWithContentLength);
        var reader = new SequenceReader<byte>(seq);
        _req.ResetForReuse();
        return HttpParser.TryParse(ref reader, _req) == HttpParseResult.Success;
    }

    private static byte[] Encode(string s) => Encoding.ASCII.GetBytes(s);
}
