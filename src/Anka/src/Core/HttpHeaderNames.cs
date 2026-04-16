namespace Anka;

/// <summary>
/// Pre-defined HTTP header name byte spans for zero-allocation request lookups and
/// <see cref="HttpHeader"/> construction.
/// All names are lowercase per RFC 7230 (required for request lookups; recommended for responses).
/// </summary>
public static class HttpHeaderNames
{
    #region Request headers

    public static ReadOnlySpan<byte> Host => "host"u8;
    public static ReadOnlySpan<byte> Connection => "connection"u8;
    public static ReadOnlySpan<byte> ContentLength => "content-length"u8;
    public static ReadOnlySpan<byte> ContentType => "content-type"u8;
    public static ReadOnlySpan<byte> TransferEncoding => "transfer-encoding"u8;
    public static ReadOnlySpan<byte> Accept => "accept"u8;
    public static ReadOnlySpan<byte> AcceptEncoding => "accept-encoding"u8;
    public static ReadOnlySpan<byte> Authorization => "authorization"u8;
    public static ReadOnlySpan<byte> UserAgent => "user-agent"u8;
    public static ReadOnlySpan<byte> CacheControl => "cache-control"u8;
    public static ReadOnlySpan<byte> Cookie => "cookie"u8;
    public static ReadOnlySpan<byte> Expect => "expect"u8;
    public static ReadOnlySpan<byte> IfMatch => "if-match"u8;
    public static ReadOnlySpan<byte> IfNoneMatch => "if-none-match"u8;
    public static ReadOnlySpan<byte> IfModifiedSince => "if-modified-since"u8;
    public static ReadOnlySpan<byte> IfUnmodifiedSince => "if-unmodified-since"u8;
    public static ReadOnlySpan<byte> Origin => "origin"u8;
    public static ReadOnlySpan<byte> Referer => "referer"u8;

    #endregion

    #region Response headers

    public static ReadOnlySpan<byte> Location => "location"u8;
    public static ReadOnlySpan<byte> SetCookie => "set-cookie"u8;
    public static ReadOnlySpan<byte> ETag => "etag"u8;
    public static ReadOnlySpan<byte> LastModified => "last-modified"u8;
    public static ReadOnlySpan<byte> Vary => "vary"u8;
    public static ReadOnlySpan<byte> WwwAuthenticate => "www-authenticate"u8;
    public static ReadOnlySpan<byte> Allow => "allow"u8;
    public static ReadOnlySpan<byte> RetryAfter => "retry-after"u8;

    #endregion

    #region CORS headers

    public static ReadOnlySpan<byte> AccessControlAllowOrigin => "access-control-allow-origin"u8;
    public static ReadOnlySpan<byte> AccessControlAllowMethods => "access-control-allow-methods"u8;
    public static ReadOnlySpan<byte> AccessControlAllowHeaders => "access-control-allow-headers"u8;
    public static ReadOnlySpan<byte> AccessControlMaxAge => "access-control-max-age"u8;
    public static ReadOnlySpan<byte> AccessControlExposeHeaders => "access-control-expose-headers"u8;

    #endregion
}