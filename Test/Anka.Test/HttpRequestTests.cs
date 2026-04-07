using System.Buffers;
using System.Text;

namespace Anka.Test;

/// <summary>
/// Tests for HttpRequest properties: Path, QueryString, Body, IsKeepAlive.
/// All tests go through HttpParser to create real instances.
/// </summary>
public class HttpRequestTests
{
    private static HttpRequest ParseOrFail(string raw)
    {
        var bytes  = Encoding.ASCII.GetBytes(raw);
        var seq    = new ReadOnlySequence<byte>(bytes);
        var reader = new SequenceReader<byte>(seq);
        var req    = new HttpRequest();
        var ok     = HttpParser.TryParse(ref reader, req);
        Assert.True(ok, "Expected request to parse successfully.");
        return req;
    }


    [Fact]
    public void Path_SimpleRoute_ReturnsCorrectString()
    {
        using var req = ParseOrFail("GET /hello HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.Equal("/hello", req.Value.Path);
    }

    [Fact]
    public void PathBytes_MatchesPathString()
    {
        using var req = ParseOrFail("GET /users/42 HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        var fromBytes = Encoding.ASCII.GetString(req.Value.PathBytes);
        Assert.Equal(req.Value.Path, fromBytes);
    }

    [Fact]
    public void Path_CalledMultipleTimes_ReturnsSameInstance()
    {
        using var req = ParseOrFail("GET /items HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        var p1 = req.Value.Path;
        var p2 = req.Value.Path;
        Assert.Same(p1, p2); // Cached — same string reference
    }

    [Fact]
    public void QueryString_Present_ReturnedCorrectly()
    {
        using var req = ParseOrFail("GET /search?q=test&page=2 HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.Equal("q=test&page=2", req.Value.QueryString);
    }

    [Fact]
    public void QueryBytes_MatchesQueryString()
    {
        using var req = ParseOrFail("GET /search?foo=bar HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        var fromBytes = Encoding.ASCII.GetString(req.Value.QueryBytes);
        Assert.Equal(req.Value.QueryString, fromBytes);
    }

    [Fact]
    public void Body_PostRequest_ContainsCorrectBytes()
    {
        const string body = "name=Anka&version=1";
        var raw =
            $"POST /form HTTP/1.1\r\n" +
            $"Host: x.com\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"\r\n" + body;

        using var req = ParseOrFail(raw).AsDisposable();
        Assert.Equal(body, Encoding.ASCII.GetString(req.Value.Body.Span));
    }
    
    [Fact]
    public void IsKeepAlive_Http11NoConnectionHeader_True()
    {
        using var req = ParseOrFail("GET / HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.True(req.Value.IsKeepAlive);
    }

    [Fact]
    public void IsKeepAlive_Http10NoConnectionHeader_False()
    {
        using var req = ParseOrFail("GET / HTTP/1.0\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.False(req.Value.IsKeepAlive);
    }

    [Fact]
    public void IsKeepAlive_Http11ConnectionClose_False()
    {
        using var req = ParseOrFail("GET / HTTP/1.1\r\nHost: x.com\r\nConnection: close\r\n\r\n").AsDisposable();
        Assert.False(req.Value.IsKeepAlive);
    }

    [Fact]
    public void Method_ParsedEnum_MatchesRawMethod()
    {
        using var req = ParseOrFail("DELETE /resource/5 HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.Equal(HttpMethod.Delete, req.Value.Method);
    }

    [Fact]
    public void Version_Http11_ParsedCorrectly()
    {
        using var req = ParseOrFail("GET / HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.Equal(HttpVersion.Http11, req.Value.Version);
    }
    
    [Fact]
    public void QueryString_NoQueryInUrl_IsNull()
    {
        using var req = ParseOrFail("GET /no-query HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.Null(req.Value.QueryString);
    }

    [Fact]
    public void Body_NoBody_IsEmpty()
    {
        using var req = ParseOrFail("GET / HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.True(req.Value.Body.IsEmpty);
    }

    [Fact]
    public void Path_RootPath_IsSlash()
    {
        using var req = ParseOrFail("GET / HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.Equal("/", req.Value.Path);
    }

    [Fact]
    public void QueryString_EmptyQueryAfterQuestionMark_IsNull()
    {
        // HttpRequest.QueryString returns null when _queryLength == 0, even if '?' was present
        using var req = ParseOrFail("GET /? HTTP/1.1\r\nHost: x.com\r\n\r\n").AsDisposable();
        Assert.Null(req.Value.QueryString);
    }

    [Fact]
    public void Return_CalledTwice_DoesNotThrow()
    {
        var bytes  = "GET / HTTP/1.1\r\nHost: x.com\r\n\r\n"u8.ToArray();
        var seq    = new ReadOnlySequence<byte>(bytes);
        var reader = new SequenceReader<byte>(seq);
        var req    = new HttpRequest();
        HttpParser.TryParse(ref reader, req);

        req.Return();
        // Second Return must not throw
        var ex = Record.Exception(() => req.Return());
        Assert.Null(ex);
    }

    [Fact]
    public void Return_ObjectReused_StateIsClean()
    {
        const string raw1 = "POST /upload HTTP/1.1\r\nHost: x.com\r\nContent-Length: 5\r\n\r\nhello";
        const string raw2 = "GET /clean HTTP/1.1\r\nHost: x.com\r\n\r\n";

        var req = new HttpRequest();

        // Parse first request
        var bytes1  = Encoding.ASCII.GetBytes(raw1);
        var seq1    = new ReadOnlySequence<byte>(bytes1);
        var reader1 = new SequenceReader<byte>(seq1);
        HttpParser.TryParse(ref reader1, req);
        Assert.Equal(HttpMethod.Post, req.Method);
        req.ResetForReuse();

        // Parse second request — reuses same object
        var bytes2  = Encoding.ASCII.GetBytes(raw2);
        var seq2    = new ReadOnlySequence<byte>(bytes2);
        var reader2 = new SequenceReader<byte>(seq2);
        HttpParser.TryParse(ref reader2, req);

        // State must reflect the NEW request, not the old one
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/clean", req.Path);
        Assert.True(req.Body.IsEmpty);
        Assert.Null(req.QueryString);
        req.Dispose();
    }
}

/// <summary>Adapter to use HttpRequest in a using block without exposing Return() publicly.</summary>
file static class HttpRequestExtensions
{
    public static HttpRequestDisposable AsDisposable(this HttpRequest req) => new(req);
}

file readonly struct HttpRequestDisposable(HttpRequest req) : IDisposable
{
    public HttpRequest Value => req;
    public void Dispose() => req.Return();
}
