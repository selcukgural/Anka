using System.Buffers;
using System.Buffers.Text;

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
    public static HttpParseResult TryParse(
        ref SequenceReader<byte> reader,
        HttpRequest req,
        int? maxRequestTargetSize = null,
        int maxRequestHeadersSize = 8 * 1024)
    {
        // Work on a copy so the original reader advances only on success.
        var parser = reader;
        var headerResult = TryParseHeadersCore(ref parser, req, maxRequestTargetSize, maxRequestHeadersSize);
        if (headerResult != HttpParseResult.Success)
        {
            return headerResult;
        }

        // Body
        if (req is { HasChunkedTransferEncoding: false, HasContentLength: true, HasInvalidContentLength: false, ContentLength: > 0 })
        {
            if (parser.Remaining < req.ContentLength)
            {
                return HttpParseResult.Incomplete;
            }

            // Reuse existing body buffer if large enough.
            if (req.BodyBuffer is null || req.BodyBuffer.Length < (int)req.ContentLength)
            {
                if (req.BodyBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(req.BodyBuffer);
                }
                
                req.BodyBuffer = ArrayPool<byte>.Shared.Rent((int)req.ContentLength);
            }

            parser.TryCopyTo(req.BodyBuffer.AsSpan(0, (int)req.ContentLength));
            parser.Advance(req.ContentLength);
            req.Body = req.BodyBuffer.AsMemory(0, (int)req.ContentLength);
        }

        req.IsKeepAlive = ComputeKeepAlive(req.Version, ref req.Headers);

        reader = parser; // commit — advance original reader on success
        return HttpParseResult.Success;
    }

    /// <summary>
    /// Attempts to parse HTTP headers from the provided <paramref name="reader"/> sequence,
    /// populating the supplied <paramref name="req"/> object with parsed data, and validating
    /// against optional constraints such as the maximum request target size and header size.
    /// </summary>
    /// <param name="reader">
    /// A <see cref="SequenceReader{T}"/> to read the HTTP headers from. The reader's position
    /// is updated to reflect the consumed bytes if parsing is successful.
    /// </param>
    /// <param name="req">
    /// The <see cref="HttpRequest"/> instance to populate with parsed header data.
    /// </param>
    /// <param name="maxRequestTargetSize">
    /// An optional maximum size, in bytes, allowed for the request target.
    /// If null, no size limit is enforced.
    /// </param>
    /// <param name="maxRequestHeadersSize">
    /// The maximum cumulative size, in bytes, allowed for HTTP headers. If the headers
    /// exceed this limit, parsing will terminate with a failure result.
    /// </param>
    /// <returns>
    /// A <see cref="HttpParseResult"/> indicating the outcome of the parsing operation.
    /// Returns <see cref="HttpParseResult.Success"/> if parsing is successful, or an appropriate
    /// error code if the input is invalid or exceeds constraints.
    /// </returns>
    internal static HttpParseResult TryParseHeaders(
        ref SequenceReader<byte> reader,
        HttpRequest req,
        int? maxRequestTargetSize = null,
        int maxRequestHeadersSize = 8 * 1024)
    {
        var parser = reader;
        var result = TryParseHeadersCore(ref parser, req, maxRequestTargetSize, maxRequestHeadersSize);
        if (result == HttpParseResult.Success)
        {
            reader = parser;
        }

        return result;
    }

    /// <summary>
    /// Parses the headers of an HTTP request from the given sequence of bytes, updating the provided
    /// <paramref name="req"/> instance with the parsed data. Allocates and reuses buffers as needed
    /// to store the parsed request line and headers.
    /// </summary>
    /// <param name="parser">A <see cref="SequenceReader{T}"/> instance that tracks the position within the byte sequence being parsed.</param>
    /// <param name="req">The <see cref="HttpRequest"/> object to populate with parsed values.</param>
    /// <param name="maxRequestTargetSize">An optional maximum allowable size (in bytes) for the request target (e.g., path and query).</param>
    /// <param name="maxRequestHeadersSize">The maximum allowable combined size (in bytes) for all headers.</param>
    /// <returns>
    /// A value from the <see cref="HttpParseResult"/> enumeration indicating the success or failure
    /// of the parsing operation, including specific error conditions.
    /// </returns>
    private static HttpParseResult TryParseHeadersCore(
        ref SequenceReader<byte> parser,
        HttpRequest req,
        int? maxRequestTargetSize,
        int maxRequestHeadersSize)
    {
        // Request line — fast exit before any allocation if incomplete.
        if (!parser.TryReadTo(out ReadOnlySequence<byte> requestLine, "\r\n"u8))
        {
            return HttpParseResult.Incomplete;
        }

        // Determine required buffer size for path + query + headers.
        var reservedTargetBytes = maxRequestTargetSize ?? (int)Math.Min(requestLine.Length, ushort.MaxValue);
        var bufSize = reservedTargetBytes + maxRequestHeadersSize;
        bufSize = Math.Min(bufSize, ushort.MaxValue);
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

        req.Headers.InitBuffer(buf, writePos, maxRequestHeadersSize);

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
                var contentLengthResult = TrackContentLength(headerLine, req);
                if (contentLengthResult != HttpParseResult.Success)
                {
                    return contentLengthResult;
                }
            }

            if (!ParseHeaderLine(headerLine, ref req.Headers))
            {
                return HttpParseResult.HeaderFieldsTooLarge;
            }
        }

        var hostValidationResult = ValidateHostAndRequestTargetAuthority(req);
        if (hostValidationResult != HttpParseResult.Success)
        {
            return hostValidationResult;
        }

        if (!TryAnalyzeTransferEncoding(ref req.Headers, out var hasChunkedTransferEncoding))
        {
            return HttpParseResult.Invalid;
        }

        req.HasChunkedTransferEncoding = hasChunkedTransferEncoding;
        if (req.HasChunkedTransferEncoding)
        {
            req.HasContentLength = false;
            req.HasParsedContentLength = false;
            req.HasInvalidContentLength = false;
            req.ContentLength = 0;
        }
        else if (req.HasInvalidContentLength)
        {
            return HttpParseResult.Invalid;
        }

        req.IsKeepAlive = ComputeKeepAlive(req.Version, ref req.Headers);
        return HttpParseResult.Success;
    }

    /// <summary>
    /// Tracks Content-Length metadata from the provided header line sequence.
    /// </summary>
    /// <param name="line">A sequence of bytes representing the header line to be analyzed.</param>
    /// <param name="request">The request receiving parsed Content-Length metadata.</param>
    /// <returns>The parse result for the Content-Length line.</returns>
    private static HttpParseResult TrackContentLength(ReadOnlySequence<byte> line, HttpRequest request)
    {
        const int nameLen = 14; // "content-length"

        // Grab the name portion into a stack buffer for comparison
        Span<byte> nameBuf = stackalloc byte[nameLen];
        line.Slice(0, nameLen).CopyTo(nameBuf);

        if (!AsciiEqualsIgnoreCase(nameBuf, "content-length"u8))
        {
            return HttpParseResult.Success;
        }

        request.HasContentLength = true;
        var valueStart = nameLen;
        while (valueStart < line.Length && GetByte(line, valueStart) is (byte)':' or (byte)' ')
        {
            valueStart++;
        }

        long parsed;
        if (line.IsSingleSegment)
        {
            if (!TryParseContentLengthValue(line.FirstSpan[valueStart..].Trim((byte)' '), out parsed))
            {
                request.HasInvalidContentLength = true;
                return HttpParseResult.Success;
            }
        }
        else
        {
            var scratch = ArrayPool<byte>.Shared.Rent((int)(line.Length - valueStart));
            try
            {
                line.Slice(valueStart).CopyTo(scratch);
                if (!TryParseContentLengthValue(
                        scratch.AsSpan(0, (int)(line.Length - valueStart)).Trim((byte)' '),
                        out parsed))
                {
                    request.HasInvalidContentLength = true;
                    return HttpParseResult.Success;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        if (request.HasParsedContentLength)
        {
            return parsed == request.ContentLength ? HttpParseResult.Success : HttpParseResult.ConflictingContentLength;
        }

        request.ContentLength = parsed;
        request.HasParsedContentLength = true;
        
        return HttpParseResult.Success;
    }

    /// <summary>
    /// Attempts to parse the provided <paramref name="value"/> as a Content-Length header value,
    /// validating that it represents a valid, non-negative long integer encoded in UTF-8.
    /// </summary>
    /// <param name="value">The span of bytes representing the Content-Length value in UTF-8 encoding.</param>
    /// <param name="parsed">When this method returns, contains the parsed Content-Length value if the parse was successful. Otherwise, it contains 0.</param>
    /// <returns>
    /// <c>true</c> if the parse operation was successful, the entire span was consumed, and the value represents a valid non-negative number; otherwise, <c>false</c>.
    /// </returns>
    private static bool TryParseContentLengthValue(ReadOnlySpan<byte> value, out long parsed)
    {
        var ok = Utf8Parser.TryParse(value, out parsed, out var consumed) &&
                 consumed == value.Length &&
                 parsed >= 0;

        if (ok)
        {
            return true;
        }

        parsed = 0;
        return false;

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

            if ((maxRequestTargetSize is { } limit && rawPath.Length > limit) || rawPath.Length > ushort.MaxValue)
            {
                return HttpParseResult.RequestTargetTooLong;
            }

            if (req.Method == HttpMethod.Unknown)
            {
                return HttpParseResult.Invalid;
            }

            var versionResult = HttpVersionParser.TryParse(versionSpan, out var version);
            switch (versionResult)
            {
                case HttpVersionParseResult.Unsupported:
                    return HttpParseResult.HttpVersionNotSupported;
                case HttpVersionParseResult.Invalid:
                    return HttpParseResult.Invalid;
            }

            req.Version = version;

            if (!TryParseRequestTarget(rawPath, req.Method, out var form, out var scheme, out var authorityPart, out var pathPart, out var queryPart))
            {
                return HttpParseResult.Invalid;
            }

            if (pathPart.Length > ushort.MaxValue || queryPart.Length > ushort.MaxValue || authorityPart.Length > ushort.MaxValue)
            {
                return HttpParseResult.RequestTargetTooLong;
            }

            req.RequestTargetForm = form;
            req.AbsoluteFormScheme = scheme;

            var authorityOffset = (ushort)writePos;
            if (!authorityPart.IsEmpty)
            {
                authorityPart.CopyTo(buf.AsSpan(writePos));
                writePos += authorityPart.Length;
            }
            req.SetAuthority(authorityOffset, (ushort)authorityPart.Length);

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

            return HttpParseResult.Success;
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
    private static bool ParseHeaderLine(ReadOnlySequence<byte> seq, ref HttpHeaders headers)
    {
        if (seq.IsSingleSegment)
        {
            return AddHeaderFromSpan(seq.FirstSpan, ref headers);
        }

        // Multi-segment line (rare): copy into a temp buffer, then parse.
        var tmp = ArrayPool<byte>.Shared.Rent((int)seq.Length);

        try
        {
            seq.CopyTo(tmp);
            return AddHeaderFromSpan(tmp.AsSpan(0, (int)seq.Length), ref headers);
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
    private static bool AddHeaderFromSpan(ReadOnlySpan<byte> line, ref HttpHeaders headers)
    {
        var colon = line.IndexOf((byte)':');
        if (colon <= 0)
        {
            return true;
        }

        var name = line[..colon];
        var value = line[(colon + 1)..].Trim((byte)' ');

        return headers.Add(name, value);
    }

    /// <summary>
    /// Attempts to parse the provided <paramref name="rawTarget"/> into components of an HTTP request target,
    /// determining its format based on the given <paramref name="method"/> and extracting the corresponding parts.
    /// </summary>
    /// <param name="rawTarget">The raw sequence of bytes representing the HTTP request target.</param>
    /// <param name="method">The HTTP method being used in the request, which affects how the target is interpreted.</param>
    /// <param name="form">The determined form of the request target, such as origin-form or absolute-form.</param>
    /// <param name="scheme">The scheme of the request target when in absolute-form.</param>
    /// <param name="authorityPart">The authority component of the target when applicable.</param>
    /// <param name="pathPart">The path component of the target, including the resource being requested.</param>
    /// <param name="queryPart">The query component of the target, representing any parameters included in the request.</param>
    /// <returns>
    /// Returns <c>true</c> if the parsing operation was successful and the target was correctly interpreted;
    /// otherwise, returns <c>false</c> if the target could not be parsed.
    /// </returns>
    private static bool TryParseRequestTarget(
        ReadOnlySpan<byte> rawTarget,
        HttpMethod method,
        out RequestTargetForm form,
        out AbsoluteFormScheme scheme,
        out ReadOnlySpan<byte> authorityPart,
        out ReadOnlySpan<byte> pathPart,
        out ReadOnlySpan<byte> queryPart)
    {
        form = RequestTargetForm.Origin;
        scheme = AbsoluteFormScheme.None;
        authorityPart = default;
        pathPart = default;
        queryPart = default;

        if (rawTarget.SequenceEqual("*"u8))
        {
            if (method != HttpMethod.Options)
            {
                return false;
            }

            form = RequestTargetForm.Asterisk;
            pathPart = rawTarget;
            queryPart = ReadOnlySpan<byte>.Empty;
            return true;
        }

        if (method == HttpMethod.Connect)
        {
            if (!TryParseAuthority(rawTarget, requirePort: true, out _))
            {
                return false;
            }

            form = RequestTargetForm.Authority;
            authorityPart = rawTarget;
            pathPart = rawTarget;
            queryPart = ReadOnlySpan<byte>.Empty;
            return true;
        }

        if (TryParseAbsoluteFormTarget(rawTarget, out scheme, out authorityPart, out pathPart, out queryPart))
        {
            form = RequestTargetForm.Absolute;
            return true;
        }

        if (rawTarget.IsEmpty || rawTarget[0] != (byte)'/')
        {
            return false;
        }

        var q = rawTarget.IndexOf((byte)'?');
        pathPart = q >= 0 ? rawTarget[..q] : rawTarget;
        queryPart = q >= 0 ? rawTarget[(q + 1)..] : ReadOnlySpan<byte>.Empty;
        return true;
    }

    /// <summary>
    /// Attempts to parse an absolute-form request target from the provided <paramref name="rawTarget"/> byte sequence.
    /// Extracts the scheme, authority part, path part, and query part of the target if valid, and determines the scheme type.
    /// </summary>
    /// <param name="rawTarget">
    /// A read-only span of bytes representing the absolute-form request target to parse.
    /// </param>
    /// <param name="scheme">
    /// When this method returns, contains the identified scheme type (e.g., HTTP, HTTPS, or Other) if parsing is successful; otherwise, contains <c>AbsoluteFormScheme.None</c>.
    /// </param>
    /// <param name="authorityPart">
    /// When this method returns, contains the extracted authority part of the request target if parsing is successful; otherwise, contains a default, empty span.
    /// </param>
    /// <param name="pathPart">
    /// When this method returns, contains the extracted path part of the request target if parsing is successful; otherwise, contains a default, empty span.
    /// </param>
    /// <param name="queryPart">
    /// When this method returns, contains the extracted query part of the request target if parsing is successful; otherwise, contains a default, empty span.
    /// </param>
    /// <returns>
    /// <c>true</c> if the absolute-form request target was successfully parsed; otherwise, <c>false</c>.
    /// </returns>
    private static bool TryParseAbsoluteFormTarget(
        ReadOnlySpan<byte> rawTarget,
        out AbsoluteFormScheme scheme,
        out ReadOnlySpan<byte> authorityPart,
        out ReadOnlySpan<byte> pathPart,
        out ReadOnlySpan<byte> queryPart)
    {
        scheme = AbsoluteFormScheme.None;
        authorityPart = default;
        pathPart = default;
        queryPart = default;

        var schemeSeparator = rawTarget.IndexOf("://"u8);
        if (schemeSeparator <= 0)
        {
            return false;
        }

        var schemePart = rawTarget[..schemeSeparator];
        scheme = schemePart.SequenceEqual("http"u8)
            ? AbsoluteFormScheme.Http
            : schemePart.SequenceEqual("https"u8)
                ? AbsoluteFormScheme.Https
                : AbsoluteFormScheme.Other;

        var authorityStart = schemeSeparator + 3;
        if (authorityStart >= rawTarget.Length)
        {
            return false;
        }

        var remainder = rawTarget[authorityStart..];
        var delimiterIndex = remainder.IndexOfAny((byte)'/', (byte)'?');
        if (delimiterIndex < 0)
        {
            authorityPart = remainder;
            pathPart = "/"u8;
            queryPart = ReadOnlySpan<byte>.Empty;
        }
        else
        {
            authorityPart = remainder[..delimiterIndex];
            var afterAuthority = remainder[delimiterIndex..];
            if (afterAuthority[0] == (byte)'?')
            {
                pathPart = "/"u8;
                queryPart = afterAuthority[1..];
            }
            else
            {
                var q = afterAuthority.IndexOf((byte)'?');
                pathPart = q >= 0 ? afterAuthority[..q] : afterAuthority;
                queryPart = q >= 0 ? afterAuthority[(q + 1)..] : ReadOnlySpan<byte>.Empty;
            }
        }

        return !authorityPart.IsEmpty && TryParseAuthority(authorityPart, requirePort: false, out _);
    }

    /// <summary>
    /// Validates the 'Host' header and the authority portion of the request target
    /// in an HTTP request. Ensures the request conforms to expected HTTP semantics
    /// regarding these components.
    /// </summary>
    /// <param name="request">The HTTP request containing headers and target information to validate.</param>
    /// <returns>
    /// Returns a <see cref="HttpParseResult"/> indicating the outcome:
    /// <list type="bullet">
    /// <item><description><c>Success</c> if validation passes.</description></item>
    /// <item><description><c>MissingHostHeader</c> if the 'Host' header is absent in an HTTP/1.1 request.</description></item>
    /// <item><description><c>Invalid</c> if the 'Host' header value or target authority is malformed or inconsistent.</description></item>
    /// </list>
    /// </returns>
    private static HttpParseResult ValidateHostAndRequestTargetAuthority(HttpRequest request)
    {
        if (!request.Headers.TryGetAllValues(HttpHeaderNames.Host, out var hostValues))
        {
            return request.Version == HttpVersion.Http11
                ? HttpParseResult.MissingHostHeader
                : HttpParseResult.Success;
        }

        ReadOnlySpan<byte> hostValue = default;
        var count = 0;
        foreach (var value in hostValues)
        {
            hostValue = value;
            count++;
            if (count > 1)
            {
                return HttpParseResult.Invalid;
            }
        }

        if (!TryParseAuthority(hostValue, requirePort: false, out _))
        {
            return HttpParseResult.Invalid;
        }

        if (request.RequestTargetForm == RequestTargetForm.Absolute &&
            !AuthoritiesEquivalent(request.AuthorityBytes, request.AbsoluteFormScheme, hostValue))
        {
            return HttpParseResult.Invalid;
        }

        return HttpParseResult.Success;
    }

    /// <summary>
    /// Compares two authorities, specified by the given byte sequences, to determine if they are equivalent.
    /// The comparison considers the host and port components of the authorities, with default ports
    /// inferred based on the provided <paramref name="scheme"/>.
    /// </summary>
    /// <param name="left">The first byte sequence representing an authority to compare.</param>
    /// <param name="scheme">The scheme that determines the default port for comparison when no explicit port is specified.</param>
    /// <param name="right">The second byte sequence representing an authority to compare.</param>
    /// <returns>
    /// <c>true</c> if the authorities are equivalent, based on case-insensitive host comparison
    /// and port equality; otherwise, <c>false</c>.
    /// </returns>
    private static bool AuthoritiesEquivalent(ReadOnlySpan<byte> left, AbsoluteFormScheme scheme, ReadOnlySpan<byte> right)
    {
        if (!TryParseAuthority(left, requirePort: false, out var leftParts) ||
            !TryParseAuthority(right, requirePort: false, out var rightParts))
        {
            return false;
        }

        if (!AsciiEqualsIgnoreCase(leftParts.Host, rightParts.Host))
        {
            return false;
        }

        var leftPort = leftParts.HasPort ? leftParts.Port : GetDefaultPort(scheme);
        var rightPort = rightParts.HasPort ? rightParts.Port : GetDefaultPort(scheme);
        return leftPort == rightPort;
    }

    /// <summary>
    /// Retrieves the default port number associated with the specified <paramref name="scheme"/>.
    /// </summary>
    /// <param name="scheme">The scheme for which the default port is to be determined.
    /// Typically, this will be either <see cref="AbsoluteFormScheme.Http"/> or <see cref="AbsoluteFormScheme.Https"/>.</param>
    /// <returns>The default port number for the specified scheme, or <c>null</c> if the scheme does not have a default port.</returns>
    private static int? GetDefaultPort(AbsoluteFormScheme scheme) => scheme switch
    {
        AbsoluteFormScheme.Http => 80,
        AbsoluteFormScheme.Https => 443,
        _ => null
    };

    /// <summary>
    /// Parses the authority component of a URI from the given byte sequence and determines its validity.
    /// </summary>
    /// <param name="value">
    /// The byte sequence representing the authority portion of the URI. This may include the host and optionally a port.
    /// </param>
    /// <param name="requirePort">
    /// Indicates whether a port number is required to be present in the authority component.
    /// </param>
    /// <param name="parts">
    /// When this method returns, contains the parsed authority components, including the host,
    /// a flag indicating the presence of a port, and the port value, if applicable.
    /// </param>
    /// <returns>
    /// <c>true</c> if the authority component is successfully parsed and valid; otherwise, <c>false</c>.
    /// </returns>
    private static bool TryParseAuthority(ReadOnlySpan<byte> value, bool requirePort, out AuthorityParts parts)
    {
        parts = default;
        if (value.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> host;
        var hasPort = false;
        var port = 0;

        if (value[0] == (byte)'[')
        {
            var endBracket = value.IndexOf((byte)']');
            if (endBracket <= 1)
            {
                return false;
            }

            host = value[1..endBracket];
            var tail = value[(endBracket + 1)..];
            if (!tail.IsEmpty)
            {
                if (tail[0] != (byte)':')
                {
                    return false;
                }

                if (!TryParsePort(tail[1..], out port))
                {
                    return false;
                }

                hasPort = true;
            }

            if (!TryValidateIpv6Literal(host))
            {
                return false;
            }
        }
        else
        {
            var colonIndex = value.LastIndexOf((byte)':');
            if (colonIndex >= 0)
            {
                if (value[..colonIndex].IndexOf((byte)':') >= 0)
                {
                    return false;
                }

                host = value[..colonIndex];
                if (!TryParsePort(value[(colonIndex + 1)..], out port))
                {
                    return false;
                }

                hasPort = true;
            }
            else
            {
                host = value;
            }

            if (!TryValidateRegName(host))
            {
                return false;
            }
        }

        if (requirePort && !hasPort)
        {
            return false;
        }

        parts = new AuthorityParts(host, hasPort, port);
        return true;
    }

    /// <summary>
    /// Attempts to parse a port number from the specified read-only span of bytes.
    /// </summary>
    /// <param name="value">A read-only span of bytes representing the port number to parse.</param>
    /// <param name="port">
    /// When this method returns, contains the parsed port number if the conversion succeeded,
    /// or 0 if the conversion failed.
    /// </param>
    /// <returns>
    /// <c>true</c> if the entire span represents a valid port number within the range 0 to 65535;
    /// otherwise, <c>false</c>.
    /// </returns>
    private static bool TryParsePort(ReadOnlySpan<byte> value, out int port)
    {
        port = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var b in value)
        {
            if ((uint)(b - '0') > 9)
            {
                return false;
            }

            port = (port * 10) + (b - '0');
            if (port > 65535)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates whether the specified IPv6 literal is properly formatted.
    /// </summary>
    /// <param name="host">The span of bytes representing the IPv6 literal to validate.</param>
    /// <returns>
    /// <c>true</c> if the provided span represents a valid IPv6 literal;
    /// otherwise, <c>false</c>.
    /// </returns>
    private static bool TryValidateIpv6Literal(ReadOnlySpan<byte> host)
    {
        if (host.IsEmpty)
        {
            return false;
        }

        foreach (var b in host)
        {
            if ((uint)(b - '0') <= 9 ||
                (uint)((b | 0x20) - 'a') <= 'f' - 'a' ||
                b is (byte)':' or (byte)'.')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates whether the provided host name complies with the "reg-name" format
    /// as defined in RFC 3986. The validation ensures that the host name consists
    /// of valid characters, does not begin or end with a period, and adheres to
    /// the allowed structure for registered names.
    /// </summary>
    /// <param name="host">
    /// A span of bytes representing the host name to be validated.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the host name is valid, according to the "reg-name"
    /// format; otherwise, <see langword="false"/>.
    /// </returns>
    private static bool TryValidateRegName(ReadOnlySpan<byte> host)
    {
        if (host.IsEmpty || host[0] == (byte)'.' || host[^1] == (byte)'.')
        {
            return false;
        }

        foreach (var b in host)
        {
            if ((uint)((b | 0x20) - 'a') <= 'z' - 'a' ||
                (uint)(b - '0') <= 9 ||
                b is (byte)'-' or (byte)'.')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Represents the essential parts of an authority segment within a URI,
    /// encapsulating the host, port, and related details during parsing operations.
    /// The authority segment typically consists of a host (e.g., domain or IP address)
    /// and may optionally include a port. This structure is designed to handle both
    /// the presence and absence of port specifications, enabling efficient parsing and
    /// representation of URI authority parts in scenarios such as HTTP request processing.
    /// This structure is immutable and optimized for performance to support scenarios where
    /// a high-throughput, allocation-free approach is required. It can store the raw host
    /// span directly as a <see cref="ReadOnlySpan{T}"/>, avoiding unnecessary allocations.
    /// </summary>
    private readonly ref struct AuthorityParts(ReadOnlySpan<byte> host, bool hasPort, int port)
    {
        public ReadOnlySpan<byte> Host { get; } = host;
        public bool HasPort { get; } = hasPort;
        public int Port { get; } = port;
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
    /// Analyzes the specified HTTP headers to determine if chunked transfer encoding is indicated.
    /// </summary>
    /// <param name="headers">The HTTP headers to analyze for the presence of the "transfer-encoding" header and its associated values.</param>
    /// <param name="hasChunkedTransferEncoding">
    /// When this method returns, contains <c>true</c> if the "transfer-encoding" header specifies "chunked",
    /// ignoring a case. Otherwise, contains <c>false</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the analysis of the "transfer-encoding" header was successfully performed; otherwise, <c>false</c>.
    /// </returns>
    private static bool TryAnalyzeTransferEncoding(ref HttpHeaders headers, out bool hasChunkedTransferEncoding)
    {
        hasChunkedTransferEncoding = false;

        if (!headers.TryGetAllValues(HttpHeaderNames.TransferEncoding, out var values))
        {
            return true;
        }

        var sawChunked = false;

        foreach (var value in values)
        {
            var remaining = value;

            while (!remaining.IsEmpty)
            {
                var comma = remaining.IndexOf((byte)',');
                var token = (comma >= 0 ? remaining[..comma] : remaining).Trim((byte)' ');
                if (token.IsEmpty || sawChunked || !AsciiEqualsIgnoreCase(token, "chunked"u8))
                {
                    return false;
                }

                sawChunked = true;
                if (comma < 0)
                {
                    break;
                }

                remaining = remaining[(comma + 1)..];
            }
        }

        hasChunkedTransferEncoding = sawChunked;
        return true;
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
