using System.Text;

namespace Anka.Test;

public class HttpMethodParserTests
{
    [Theory]
    [InlineData("GET",     RequestMethod.Get)]
    [InlineData("POST",    RequestMethod.Post)]
    [InlineData("PUT",     RequestMethod.Put)]
    [InlineData("DELETE",  RequestMethod.Delete)]
    [InlineData("HEAD",    RequestMethod.Head)]
    [InlineData("OPTIONS", RequestMethod.Options)]
    [InlineData("PATCH",   RequestMethod.Patch)]
    [InlineData("TRACE",   RequestMethod.Trace)]
    [InlineData("CONNECT", RequestMethod.Connect)]
    public void Parse_KnownMethod_ReturnsCorrectEnum(string method, RequestMethod expected)
    {
        var bytes = Encoding.ASCII.GetBytes(method);
        Assert.Equal(expected, HttpMethodParser.Parse(bytes));
    }
    
    [Theory]
    [InlineData(RequestMethod.Get)]
    [InlineData(RequestMethod.Post)]
    [InlineData(RequestMethod.Put)]
    [InlineData(RequestMethod.Delete)]
    [InlineData(RequestMethod.Head)]
    [InlineData(RequestMethod.Options)]
    [InlineData(RequestMethod.Patch)]
    [InlineData(RequestMethod.Trace)]
    [InlineData(RequestMethod.Connect)]
    public void ToBytes_ThenParse_RoundTrip(RequestMethod method)
    {
        var bytes = method.ToBytes();
        Assert.Equal(method, HttpMethodParser.Parse(bytes));
    }
    
    [Fact]
    public void Parse_EmptySpan_ReturnsUnknown()
    {
        Assert.Equal(RequestMethod.Unknown, HttpMethodParser.Parse([]));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("Get")]
    [InlineData("gEt")]
    public void Parse_LowercaseMethod_ReturnsUnknown(string method)
    {
        // Methods are case-sensitive per RFC 7230
        var bytes = Encoding.ASCII.GetBytes(method);
        Assert.Equal(RequestMethod.Unknown, HttpMethodParser.Parse(bytes));
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("BREW")]
    [InlineData("FOO")]
    public void Parse_UnknownMethod_ReturnsUnknown(string method)
    {
        var bytes = Encoding.ASCII.GetBytes(method);
        Assert.Equal(RequestMethod.Unknown, HttpMethodParser.Parse(bytes));
    }

    [Fact]
    public void ToBytes_UnknownMethod_ReturnsUnknownBytes()
    {
        var bytes = RequestMethod.Unknown.ToBytes();
        Assert.True(bytes.SequenceEqual("UNKNOWN"u8));
    }
}
