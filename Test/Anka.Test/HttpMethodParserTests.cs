using System.Text;

namespace Anka.Test;

public class HttpMethodParserTests
{
    [Theory]
    [InlineData("GET",     HttpMethod.Get)]
    [InlineData("POST",    HttpMethod.Post)]
    [InlineData("PUT",     HttpMethod.Put)]
    [InlineData("DELETE",  HttpMethod.Delete)]
    [InlineData("HEAD",    HttpMethod.Head)]
    [InlineData("OPTIONS", HttpMethod.Options)]
    [InlineData("PATCH",   HttpMethod.Patch)]
    [InlineData("TRACE",   HttpMethod.Trace)]
    [InlineData("CONNECT", HttpMethod.Connect)]
    public void Parse_KnownMethod_ReturnsCorrectEnum(string method, HttpMethod expected)
    {
        var bytes = Encoding.ASCII.GetBytes(method);
        Assert.Equal(expected, HttpMethodParser.Parse(bytes));
    }
    
    [Theory]
    [InlineData(HttpMethod.Get)]
    [InlineData(HttpMethod.Post)]
    [InlineData(HttpMethod.Put)]
    [InlineData(HttpMethod.Delete)]
    [InlineData(HttpMethod.Head)]
    [InlineData(HttpMethod.Options)]
    [InlineData(HttpMethod.Patch)]
    [InlineData(HttpMethod.Trace)]
    [InlineData(HttpMethod.Connect)]
    public void ToBytes_ThenParse_RoundTrip(HttpMethod method)
    {
        var bytes = method.ToBytes();
        Assert.Equal(method, HttpMethodParser.Parse(bytes));
    }
    
    [Fact]
    public void Parse_EmptySpan_ReturnsUnknown()
    {
        Assert.Equal(HttpMethod.Unknown, HttpMethodParser.Parse([]));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("Get")]
    [InlineData("gEt")]
    public void Parse_LowercaseMethod_ReturnsUnknown(string method)
    {
        // Methods are case-sensitive per RFC 7230
        var bytes = Encoding.ASCII.GetBytes(method);
        Assert.Equal(HttpMethod.Unknown, HttpMethodParser.Parse(bytes));
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("BREW")]
    [InlineData("FOO")]
    public void Parse_UnknownMethod_ReturnsUnknown(string method)
    {
        var bytes = Encoding.ASCII.GetBytes(method);
        Assert.Equal(HttpMethod.Unknown, HttpMethodParser.Parse(bytes));
    }

    [Fact]
    public void ToBytes_UnknownMethod_ReturnsUnknownBytes()
    {
        var bytes = HttpMethod.Unknown.ToBytes();
        Assert.True(bytes.SequenceEqual("UNKNOWN"u8));
    }
}
