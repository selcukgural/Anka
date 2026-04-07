using System.Text;

namespace Anka.Test;

public class HttpVersionParserTests
{
    [Fact]
    public void Parse_Http11_ReturnsHttp11()
    {
        Assert.Equal(HttpVersion.Http11, HttpVersionParser.Parse("HTTP/1.1"u8));
    }

    [Fact]
    public void Parse_Http10_ReturnsHttp10()
    {
        Assert.Equal(HttpVersion.Http10, HttpVersionParser.Parse("HTTP/1.0"u8));
    }
    
    [Fact]
    public void Parse_EmptySpan_ReturnsUnknown()
    {
        Assert.Equal(HttpVersion.Unknown, HttpVersionParser.Parse([]));
    }

    [Theory]
    [InlineData("HTTP/2")]
    [InlineData("HTTP/2.0")]
    [InlineData("HTTP/1.2")]
    public void Parse_UnsupportedVersion_ReturnsUnknown(string version)
    {
        var bytes = Encoding.ASCII.GetBytes(version);
        Assert.Equal(HttpVersion.Unknown, HttpVersionParser.Parse(bytes));
    }

    [Theory]
    [InlineData("http/1.1")]
    [InlineData("Http/1.0")]
    public void Parse_LowercaseVersion_ReturnsUnknown(string version)
    {
        // Version token is case-sensitive per RFC
        var bytes = Encoding.ASCII.GetBytes(version);
        Assert.Equal(HttpVersion.Unknown, HttpVersionParser.Parse(bytes));
    }

    [Fact]
    public void Parse_ArbitraryString_ReturnsUnknown()
    {
        Assert.Equal(HttpVersion.Unknown, HttpVersionParser.Parse("foobar"u8));
    }
}
