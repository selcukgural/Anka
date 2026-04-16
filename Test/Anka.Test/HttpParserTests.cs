using System.Buffers;
using System.Text;

namespace Anka.Test;

/// <summary>
/// Tests for HttpParser.TryParse covering the full HTTP/1.x parsing pipeline.
/// </summary>
public class HttpParserTests
{
    private static HttpRequest CreateRequest() => new HttpRequest();

    private static bool TryParse(string raw, HttpRequest request)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var seq   = new ReadOnlySequence<byte>(bytes);
        var reader = new SequenceReader<byte>(seq);
        request.ResetForReuse();
        return HttpParser.TryParse(ref reader, request) == HttpParseResult.Success;
    }

    private static HttpParseResult TryParseResult(
        string raw,
        HttpRequest request,
        int? maxRequestTargetSize = null,
        int maxRequestHeadersSize = 8 * 1024)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var seq   = new ReadOnlySequence<byte>(bytes);
        var reader = new SequenceReader<byte>(seq);
        request.ResetForReuse();
        return HttpParser.TryParse(ref reader, request, maxRequestTargetSize, maxRequestHeadersSize);
    }

    private static bool TryParse(string raw, out HttpRequest? request)
    {
        var req = CreateRequest();
        if (TryParse(raw, req))
        {
            request = req;
            return true;
        }
        request = null;
        req.Dispose();
        return false;
    }

    private static HttpParseResult TryParseHeadersResult(
        string raw,
        HttpRequest request,
        out int consumed,
        int? maxRequestTargetSize = null,
        int maxRequestHeadersSize = 8 * 1024)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var seq = new ReadOnlySequence<byte>(bytes);
        var reader = new SequenceReader<byte>(seq);
        request.ResetForReuse();
        var result = HttpParser.TryParseHeaders(ref reader, request, maxRequestTargetSize, maxRequestHeadersSize);
        consumed = result == HttpParseResult.Success ? (int)reader.Consumed : 0;
        return result;
    }
    
    [Fact]
    public void TryParse_SimpleGet_ReturnsRequest()
    {
        const string raw = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.NotNull(req);
        Assert.Equal(HttpMethod.Get, req!.Method);
        Assert.Equal(HttpVersion.Http11, req.Version);
        Assert.Equal("/", req.Path);
        Assert.Null(req.QueryString);
        req.Return();
    }

    [Fact]
    public void TryParse_PostWithBody_BodyReadCorrectly()
    {
        const string body = "{\"key\":\"value\"}";
        var raw =
            $"POST /api/data HTTP/1.1\r\n" +
            $"Host: example.com\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"\r\n" +
            body;

        Assert.True(TryParse(raw, out var req));
        Assert.NotNull(req);
        Assert.Equal(HttpMethod.Post, req!.Method);
        Assert.Equal("/api/data", req.Path);
        Assert.Equal(body, Encoding.ASCII.GetString(req.Body.Span));
        req.Return();
    }

    [Fact]
    public void TryParse_PathWithQueryString_SplitsCorrectly()
    {
        const string raw = "GET /search?q=hello&lang=tr HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.NotNull(req);
        Assert.Equal("/search", req!.Path);
        Assert.Equal("q=hello&lang=tr", req.QueryString);
        req.Return();
    }

    [Fact]
    public void TryParse_MultipleHeaders_AllParsed()
    {
        const string raw =
            "GET / HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Accept: */*\r\n" +
            "Authorization: Bearer abc123\r\n" +
            "User-Agent: Anka/1.0\r\n" +
            "\r\n";

        Assert.True(TryParse(raw, out var req));
        Assert.NotNull(req);
        Assert.Equal(4, req!.Headers.Count);
        Assert.True(req.Headers.TryGetValue(HttpHeaderNames.Authorization, out var auth));
        Assert.True(auth.SequenceEqual("Bearer abc123"u8));
        req.Return();
    }

    [Fact]
    public void TryParse_Http10Request_ParsedCorrectly()
    {
        const string raw = "GET /page HTTP/1.0\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.NotNull(req);
        Assert.Equal(HttpVersion.Http10, req!.Version);
        req.Return();
    }
    
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("PATCH")]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    public void TryParse_AllKnownMethods_ParsedSuccessfully(string method)
    {
        var target = method == "CONNECT" ? "example.com:443" : "/";
        var raw = $"{method} {target} HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.NotNull(req);
        req!.Return();
    }

    [Fact]
    public void TryParse_Http11_DefaultIsKeepAlive()
    {
        const string raw = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.True(req!.IsKeepAlive);
        req.Return();
    }

    [Fact]
    public void TryParse_Http10_DefaultIsNotKeepAlive()
    {
        const string raw = "GET / HTTP/1.0\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.False(req!.IsKeepAlive);
        req.Return();
    }

    [Fact]
    public void TryParse_ConnectionClose_IsKeepAliveFalse()
    {
        const string raw = "GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.False(req!.IsKeepAlive);
        req.Return();
    }

    [Fact]
    public void TryParse_Http10WithConnectionKeepAlive_IsKeepAliveTrue()
    {
        const string raw = "GET / HTTP/1.0\r\nHost: example.com\r\nConnection: keep-alive\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.True(req!.IsKeepAlive);
        req.Return();
    }

    [Fact]
    public void TryParse_NoBody_BodyIsEmpty()
    {
        const string raw = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.True(req!.Body.IsEmpty);
        req.Return();
    }

    [Fact]
    public void TryParse_ContentLengthZero_BodyIsEmpty()
    {
        const string raw = "POST /submit HTTP/1.1\r\nHost: example.com\r\nContent-Length: 0\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.True(req!.Body.IsEmpty);
        req.Return();
    }

    [Fact]
    public void TryParse_RootPath_NullQueryString()
    {
        const string raw = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.Equal("/", req!.Path);
        Assert.Null(req.QueryString);
        req.Return();
    }

    [Fact]
    public void TryParse_DeepPath_ParsedCorrectly()
    {
        const string raw = "GET /api/v1/users/42 HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.Equal("/api/v1/users/42", req!.Path);
        req.Return();
    }
    
    [Fact]
    public void TryParse_EmptyInput_ReturnsFalse()
    {
        Assert.False(TryParse("", out var req));
        Assert.Null(req);
    }

    [Fact]
    public void TryParse_PartialRequestLine_ReturnsFalse()
    {
        var request = CreateRequest();
        var result = TryParseResult("GET / HTTP/1.1", request);
        Assert.Equal(HttpParseResult.Incomplete, result);
        request.Dispose();
        HttpRequest? req = null;
        Assert.Null(req);
    }

    [Fact]
    public void TryParse_MissingHeaderTerminator_ReturnsFalse()
    {
        // Headers not terminated with \r\n\r\n
        var request = CreateRequest();
        var result = TryParseResult("GET / HTTP/1.1\r\nHost: example.com\r\n", request);
        Assert.Equal(HttpParseResult.Incomplete, result);
        request.Dispose();
        HttpRequest? req = null;
        Assert.Null(req);
    }

    [Fact]
    public void TryParse_UnknownMethod_ReturnsFalse()
    {
        var req = CreateRequest();
        var result = TryParseResult("BREW /coffee HTTP/1.1\r\nHost: example.com\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_UnsupportedVersion_ReturnsHttpVersionNotSupported()
    {
        var req = CreateRequest();
        var result = TryParseResult("GET / HTTP/2.0\r\nHost: example.com\r\n\r\n", req);
        Assert.Equal(HttpParseResult.HttpVersionNotSupported, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_InvalidVersion_ReturnsInvalid()
    {
        var req = CreateRequest();
        var result = TryParseResult("GET / http/1.1\r\nHost: example.com\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_DuplicateContentLengthWithSameValue_ReturnsSuccess()
    {
        const string raw =
            "POST / HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Length: 5\r\n" +
            "Content-Length: 5\r\n" +
            "\r\n" +
            "hello";

        Assert.True(TryParse(raw, out var req));
        Assert.Equal("hello", Encoding.ASCII.GetString(req!.Body.Span));
        req.Return();
    }

    [Fact]
    public void TryParse_DuplicateContentLengthWithConflictingValues_ReturnsConflict()
    {
        const string raw =
            "POST / HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Length: 5\r\n" +
            "Content-Length: 7\r\n" +
            "\r\n" +
            "hello!!";

        var req = CreateRequest();
        var result = TryParseResult(raw, req);
        Assert.Equal(HttpParseResult.ConflictingContentLength, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_BodyTruncated_ReturnsFalse()
    {
        // Content-Length says 10 but only 5 bytes of body present
        var req = CreateRequest();
        var result = TryParseResult("POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length: 10\r\n\r\nhello", req);
        Assert.Equal(HttpParseResult.Incomplete, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_RequestTargetExceedsLimit_ReturnsRequestTargetTooLong()
    {
        var raw = $"GET /{new string('a', 32)} HTTP/1.1\r\nHost: example.com\r\n\r\n";
        var req = CreateRequest();
        var result = TryParseResult(raw, req, maxRequestTargetSize: 16);
        Assert.Equal(HttpParseResult.RequestTargetTooLong, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_QueryCountsTowardsRequestTargetLimit()
    {
        const string raw = "GET /search?q=hello HTTP/1.1\r\nHost: example.com\r\n\r\n";
        var req = CreateRequest();
        var result = TryParseResult(raw, req, maxRequestTargetSize: 8);
        Assert.Equal(HttpParseResult.RequestTargetTooLong, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_TooManyHeaders_ReturnsHeaderFieldsTooLarge()
    {
        var builder = new StringBuilder("GET / HTTP/1.1\r\n");
        for (var i = 0; i < 65; i++)
        {
            builder.Append("X-Header-").Append(i.ToString("D2")).Append(": value\r\n");
        }
        builder.Append("\r\n");

        var req = CreateRequest();
        var result = TryParseResult(builder.ToString(), req);
        Assert.Equal(HttpParseResult.HeaderFieldsTooLarge, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_HeaderBytesExceedConfiguredLimit_ReturnsHeaderFieldsTooLarge()
    {
        const string raw =
            "GET / HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "X-Long: abcdefghijklmnopqrstuvwxyz\r\n" +
            "\r\n";

        var req = CreateRequest();
        var result = TryParseResult(raw, req, maxRequestHeadersSize: 16);
        Assert.Equal(HttpParseResult.HeaderFieldsTooLarge, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_Http11WithoutHost_ReturnsMissingHostHeader()
    {
        const string raw = "GET / HTTP/1.1\r\n\r\n";
        var req = CreateRequest();
        var result = TryParseResult(raw, req);
        Assert.Equal(HttpParseResult.MissingHostHeader, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_Http10WithoutHost_ParsedSuccessfully()
    {
        const string raw = "GET / HTTP/1.0\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.NotNull(req);
        Assert.Equal(0, req!.Headers.Count);
        req.Return();
    }

    [Fact]
    public void TryParse_HeaderWithExtraSpacesAroundValue_ValueTrimmed()
    {
        const string raw = "GET / HTTP/1.1\r\nHost:   example.com   \r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.True(req!.Headers.TryGetValue(HttpHeaderNames.Host, out var host));
        // AddHeaderFromSpan calls Trim(' ') — both leading and trailing spaces are removed
        Assert.True(host.SequenceEqual("example.com"u8));
        req.Return();
    }

    [Fact]
    public void TryParse_ContentLengthHeaderCaseInsensitive_BodyRead()
    {
        const string body = "hello";
        var raw =
            $"POST / HTTP/1.1\r\n" +
            $"Host: example.com\r\n" +
            $"CONTENT-LENGTH: {body.Length}\r\n" +
            $"\r\n" +
            body;

        Assert.True(TryParse(raw, out var req));
        Assert.Equal(body, Encoding.ASCII.GetString(req!.Body.Span));
        req.Return();
    }

    [Fact]
    public void TryParseHeaders_ContentLengthBodyIncomplete_ReturnsSuccessAndStopsAtHeaderEnd()
    {
        const string raw =
            "POST /upload HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Length: 10\r\n" +
            "\r\n" +
            "hello";

        var req = CreateRequest();
        var result = TryParseHeadersResult(raw, req, out var consumed);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.Equal("POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 10\r\n\r\n".Length, consumed);
        Assert.True(req.HasContentLength);
        Assert.Equal(10, req.ContentLength);
        Assert.True(req.Body.IsEmpty);
        req.Dispose();
    }

    [Fact]
    public void TryParseHeaders_ChunkedTransferEncoding_SetsChunkedFlagWithoutReadingBody()
    {
        const string raw =
            "POST /upload HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n" +
            "5\r\nhello\r\n0\r\n\r\n";

        var req = CreateRequest();
        var result = TryParseHeadersResult(raw, req, out var consumed);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.Equal("POST /upload HTTP/1.1\r\nHost: example.com\r\nTransfer-Encoding: chunked\r\n\r\n".Length, consumed);
        Assert.True(req.HasChunkedTransferEncoding);
        Assert.True(req.Body.IsEmpty);
        req.Dispose();
    }

    [Fact]
    public void TryParse_AbsoluteFormRequest_NormalizesPathAndQuery()
    {
        const string raw = "GET http://example.com/search?q=hello HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.Equal("/search", req!.Path);
        Assert.Equal("q=hello", req.QueryString);
        req.Return();
    }

    [Theory]
    [InlineData("http://example.com:80/search", "example.com")]
    [InlineData("https://example.com:443/search", "example.com")]
    [InlineData("http://example.com/search", "example.com:80")]
    [InlineData("https://example.com/search", "example.com:443")]
    public void TryParse_AbsoluteFormDefaultPorts_AreTreatedAsEquivalent(string target, string host)
    {
        var raw = $"GET {target} HTTP/1.1\r\nHost: {host}\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.Equal("/search", req!.Path);
        req.Return();
    }

    [Fact]
    public void TryParse_AbsoluteFormWithoutPath_NormalizesToSlash()
    {
        const string raw = "GET http://example.com HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.Equal("/", req!.Path);
        Assert.Null(req.QueryString);
        req.Return();
    }

    [Fact]
    public void TryParse_AbsoluteFormQueryWithoutPath_NormalizesToSlashAndQuery()
    {
        const string raw = "GET http://example.com?q=hello HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.Equal("/", req!.Path);
        Assert.Equal("q=hello", req.QueryString);
        req.Return();
    }

    [Fact]
    public void TryParse_AbsoluteFormHostMismatch_ReturnsInvalid()
    {
        var req = CreateRequest();
        var result = TryParseResult("GET http://example.org/search HTTP/1.1\r\nHost: example.com\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_DuplicateHostHeaders_ReturnsInvalid()
    {
        var req = CreateRequest();
        var result = TryParseResult("GET / HTTP/1.1\r\nHost: example.com\r\nHost: other.example\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_InvalidHostHeader_ReturnsInvalid()
    {
        var req = CreateRequest();
        var result = TryParseResult("GET / HTTP/1.1\r\nHost: bad host\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_Http10DuplicateHostHeaders_ReturnsInvalid()
    {
        var req = CreateRequest();
        var result = TryParseResult("GET / HTTP/1.0\r\nHost: example.com\r\nHost: other.example\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_OptionsAsteriskForm_ParsedSuccessfully()
    {
        const string raw = "OPTIONS * HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.Equal("*", req!.Path);
        req.Return();
    }

    [Fact]
    public void TryParse_GetAsteriskForm_ReturnsInvalid()
    {
        var req = CreateRequest();
        var result = TryParseResult("GET * HTTP/1.1\r\nHost: example.com\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_ConnectAuthorityForm_ParsedSuccessfully()
    {
        const string raw = "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.True(TryParse(raw, out var req));
        Assert.Equal("example.com:443", req!.Path);
        req.Return();
    }

    [Fact]
    public void TryParse_ConnectWithoutPort_ReturnsInvalid()
    {
        var req = CreateRequest();
        var result = TryParseResult("CONNECT example.com HTTP/1.1\r\nHost: example.com\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_ConnectOriginForm_ReturnsInvalid()
    {
        var req = CreateRequest();
        var result = TryParseResult("CONNECT /tunnel HTTP/1.1\r\nHost: example.com\r\n\r\n", req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParseHeaders_TransferEncodingChunked_ClearsContentLengthMetadata()
    {
        const string raw =
            "POST /upload HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Length: 999\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n";

        var req = CreateRequest();
        var result = TryParseHeadersResult(raw, req, out _);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.True(req.HasChunkedTransferEncoding);
        Assert.False(req.HasContentLength);
        Assert.False(req.HasParsedContentLength);
        Assert.False(req.HasInvalidContentLength);
        Assert.Equal(0, req.ContentLength);
        req.Dispose();
    }

    [Theory]
    [InlineData("Transfer-Encoding: gzip\r\n")]
    [InlineData("Transfer-Encoding: chunked, gzip\r\n")]
    [InlineData("Transfer-Encoding: gzip, chunked\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n")]
    public void TryParseHeaders_InvalidTransferEncodingShapes_ReturnInvalid(string transferEncodingHeaders)
    {
        var raw =
            "POST /upload HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            transferEncodingHeaders +
            "\r\n";

        var req = CreateRequest();
        var result = TryParseHeadersResult(raw, req, out _);

        Assert.Equal(HttpParseResult.Invalid, result);
        req.Dispose();
    }

    [Fact]
    public void TryParse_MultipleConsecutiveRequests_EachParsedIndependently()
    {
        const string req1 = "GET /first HTTP/1.1\r\nHost: a.com\r\n\r\n";
        const string req2 = "POST /second HTTP/1.1\r\nHost: b.com\r\nContent-Length: 2\r\n\r\nhi";

        Assert.True(TryParse(req1, out var r1));
        Assert.Equal("/first", r1!.Path);
        r1.Return();

        Assert.True(TryParse(req2, out var r2));
        Assert.Equal("/second", r2!.Path);
        Assert.Equal("hi", Encoding.ASCII.GetString(r2.Body.Span));
        r2.Return();
    }
}
