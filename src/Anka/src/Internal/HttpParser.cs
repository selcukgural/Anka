using System.Buffers;

namespace Anka;

/// <summary>
/// Provides functionality for parsing HTTP requests in a high-performance,
/// allocation-efficient manner using a single-pass approach:
///
/// A copy of the <see cref="SequenceReader{T}"/> (<c>parser</c>) is used to
/// scan and parse the request line, headers, and body simultaneously. The
/// original reader is advanced only on success. Content-Length is extracted
/// inline during header parsing, so no second scan is needed.
///
/// HTTP method and version values are parsed as enums. Header names are
/// normalized to lowercase in-place. If a body is present (Content-Length > 0),
/// a separate buffer is rented and the body bytes are copied into it. On any
/// failure all rented buffers are returned immediately. On success ownership of
/// the buffers transfers to the returned <see cref="HttpRequest"/>; the caller
/// must call <see cref="HttpRequest.Return"/> when done.
/// </summary>
internal static class HttpParser
{
    /// <summary>
    /// Attempts to parse an HTTP request from the provided sequence of bytes,
    /// reusing the supplied <paramref name="req"/> instance and its existing buffer
    /// when possible to avoid per-request allocations.
    /// </summary>
    public static HttpParseResult TryParse(ref SequenceReader<byte> reader, HttpRequest req, int? maxRequestTargetSize = null)
    {
        // Work on a copy so the original reader advances only on success.
        var parser = reader;

        // Request line — fast exit before any allocation if incomplete.
        if (!parser.TryReadTo(out ReadOnlySequence<byte> requestLine, "\r\n"u8))
        {
            return HttpParseResult.Incomplete;
        }

        // Determine required buffer size for path + query + headers.
        var bufSize = (int)Math.Min(parser.Remaining + requestLine.Length + 2L, 8192L);
        bufSize = Math.Max(bufSize, 256);

        // Reuse the existing buffer if large enough; otherwise rent a new one.
        if (req.Buffer is null || req.Buffer.Length < bufSize)
        {
            if (req.Buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(req.Buffer);
            }
            
            req.Buffer = ArrayPool<byte>.Shared.Rent(bufSize);
        }

        var buf = req.Buffer;
        var writePos = 0;

        var requestLineResult = ParseRequestLine(requestLine, buf, ref writePos, req, maxRequestTargetSize);
        if (requestLineResult != HttpParseResult.Success)
        {
            return requestLineResult;
        }

        req.Headers.InitBuffer(buf, writePos);

        // Single pass over headers — extracts Content-Length inline.
        long contentLength = 0;

        while (true)
        {
            if (!parser.TryReadTo(out ReadOnlySequence<byte> headerLine, "\r\n"u8))
            {
                return HttpParseResult.Incomplete;
            }

            if (headerLine.Length == 0)
            {
                break; // empty line = end of headers
            }

            if (headerLine.Length > 15 && (headerLine.FirstSpan[0] | 0x20) == (byte)'c')
            {
                TryExtractContentLength(headerLine, ref contentLength);
            }

            ParseHeaderLine(headerLine, ref req.Headers);
        }

        req.HasChunkedTransferEncoding = HasChunkedTransferEncoding(ref req.Headers);

        // Body
        if (!req.HasChunkedTransferEncoding && contentLength > 0)
        {
            if (parser.Remaining < contentLength)
            {
                return HttpParseResult.Incomplete;
            }

            // Reuse existing body buffer if large enough.
            if (req.BodyBuffer is null || req.BodyBuffer.Length < (int)contentLength)
            {
                if (req.BodyBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(req.BodyBuffer);
                }
                
                req.BodyBuffer = ArrayPool<byte>.Shared.Rent((int)contentLength);
            }

            parser.TryCopyTo(req.BodyBuffer.AsSpan(0, (int)contentLength));
            parser.Advance(contentLength);
            req.Body = req.BodyBuffer.AsMemory(0, (int)contentLength);
        }

        req.IsKeepAlive = ComputeKeepAlive(req.Version, ref req.Headers);

        reader = parser; // commit — advance original reader on success
        return HttpParseResult.Success;
    }

