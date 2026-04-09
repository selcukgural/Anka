using System.Text;

namespace Anka;

/// <summary>
/// A fluent builder that collects extra response headers and delegates
/// to <see cref="HttpResponseWriter.WriteAsync(int, ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, bool, ReadOnlySpan{HttpHeader}, CancellationToken)"/> once all headers are set.
/// </summary>
/// <remarks>
/// <para>
/// Obtain an instance via the <c>AddHeader</c> extension method on
/// <see cref="HttpResponseWriter"/> rather than constructing directly:
/// </para>
/// <code>
/// await response
///     .AddHeader(HttpHeaderNames.Location, "/new-path"u8)
///     .WriteAsync(301, default, default, keepAlive: false, ct);
///
/// await response
///     .AddHeader(HttpHeaderNames.AccessControlAllowOrigin, "*"u8)
///     .AddHeader(HttpHeaderNames.AccessControlAllowMethods, "GET, POST"u8)
///     .WriteAsync(200, body, contentType, keepAlive: true, ct);
/// </code>
/// <para>
/// There is no limit on the number of extra headers that can be added.
/// Multiple headers with the same name (e.g., <c>Set-Cookie</c>) are fully supported.
/// </para>
/// <para>
/// This is a convenience API that allocates a <see cref="List{T}"/> per use.
/// For zero-allocation hot paths, pre-build a <c>static readonly HttpHeader[]</c> and pass it
/// directly to <see cref="HttpResponseWriter.WriteAsync(int, ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, bool, ReadOnlySpan{HttpHeader}, CancellationToken)"/>.
/// </para>
/// </remarks>
public readonly struct ResponseContext
{
    private readonly HttpResponseWriter  _writer;
    private readonly List<HttpHeader>    _headers;

    internal ResponseContext(HttpResponseWriter writer)
    {
        _writer  = writer;
        _headers = [];
    }

    /// <summary>
    /// Adds an extra response header using byte spans or arrays.
    /// Accepts <see cref="ReadOnlySpan{T}"/>, <c>byte[]</c>, and <c>"..."u8</c> UTF-8 literals directly.
    /// </summary>
    /// <param name="name">Header name as lowercase ASCII bytes (e.g., <c>HttpHeaderNames.Location</c>).</param>
    /// <param name="value">Header value as ASCII/UTF-8 bytes.</param>
    public ResponseContext AddHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        _headers.Add(new HttpHeader(name.ToArray(), value.ToArray()));
        return this;
    }

    /// <summary>
    /// Adds an extra response header using strings.
    /// <paramref name="name"/> is lower-cased automatically.
    /// </summary>
    public ResponseContext AddHeader(string name, string value)
    {
        _headers.Add(new HttpHeader(name, value));
        return this;
    }

    /// <summary>
    /// Writes the HTTP response with any previously added extra headers.
    /// Delegates to <see cref="HttpResponseWriter.WriteAsync(int, ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, bool, ReadOnlySpan{HttpHeader}, CancellationToken)"/>.
    /// </summary>
    public ValueTask WriteAsync(
        int                  statusCode,
        ReadOnlyMemory<byte> body              = default,
        ReadOnlyMemory<byte> contentType       = default,
        bool                 keepAlive         = true,
        CancellationToken    cancellationToken = default) =>
        _writer.WriteAsync(statusCode, body, contentType, keepAlive,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_headers),
            cancellationToken);
}
