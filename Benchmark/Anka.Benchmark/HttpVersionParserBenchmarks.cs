using System.Text;
using BenchmarkDotNet.Attributes;

namespace Anka.Benchmark;

/// <summary>
/// Measures HttpVersionParser.Parse throughput for supported and unsupported version strings.
/// </summary>
[MemoryDiagnoser]
public class HttpVersionParserBenchmarks
{
    private byte[] _http11  = null!;
    private byte[] _http10  = null!;
    private byte[] _http2   = null!;
    private byte[] _unknown = null!;

    [GlobalSetup]
    public void Setup()
    {
        _http11  = "HTTP/1.1"u8.ToArray();
        _http10  = "HTTP/1.0"u8.ToArray();
        _http2   = "HTTP/2"u8.ToArray();
        _unknown = "foobar"u8.ToArray();
    }

    [Benchmark(Baseline = true)]
    public HttpVersion Parse_Http11() => HttpVersionParser.Parse(_http11);

    [Benchmark]
    public HttpVersion Parse_Http10() => HttpVersionParser.Parse(_http10);

    /// <summary>Recognised prefix but unsupported version — falls to default.</summary>
    [Benchmark]
    public HttpVersion Parse_Http2_Unknown() => HttpVersionParser.Parse(_http2);

    /// <summary>Completely arbitrary string — immediate mismatch.</summary>
    [Benchmark]
    public HttpVersion Parse_Arbitrary_Unknown() => HttpVersionParser.Parse(_unknown);
}
