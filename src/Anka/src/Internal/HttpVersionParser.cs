namespace Anka;

/// <summary>
/// Provides parsing functionality for HTTP version strings present in HTTP request lines.
/// </summary>
internal static class HttpVersionParser
{
    /// Parses and determines the HTTP version based on the provided byte sequence.
    /// <param name="span">A span of bytes representing the HTTP version string (e.g., "HTTP/1.1").</param>
    /// <returns>
    /// An <see cref="HttpVersion"/> value that represents the parsed HTTP version,
    /// or <see cref="HttpVersion.Unknown"/> if the version is unrecognized.
    /// </returns>
    public static HttpVersion Parse(ReadOnlySpan<byte> span)
    {
        if (span.SequenceEqual("HTTP/1.1"u8))
        {
            return HttpVersion.Http11;
        }
        
        return span.SequenceEqual("HTTP/1.0"u8) ? HttpVersion.Http10 : HttpVersion.Unknown;
    }
}