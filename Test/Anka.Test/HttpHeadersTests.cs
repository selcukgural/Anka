namespace Anka.Test;

/// <summary>
/// Tests for HttpHeaders struct.
/// HttpHeaders require an internal byte buffer (normally supplied by HttpParser),
/// so a helper creates a standalone instance for unit testing.
/// </summary>
public class HttpHeadersTests
{
    private static HttpHeaders CreateHeaders(int bufSize = 4096)
    {
        var buf = new byte[bufSize];
        var headers = new HttpHeaders();
        headers.InitBuffer(buf, 0);
        return headers;
    }
    
    [Fact]
    public void Add_ThenTryGetValue_ReturnsValue()
    {
        var headers = CreateHeaders();
        headers.Add("Content-Type"u8, "application/json"u8);

        Assert.True(headers.TryGetValue("content-type"u8, out var value));
        Assert.True(value.SequenceEqual("application/json"u8));
    }

    [Fact]
    public void Add_UppercaseName_StoredAsLowercase()
    {
        var headers = CreateHeaders();
        headers.Add("CONTENT-TYPE"u8, "text/html"u8);

        Assert.True(headers.TryGetValue("content-type"u8, out _));
    }

    [Fact]
    public void TryGetValue_StringOverload_CaseInsensitive()
    {
        var headers = CreateHeaders();
        headers.Add("Authorization"u8, "Bearer token123"u8);

        Assert.True(headers.TryGetValue("Authorization", out var value));
        Assert.True(value.SequenceEqual("Bearer token123"u8));

        Assert.True(headers.TryGetValue("AUTHORIZATION", out _));
        Assert.True(headers.TryGetValue("authorization", out _));
    }
    
    [Fact]
    public void Add_MultipleHeaders_AllRetrievable()
    {
        var headers = CreateHeaders();
        headers.Add("Host"u8, "example.com"u8);
        headers.Add("Accept"u8, "*/*"u8);
        headers.Add("User-Agent"u8, "Anka/1.0"u8);

        Assert.Equal(3, headers.Count);

        Assert.True(headers.TryGetValue("host"u8, out var host));
        Assert.True(host.SequenceEqual("example.com"u8));

        Assert.True(headers.TryGetValue("accept"u8, out var accept));
        Assert.True(accept.SequenceEqual("*/*"u8));

        Assert.True(headers.TryGetValue("user-agent"u8, out var ua));
        Assert.True(ua.SequenceEqual("Anka/1.0"u8));
    }

    [Fact]
    public void TryGetValue_StringOverload_NameLongerThan128_ReturnsFalse()
    {
        var headers = CreateHeaders();
        var longName = new string('x', 129);
        Assert.False(headers.TryGetValue(longName, out _));
    }
    
    [Fact]
    public void TryGetValue_NotFound_ReturnsFalse()
    {
        var headers = CreateHeaders();
        headers.Add("Content-Type"u8, "text/plain"u8);

        Assert.False(headers.TryGetValue("accept"u8, out var value));
        Assert.True(value.IsEmpty);
    }

    [Fact]
    public void Add_ExceedingMaxEntries_ExtraEntriesDropped()
    {
        var headers = CreateHeaders(bufSize: 65536);
        var added = true;

        for (var i = 0; i < 70; i++)
        {
            var name = $"X-Header-{i:D3}";
            added = headers.Add(System.Text.Encoding.ASCII.GetBytes(name), "value"u8);
        }

        // Only first 64 should be stored
        Assert.False(added);
        Assert.Equal(64, headers.Count);

        // Entry 65 should not exist
        Assert.False(headers.TryGetValue("X-Header-064"u8, out _));
    }

    [Fact]
    public void Add_ExceedingBufferCapacity_ReturnsFalse()
    {
        var headers = CreateHeaders(bufSize: 8);
        var added = headers.Add("Host"u8, "example.com"u8);

        Assert.False(added);
        Assert.Equal(0, headers.Count);
    }

    [Fact]
    public void TryGetValue_EmptyHeaders_ReturnsFalse()
    {
        var headers = CreateHeaders();
        Assert.Equal(0, headers.Count);
        Assert.False(headers.TryGetValue("host"u8, out _));
    }

    [Fact]
    public void Add_DuplicateHeaderName_BothEntriesStored()
    {
        var headers = CreateHeaders();
        headers.Add("Set-Cookie"u8, "a=1"u8);
        headers.Add("Set-Cookie"u8, "b=2"u8);

        // TryGetValue returns the first match
        Assert.True(headers.TryGetValue("set-cookie"u8, out var value));
        Assert.True(value.SequenceEqual("a=1"u8));
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void TryGetAllValues_DuplicateHeaderName_ReturnsAllValuesInOrder()
    {
        var headers = CreateHeaders();
        headers.Add("Set-Cookie"u8, "a=1"u8);
        headers.Add("Set-Cookie"u8, "b=2"u8);
        headers.Add("Set-Cookie"u8, "c=3"u8);

        Assert.True(headers.TryGetAllValues("set-cookie"u8, out var values));

        var collected = new List<string>();
        foreach (var value in values)
        {
            collected.Add(System.Text.Encoding.ASCII.GetString(value));
        }

        Assert.Equal(["a=1", "b=2", "c=3"], collected);
    }
}
