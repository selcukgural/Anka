using System.Text;
using BenchmarkDotNet.Attributes;

namespace Anka.Benchmark;

/// <summary>
/// Provides benchmarks for evaluating the performance of the HttpMethodParser.Parse
/// method across all supported HTTP methods and an unknown/fallback scenario.
/// </summary>
[MemoryDiagnoser]
public class HttpMethodParserBenchmarks
{
    /// <summary>
    /// Represents encoded byte data corresponding to the HTTP GET method used in
    /// HTTP method parsing benchmarks to evaluate parsing performance and throughput.
    /// </summary>
    private byte[] _get = null!;

    /// <summary>
    /// Stores the byte array representation of the HTTP "POST" method.
    /// Used for benchmarking the parsing performance of the HttpMethodParser
    /// when handling the "POST" HTTP method.
    /// </summary>
    private byte[] _post = null!;

    /// <summary>
    /// Represents a byte array corresponding to the HTTP "PUT" method.
    /// This variable is initialized in the <see cref="Setup"/> method
    /// to contain the ASCII-encoded representation of the "PUT" string.
    /// It is used in benchmarking scenarios to measure the performance
    /// of parsing the "PUT" HTTP method with <see cref="HttpMethodParser.Parse"/>.
    /// </summary>
    private byte[] _put     = null!;

    /// <summary>
    /// Represents the encoded byte array for the HTTP DELETE method.
    /// Used during benchmark testing to measure the parsing throughput of
    /// the DELETE HTTP method in the HttpMethodParser.
    /// </summary>
    private byte[] _delete  = null!;

    /// <summary>
    /// Represents the byte-encoded data for the HTTP "HEAD" method,
    /// used to measure and benchmark the parsing performance of the
    /// <see cref="HttpMethodParser"/> specifically for the "HEAD" method.
    /// </summary>
    private byte[] _head    = null!;

    /// <summary>
    /// Represents the raw byte-encoded HTTP method "OPTIONS" used as input data for benchmarking
    /// the HttpMethodParser's ability to parse the OPTIONS HTTP method.
    /// </summary>
    private byte[] _options = null!;

    /// <summary>
    /// Represents the encoded HTTP method "PATCH".
    /// Used in benchmarking to test the performance of the
    /// <see cref="HttpMethodParser.Parse"/> method when parsing the HTTP PATCH method.
    /// </summary>
    private byte[] _patch   = null!;

    /// <summary>
    /// Represents the encoded byte array corresponding to the HTTP TRACE method.
    /// This variable is used in benchmarking scenarios to measure the performance
    /// of parsing HTTP methods with the TRACE method as input.
    /// </summary>
    private byte[] _trace   = null!;

    /// <summary>
    /// Byte array representing the "CONNECT" HTTP method.
    /// Used in benchmarking the parsing performance of the HttpMethodParser for the CONNECT method.
    /// </summary>
    private byte[] _connect = null!;

    /// <summary>
    /// Represents a byte array containing HTTP method data that does not match any
    /// of the predefined HTTP methods (e.g., GET, POST, etc.). This variable is used
    /// to benchmark the parsing performance of unknown or unsupported HTTP methods.
    /// </summary>
    private byte[] _unknown = null!;

    /// <summary>
    /// Prepares encoded byte arrays representing various HTTP methods,
    /// including a fallback value for an unsupported or unknown method.
    /// This setup is executed once globally before running the benchmarks.
    /// </summary>
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

    /// <summary>
    /// Parses the stored input byte array and identifies whether it represents an HTTP GET method.
    /// </summary>
    /// <returns>The HTTP method corresponding to the parsed input, or <see cref="HttpMethod.Unknown"/> if the input does not match.</returns>
    [Benchmark(Baseline = true)]
    public HttpMethod Parse_Get()     => HttpMethodParser.Parse(_get);

    /// <summary>
    /// Parses the HTTP POST method from a byte array representation.
    /// </summary>
    /// <returns>The <see cref="HttpMethod.Post"/> enumeration value if the input matches the POST method; otherwise, <see cref="HttpMethod.Unknown"/>.
    /// </returns>
    [Benchmark]
    public HttpMethod Parse_Post()    => HttpMethodParser.Parse(_post);

    /// <summary>Parses the HTTP PUT method from a given byte span.</summary>
    /// <return>Returns <see cref="HttpMethod.Put"/> if the span represents a valid PUT method; otherwise, returns <see cref="HttpMethod.Unknown"/>.</return>
    [Benchmark]
    public HttpMethod Parse_Put()     => HttpMethodParser.Parse(_put);

    /// <summary>
    /// Benchmarks the parsing of the HTTP DELETE method.
    /// </summary>
    /// <returns>The parsed <see cref="HttpMethod.Delete"/> if the input matches the DELETE method; otherwise, <see cref="HttpMethod.Unknown"/>.</returns>
    [Benchmark]
    public HttpMethod Parse_Delete()  => HttpMethodParser.Parse(_delete);

    /// <summary>Parses the "HEAD" HTTP method from the provided input.</summary>
    /// <returns>An <see cref="HttpMethod"/> representing the "HEAD" method, or <see cref="HttpMethod.Unknown"/> if parsing fails.</returns>
    [Benchmark]
    public HttpMethod Parse_Head()    => HttpMethodParser.Parse(_head);

    /// <summary>
    /// Parses the input data to determine if it represents the HTTP OPTIONS method.
    /// </summary>
    /// <returns>The HTTP method corresponding to OPTIONS if the input matches; otherwise, <see cref="HttpMethod.Unknown"/>.</returns>
    [Benchmark]
    public HttpMethod Parse_Options() => HttpMethodParser.Parse(_options);

    /// <summary>Parses the specified byte span to identify the HTTP PATCH method.</summary>
    /// <returns>An <c>HttpMethod</c> indicating HTTP PATCH if the byte span matches; otherwise, <c>HttpMethod.Unknown</c>.</returns>
    [Benchmark]
    public HttpMethod Parse_Patch()   => HttpMethodParser.Parse(_patch);

    /// <summary>Parses the HTTP TRACE method from the provided byte array.</summary>
    /// <returns>An <see cref="HttpMethod"/> value representing the TRACE method if successfully parsed; otherwise, <see cref="HttpMethod.Unknown"/>.</returns>
    [Benchmark]
    public HttpMethod Parse_Trace()   => HttpMethodParser.Parse(_trace);

    /// <summary>Parses the input data to identify the HTTP CONNECT method.</summary>
    /// <returns>An enumeration value representing the HTTP CONNECT method.</returns>
    [Benchmark]
    public HttpMethod Parse_Connect() => HttpMethodParser.Parse(_connect);

    /// <summary>
    /// Parses an unknown HTTP method, falling through all predefined cases to return the default case.
    /// </summary>
    /// <returns>The <see cref="HttpMethod.Unknown"/> value, indicating an unrecognized HTTP method.</returns>
    [Benchmark]
    public HttpMethod Parse_Unknown() => HttpMethodParser.Parse(_unknown);

    /// <summary>
    /// Converts the specified string into a byte array using ASCII encoding.
    /// </summary>
    /// <param name="s">The string to be encoded into a byte array.</param>
    /// <return>A byte array representation of the given string encoded in ASCII.</return>
    private static byte[] Encode(string s) => Encoding.ASCII.GetBytes(s);
}
