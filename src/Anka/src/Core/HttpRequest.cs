using System.Buffers;
using System.Text;

namespace Anka;

/// <summary>
/// Represents a parsed HTTP request managed by the framework.
/// This class handles rented buffers for storing request path, query, headers, and body data.
/// Provides access to HTTP method, version, path, query, headers, and body content.
/// The <see cref="Return"/> method must be called to release the rented resources once the request is fully processed.
/// </summary>
public sealed class HttpRequest
{
    /// <summary>
    /// Buffer containing path, query, and header name/value data for the HTTP request.
    /// This buffer is populated by the HTTP parser and serves as the data source
    /// for various request components, enabling zero-copy access to underlying bytes.
    /// Allocates memory via a shared array pool and must be returned to the pool
    /// after use to prevent memory leaks.
    /// </summary>
    internal byte[]? Buffer; // set by HttpParser before handing off

    /// <summary>
    /// Temporary buffer allocated for storing the HTTP request body during parsing.
    /// Managed internally by the HTTP parser and returned to the shared array pool
    /// after processing to minimize memory allocations.
    /// </summary>
    internal byte[]? BodyBuffer;

    /// <summary>
    /// Represents the HTTP method of the request, such as GET, POST, PUT, etc.
    /// </summary>
    public HttpMethod Method { get; internal set; }

    /// <summary>
    /// Represents the HTTP version of the request. This property is used to indicate
    /// the protocol version (e.g., HTTP/1.0, HTTP/1.1) as specified in the request line
    /// and is critical for determining the features and behaviors of the HTTP request.
    /// </summary>
    public HttpVersion Version { get; internal set; }

    /// <summary>
    /// Cached string representation of the request path, decoded from the buffer containing the raw path data.
    /// The value is initialized lazily upon first access of the <see cref="Path"/> property and is reset
    /// whenever the path is modified or the request is reset.
    /// </summary>
    private string? _pathStr;

    /// <summary>Decoded query string extracted from the HTTP request, allocated and cached once accessed.</summary>
    private string? _queryStr;
    
    // Path stored as a slice of _buf; string materialized lazily
    /// <summary>
    /// The zero-based offset, within the internal buffer, where the HTTP request path starts.
    /// Used as part of a slice for extracting the path bytes.
    /// </summary>
    private ushort  _pathOffset, _pathLength;

    /// <summary>
    /// The offset within the buffer where the query string begins.
    /// Used in conjunction with <see cref="_queryLength"/> to extract the query portion
    /// of an HTTP request without additional memory allocations.
    /// </summary>
    private ushort  _queryOffset, _queryLength;
    private ushort _authorityOffset, _authorityLength;

    /// <summary>
    /// Gets the path of the HTTP request as a read-only span of bytes, representing a slice of the internal buffer.
    /// This property allows access to the raw, unprocessed binary data of the request's path without additional allocations.
    /// </summary>
    public ReadOnlySpan<byte> PathBytes
        => Buffer.AsSpan(_pathOffset, _pathLength);

    /// <summary>
    /// Provides the segment of the internal buffer that contains the raw query string as a read-only byte span.
    /// This enables zero-copy access to the query portion of the request URI.
    /// </summary>
    public ReadOnlySpan<byte> QueryBytes
        => Buffer.AsSpan(_queryOffset, _queryLength);

    internal ReadOnlySpan<byte> AuthorityBytes
        => Buffer.AsSpan(_authorityOffset, _authorityLength);

    /// <summary>
    /// Returns true when the request path exactly matches <paramref name="path"/>.
    /// Uses a zero-allocation span comparison — avoids materializing the path string.
    /// </summary>
    public bool PathEquals(ReadOnlySpan<byte> path) => PathBytes.SequenceEqual(path);

    /// <summary>
    /// Gets the decoded path string of the HTTP request.
    /// The value is derived from the underlying byte span and cached for reuse,
    /// ensuring the same string reference is returned for later accesses.
    /// </summary>
    public string Path => _pathStr ??= Encoding.ASCII.GetString(PathBytes);

    /// <summary>
    /// Decoded query string extracted from the request URL, or null if no query is present.
    /// Allocates and caches the decoded value on first access.
    /// </summary>
    public string? QueryString => _queryLength > 0
                                      ? _queryStr ??= Encoding.ASCII.GetString(QueryBytes)
                                      : null;