    /// <summary>
    /// Attempts to extract the Content-Length value from the provided header line sequence
    /// and update the given reference with the parsed content length.
    /// </summary>
    /// <param name="line">A sequence of bytes representing the header line to be analyzed.</param>
    /// <param name="contentLength">
    /// A reference to the variable that will be updated with the numeric value of the Content-Length
    /// if it is successfully parsed from the header line.
    /// </param>
    private static void TryExtractContentLength(ReadOnlySequence<byte> line, ref long contentLength)
    {
        const int nameLen = 14; // "content-length"

        // Grab the name portion into a stack buffer for comparison
        Span<byte> nameBuf = stackalloc byte[nameLen];
        line.Slice(0, nameLen).CopyTo(nameBuf);

        if (!AsciiEqualsIgnoreCase(nameBuf, "content-length"u8))
        {
            return;
        }

        // Skip ": "
        long pos = nameLen;

        while (pos < line.Length && GetByte(line, pos) is (byte)':' or (byte)' ')
        {
            pos++;
        }

        long value = 0;

        while (pos < line.Length)
        {
            var digit = (uint)(GetByte(line, pos++) - '0');
            if (digit > 9)
            {
                break;
            }

            value = value * 10 + digit;
        }

        contentLength = value;
    }

    /// <summary>
    /// Parses the HTTP request line from the provided byte sequence and extracts method, path, query, and HTTP version information.
    /// This method also copies path and query data into a shared buffer for further processing.
    /// Returns true if the parsing is successful and the request is valid.
    /// </summary>
    /// <param name="seq">The input byte sequence containing the HTTP request line.</param>
    /// <param name="buf">The shared buffer used for copying path and query data.</param>
    /// <param name="writePos">A reference to the position in the shared buffer where data should be written.</param>
    /// <param name="req">An instance of the <see cref="HttpRequest"/> class to hold the parsed request information.</param>
    /// <param name="maxRequestTargetSize">The optional maximum allowed size, in bytes, of the raw request-target.</param>
    /// <returns>The parse result for the request line.</returns>
    private static HttpParseResult ParseRequestLine(
        ReadOnlySequence<byte> seq,
        byte[] buf,
        ref int writePos,
        HttpRequest req,
        int? maxRequestTargetSize)
    {
        // Flatten to a span; single-segment is the common (zero-copy) path.
        ReadOnlySpan<byte> line;
        byte[]? scratch = null;

        if (seq.IsSingleSegment)
        {
            line = seq.FirstSpan;
        }
        else
        {
            scratch = ArrayPool<byte>.Shared.Rent((int)seq.Length);
            seq.CopyTo(scratch);
            line = scratch.AsSpan(0, (int)seq.Length);
        }

        try
        {
            // METHOD SP path[?query] SP HTTP/x.y
            var s1 = line.IndexOf((byte)' ');
            if (s1 <= 0)
            {
                return HttpParseResult.Invalid;
            }

            req.Method = HttpMethodParser.Parse(line[..s1]);

            var rest = line[(s1 + 1)..];
            var s2 = rest.IndexOf((byte)' ');
            if (s2 <= 0)
            {
                return HttpParseResult.Invalid;
            }

            var rawPath = rest[..s2];
            var versionSpan = rest[(s2 + 1)..].TrimEnd((byte)'\r');

            if (maxRequestTargetSize is { } limit && rawPath.Length > limit || rawPath.Length > ushort.MaxValue)
            {
                return HttpParseResult.RequestTargetTooLong;
            }

            req.Version = HttpVersionParser.Parse(versionSpan);

            // Split path / query
            var q = rawPath.IndexOf((byte)'?');
            var pathPart = q >= 0 ? rawPath[..q] : rawPath;
            var queryPart = q >= 0 ? rawPath[(q + 1)..] : ReadOnlySpan<byte>.Empty;

            if (pathPart.Length > ushort.MaxValue || queryPart.Length > ushort.MaxValue)
            {
                return HttpParseResult.RequestTargetTooLong;
            }

            // Copy into shared buffer
            var pathOffset = (ushort)writePos;
            pathPart.CopyTo(buf.AsSpan(writePos));
            writePos += pathPart.Length;
            req.SetPath(pathOffset, (ushort)pathPart.Length);

            var queryOffset = (ushort)writePos;

            if (!queryPart.IsEmpty)
            {
                queryPart.CopyTo(buf.AsSpan(writePos));
                writePos += queryPart.Length;
            }

            req.SetQuery(queryOffset, (ushort)queryPart.Length);

            return req.Method != HttpMethod.Unknown && req.Version != HttpVersion.Unknown
                ? HttpParseResult.Success
                : HttpParseResult.Invalid;
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>
    /// Parses a single HTTP header line and adds the header information to the provided headers' collection.
    /// Supports both single-segment and multi-segment header lines.
    /// </summary>
    /// <param name="seq">The sequence of bytes representing the header line to parse.</param>
    /// <param name="headers">The collection of HTTP headers to which the parsed header will be added.</param>
    private static void ParseHeaderLine(ReadOnlySequence<byte> seq, ref HttpHeaders headers)
    {
        if (seq.IsSingleSegment)
        {
            AddHeaderFromSpan(seq.FirstSpan, ref headers);
            return;
        }

        // Multi-segment line (rare): copy into a temp buffer, then parse.
        var tmp = ArrayPool<byte>.Shared.Rent((int)seq.Length);

        try
        {
            seq.CopyTo(tmp);
            AddHeaderFromSpan(tmp.AsSpan(0, (int)seq.Length), ref headers);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    /// <summary>
    /// Parses a "Name: Value" pair from a specified span and adds the name-value pair to the header collection.
    /// </summary>
    /// <param name="line">The span representing a line containing the header name and value separated by a colon.</param>
    /// <param name="headers">The reference to the header collection where the parsed name-value pair should be added.</param>
    private static void AddHeaderFromSpan(ReadOnlySpan<byte> line, ref HttpHeaders headers)
    {
        var colon = line.IndexOf((byte)':');
        if (colon <= 0)
        {
            return;
        }

        var name = line[..colon];
        var value = line[(colon + 1)..].Trim((byte)' ');

        headers.Add(name, value);
    }

    /// <summary>
    /// Determines whether the HTTP connection should use a persistent connection (Keep-Alive)
    /// based on the HTTP version and the connection header value.
    /// </summary>
    /// <param name="version">The HTTP version of the request.</param>
    /// <param name="headers">The HTTP headers associated with the request.</param>
    /// <returns>
    /// A boolean value indicating whether the connection should be kept alive.
    /// Returns true for persistent connections and false otherwise.
    /// </returns>
    private static bool ComputeKeepAlive(HttpVersion version, ref HttpHeaders headers)
    {
        if (!headers.TryGetValue(HttpHeaderNames.Connection, out var v))
        {
            return version == HttpVersion.Http11;
        }

        if (v.SequenceEqual("close"u8))
        {
            return false;
        }
        
        if (v.SequenceEqual("keep-alive"u8))
        {
            return true;
        }

        return version == HttpVersion.Http11;
    }

    /// <summary>
    /// Determines whether the provided <paramref name="headers"/> indicate that chunked transfer encoding is being used.
    /// </summary>
    /// <param name="headers">The HTTP headers to inspect for the "transfer-encoding" header and its values.</param>
    /// <returns>
    /// <c>true</c> if the "transfer-encoding" header contains a value of "chunked", ignoring case; otherwise, <c>false</c>.
    /// </returns>
    private static bool HasChunkedTransferEncoding(ref HttpHeaders headers)
    {
        if (!headers.TryGetValue(HttpHeaderNames.TransferEncoding, out var value))
        {
            return false;
        }

        while (!value.IsEmpty)
        {
            var comma = value.IndexOf((byte)',');
            var token = comma >= 0 ? value[..comma] : value;
            if (AsciiEqualsIgnoreCase(token.Trim((byte)' '), "chunked"u8))
            {
                return true;
            }

            if (comma < 0)
            {
                break;
            }

            value = value[(comma + 1)..];
        }

        return false;
    }


    /// <summary>
    /// Retrieves the byte at the specified <paramref name="position"/> within a <see cref="ReadOnlySequence{T}"/>.
    /// The sequence can be composed of multiple segments.
    /// </summary>
    /// <param name="seq">The <see cref="ReadOnlySequence{T}"/> from which to retrieve the byte.</param>
    /// <param name="position">The zero-based position of the byte to retrieve within the sequence.</param>
    /// <returns>The byte value at the specified position if found; otherwise, returns 0.</returns>
    private static byte GetByte(ReadOnlySequence<byte> seq, long position)
    {
        // Fast path: position is in the first segment
        if (position < seq.FirstSpan.Length)
        {
            return seq.FirstSpan[(int)position];
        }

        foreach (var segment in seq)
        {
            if (position < segment.Length)
            {
                return segment.Span[(int)position];
            }

            position -= segment.Length;
        }

        return 0;
    }

    /// <summary>
    /// Compares two ASCII byte sequences for equality, ignoring a case.
    /// Returns true if the sequences are equal in a case-insensitive manner; otherwise, returns false.
    /// </summary>
    /// <param name="a">The first ASCII byte sequence.</param>
    /// <param name="b">The second ASCII byte sequence.</param>
    /// <returns>
    /// A boolean value indicating whether the two ASCII byte sequences are equal, ignoring the case.
    /// </returns>
    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            byte x = a[i], y = b[i];
            if (x == y)
            {
                continue;
            }

            if ((uint)(x - 'A') <= 'Z' - 'A')
            {
                x |= 0x20;
            }
            if ((uint)(y - 'A') <= 'Z' - 'A')
            {
                y |= 0x20;
            }
            if (x != y)
            {
                return false;
            }
        }

        return true;
    }
}
