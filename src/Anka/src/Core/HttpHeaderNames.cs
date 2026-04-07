namespace Anka;

/// <summary>
/// Provides a collection of pre-defined HTTP header names as lowercase byte spans for efficient, zero-allocation lookups.
/// </summary>
public static class HttpHeaderNames
{
    public static ReadOnlySpan<byte> Host => "host"u8;
    public static ReadOnlySpan<byte> Connection       => "connection"u8;
    public static ReadOnlySpan<byte> ContentLength    => "content-length"u8;
    public static ReadOnlySpan<byte> ContentType      => "content-type"u8;
    public static ReadOnlySpan<byte> TransferEncoding => "transfer-encoding"u8;
    public static ReadOnlySpan<byte> Accept           => "accept"u8;
    public static ReadOnlySpan<byte> AcceptEncoding   => "accept-encoding"u8;
    public static ReadOnlySpan<byte> Authorization    => "authorization"u8;
    public static ReadOnlySpan<byte> UserAgent        => "user-agent"u8;
    public static ReadOnlySpan<byte> CacheControl     => "cache-control"u8;
    public static ReadOnlySpan<byte> Cookie           => "cookie"u8;
    public static ReadOnlySpan<byte> Origin           => "origin"u8;
    public static ReadOnlySpan<byte> Referer          => "referer"u8;
}
