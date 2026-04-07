using System.Text;
using BenchmarkDotNet.Attributes;

namespace Anka.Benchmark;

/// <summary>
/// Measures HttpMethodParser.Parse throughput for all supported HTTP methods
/// and the unknown/fallback case.
/// </summary>
[MemoryDiagnoser]
public class HttpMethodParserBenchmarks
{
    private byte[] _get     = null!;
    private byte[] _post    = null!;
    private byte[] _put     = null!;
    private byte[] _delete  = null!;
    private byte[] _head    = null!;
    private byte[] _options = null!;
    private byte[] _patch   = null!;
    private byte[] _trace   = null!;
    private byte[] _connect = null!;
    private byte[] _unknown = null!;

    [GlobalSetup]
    public void Setup()
    {
        _get     = Encode("GET");
        _post    = Encode("POST");
        _put     = Encode("PUT");
        _delete  = Encode("DELETE");
        _head    = Encode("HEAD");
        _options = Encode("OPTIONS");
        _patch   = Encode("PATCH");
        _trace   = Encode("TRACE");
        _connect = Encode("CONNECT");
        _unknown = Encode("BREW");
    }

    [Benchmark(Baseline = true)]
    public HttpMethod Parse_Get()     => HttpMethodParser.Parse(_get);

    [Benchmark]
    public HttpMethod Parse_Post()    => HttpMethodParser.Parse(_post);

    [Benchmark]
    public HttpMethod Parse_Put()     => HttpMethodParser.Parse(_put);

    [Benchmark]
    public HttpMethod Parse_Delete()  => HttpMethodParser.Parse(_delete);

    [Benchmark]
    public HttpMethod Parse_Head()    => HttpMethodParser.Parse(_head);

    [Benchmark]
    public HttpMethod Parse_Options() => HttpMethodParser.Parse(_options);

    [Benchmark]
    public HttpMethod Parse_Patch()   => HttpMethodParser.Parse(_patch);

    [Benchmark]
    public HttpMethod Parse_Trace()   => HttpMethodParser.Parse(_trace);

    [Benchmark]
    public HttpMethod Parse_Connect() => HttpMethodParser.Parse(_connect);

    /// <summary>Unknown method — falls through all branches to the default case.</summary>
    [Benchmark]
    public HttpMethod Parse_Unknown() => HttpMethodParser.Parse(_unknown);

    private static byte[] Encode(string s) => Encoding.ASCII.GetBytes(s);
}
