namespace Anka;

internal enum HttpVersionParseResult
{
    Success = 0,
    Unsupported = 1,
    Invalid = 2
}

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
        return TryParse(span, out var version) == HttpVersionParseResult.Success
            ? version
            : HttpVersion.Unknown;
    }

    public static HttpVersionParseResult TryParse(ReadOnlySpan<byte> span, out HttpVersion version)
    {
        if (span.SequenceEqual("HTTP/1.1"u8))
        {
            version = HttpVersion.Http11;
            return HttpVersionParseResult.Success;
        }

        if (span.SequenceEqual("HTTP/1.0"u8))
        {
            version = HttpVersion.Http10;
            return HttpVersionParseResult.Success;
        }

        version = HttpVersion.Unknown;

        return IsWellFormedHttpVersion(span)
            ? HttpVersionParseResult.Unsupported
            : HttpVersionParseResult.Invalid;
    }

    private static bool IsWellFormedHttpVersion(ReadOnlySpan<byte> span)
    {
        return span.Length == 8 &&
               span[0] == 'H' &&
               span[1] == 'T' &&
               span[2] == 'T' &&
               span[3] == 'P' &&
               span[4] == '/' &&
               (uint)(span[5] - '0') <= 9 &&
               span[6] == '.' &&
               (uint)(span[7] - '0') <= 9;
    }
}