    /// <summary>
    /// Represents the collection of HTTP headers for the request. Optimized for zero-copy lookups and backed by a buffer.
    /// </summary>
    public HttpHeaders Headers;

    /// <summary>
    /// Represents the body content of the HTTP request as a read-only sequence of bytes.
    /// This property provides access to the raw binary data of the request body,
    /// which could be empty if the request does not include body content.
    /// </summary>
    public ReadOnlyMemory<byte> Body { get; internal set; }

    /// <summary>
    /// Indicates whether the HTTP connection should be kept alive after the current request.
    /// This property is typically determined based on the HTTP version and the "Connection" header value.
    /// </summary>
    public bool IsKeepAlive { get; internal set; }

    /// <summary>
    /// Indicates that the request uses chunked transfer-encoding for its body.
    /// </summary>
    internal bool HasChunkedTransferEncoding { get; set; }

    /// <summary>
    /// Indicates that at least one Content-Length header was present.
    /// </summary>
    internal bool HasContentLength { get; set; }

    /// <summary>
    /// Indicates that a Content-Length header was present but could not be parsed as a non-negative integer.
    /// </summary>
    internal bool HasInvalidContentLength { get; set; }

    /// <summary>
    /// Parsed numeric Content-Length value from the request headers.
    /// </summary>
    internal long ContentLength { get; set; }

    /// <summary>
    /// Indicates that a valid numeric Content-Length has been parsed.
    /// </summary>
    internal bool HasParsedContentLength { get; set; }
    internal RequestTargetForm RequestTargetForm { get; set; }
    internal AbsoluteFormScheme AbsoluteFormScheme { get; set; }

    /// <summary>
    /// Sets the path offset and length for the <see cref="HttpRequest"/> instance.
    /// This method updates the internal state to define the portion of the buffer
    /// representing the HTTP request path.
    /// </summary>
    /// <param name="offset">The starting position of the path in the buffer.</param>
    /// <param name="length">The length of the path in the buffer.</param>
    internal void SetPath(ushort offset, ushort length)
    {
        _pathOffset = offset;
        _pathLength = length;
        _pathStr    = null;
    }

    /// <summary>
    /// Sets the offset and length of the query portion of the HTTP request within the internal buffer.
    /// This method resets the cached query string to ensure it reflects the updated query bytes.
    /// </summary>
    /// <param name="offset">The starting offset of the query bytes in the internal buffer.</param>
    /// <param name="length">The length of the query bytes in the internal buffer.</param>
    internal void SetQuery(ushort offset, ushort length)
    {
        _queryOffset = offset;
        _queryLength = length;
        _queryStr    = null;
    }

    internal void SetAuthority(ushort offset, ushort length)
    {
        _authorityOffset = offset;
        _authorityLength = length;
    }


    /// <summary>
    /// Resets all fields so the instance can be reused for the next request on the same connection.
    /// Retains ownership of <see cref="Buffer"/> and <see cref="BodyBuffer"/> — they are NOT
    /// returned to the pool. The caller (Connection) is responsible for returning buffers when
    /// the connection closes.
    /// </summary>
    internal void ResetForReuse()
    {
        // Keep Buffer — it will be reassigned by the parser if needed.
        // Keep BodyBuffer — returned only if a new body needs a different size.

        Method       = HttpMethod.Unknown;
        Version      = HttpVersion.Unknown;
        Body         = default;
        IsKeepAlive  = false;
        HasChunkedTransferEncoding = false;
        HasContentLength = false;
        HasInvalidContentLength = false;
        ContentLength = 0;
        HasParsedContentLength = false;
        _pathStr     = null;
        _queryStr    = null;
        _pathOffset  = 0;
        _pathLength  = 0;
        _queryOffset = 0;
        _queryLength = 0;
        _authorityOffset = 0;
        _authorityLength = 0;
        RequestTargetForm = RequestTargetForm.Origin;
        AbsoluteFormScheme = AbsoluteFormScheme.None;
        Headers      = default;
    }

    /// <summary>
    /// Returns all rented buffers to the pool and resets all fields. Called once
    /// when the owning connection closes — NOT per request.
    /// </summary>
    internal void Dispose()
    {
        if (Buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(Buffer);
            Buffer = null;
        }

        if (BodyBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(BodyBuffer);
            BodyBuffer = null;
        }

        ResetForReuse();
    }

    /// <summary>
    /// Legacy compatibility — resets the request for reuse on the same connection.
    /// </summary>
    internal void Return() => ResetForReuse();
}
