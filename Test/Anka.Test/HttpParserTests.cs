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
        return HttpParser.TryParse(ref reader, request);
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
        var raw = $"{method} / HTTP/1.1\r\nHost: example.com\r\n\r\n";
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
        Assert.False(TryParse("GET / HTTP/1.1", out var req));
        Assert.Null(req);
    }

    [Fact]
    public void TryParse_MissingHeaderTerminator_ReturnsFalse()
    {
        // Headers not terminated with \r\n\r\n
        Assert.False(TryParse("GET / HTTP/1.1\r\nHost: example.com\r\n", out var req));
        Assert.Null(req);
    }

    [Fact]
    public void TryParse_UnknownMethod_ReturnsFalse()
    {
        const string raw = "BREW /coffee HTTP/1.1\r\nHost: example.com\r\n\r\n";
        Assert.False(TryParse(raw, out var req));
        Assert.Null(req);
    }

    [Fact]
    public void TryParse_UnknownVersion_ReturnsFalse()
    {
        const string raw = "GET / HTTP/2.0\r\nHost: example.com\r\n\r\n";
        Assert.False(TryParse(raw, out var req));
        Assert.Null(req);
    }

    [Fact]
    public void TryParse_BodyTruncated_ReturnsFalse()
    {
        // Content-Length says 10 but only 5 bytes of body present
        const string raw = "POST / HTTP/1.1\r\nContent-Length: 10\r\n\r\nhello";
        Assert.False(TryParse(raw, out var req));
        Assert.Null(req);
    }

    [Fact]
    public void TryParse_RequestWithNoHeaders_ParsedSuccessfully()
    {
        const string raw = "GET / HTTP/1.1\r\n\r\n";
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
            $"CONTENT-LENGTH: {body.Length}\r\n" +
            $"\r\n" +
            body;

        Assert.True(TryParse(raw, out var req));
        Assert.Equal(body, Encoding.ASCII.GetString(req!.Body.Span));
        req.Return();
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
