using System.Text;

namespace Anka;

/// <summary>
/// An HTTP response header name/value pair for use with
/// <see cref="HttpResponseWriter.WriteAsync(int,System.ReadOnlyMemory{byte},System.ReadOnlyMemory{byte},bool,System.ReadOnlySpan{HttpHeader},System.Threading.CancellationToken)"/>.
/// <para>
/// For zero-allocation hot paths, create instances once at startup and store them in
/// <c>static readonly</c> arrays or spans. The byte-based constructor avoids any heap
/// allocation at call time.
/// </para>
/// </summary>
/// <example>
/// Static CORS headers (allocated once, reused on every request):
/// <code>
/// static readonly HttpHeader[] CorsHeaders =
/// [
///     new HttpHeader("access-control-allow-origin"u8.ToArray(), "*"u8.ToArray()),
///     new HttpHeader("access-control-allow-methods"u8.ToArray(), "GET, POST"u8.ToArray()),
/// ];
/// await res.WriteAsync(200, body, contentType, true, CorsHeaders, ct);
/// </code>
/// </example>
public readonly struct HttpHeader
{
    /// <summary>
    /// The header field name as lowercase ASCII bytes (RFC 7230 §3.2 recommends lowercase for HTTP/2
    /// forward-compatibility, and Anka stores request names in lowercase for the same reason).
    /// </summary>
    public ReadOnlyMemory<byte> Name  { get; }

    /// <summary>The header field value as ASCII/UTF-8 bytes.</summary>
    public ReadOnlyMemory<byte> Value { get; }

    /// <summary>
    /// Initialises the header from pre-encoded byte buffers.
    /// Prefer this constructor in hot paths — no heap allocation at call time.
    /// </summary>
    /// <param name="name">Header name as lowercase ASCII bytes.</param>
    /// <param name="value">Header value as ASCII/UTF-8 bytes.</param>
    public HttpHeader(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value)
    {
        Name  = name;
        Value = value;
    }

    /// <summary>
    /// Convenience constructor that converts strings to ASCII bytes.
    /// Allocates — intended for startup-time constant construction only.
    /// </summary>
    /// <param name="name">Header name (will be lower-cased).</param>
    /// <param name="value">Header value.</param>
    public HttpHeader(string name, string value)
        : this(
            Encoding.ASCII.GetBytes(name.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(value))
    {
    }
}
