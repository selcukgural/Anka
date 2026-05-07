using System.Buffers;
using System.Text;
using Anka.Extensions;
using Anka.Internal;

namespace Anka.Test;

public class MultipartTests
{
    [Fact]
    public void Parse_MultipartBody_ExtractsParts()
    {
        var boundary = "AaB03x";
        var body = 
            "--AaB03x\r\n" +
            "Content-Disposition: form-data; name=\"field1\"\r\n" +
            "\r\n" +
            "Joe Blow\r\n" +
            "--AaB03x\r\n" +
            "Content-Disposition: form-data; name=\"pics\"; filename=\"file1.txt\"\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "... contents of file1.txt ...\r\n" +
            "--AaB03x--\r\n";

        var seq = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(body));
        var parser = new MultipartParser(seq, Encoding.ASCII.GetBytes(boundary));

        Assert.True(parser.TryReadNextPart(out var part1));
        Assert.True(part1.TryGetContentDisposition(out var name1, out var filename1));
        Assert.Equal("field1", Encoding.ASCII.GetString(name1));
        Assert.True(filename1.IsEmpty);
        Assert.Equal("Joe Blow", Encoding.ASCII.GetString(part1.Content));

        Assert.True(parser.TryReadNextPart(out var part2));
        Assert.True(part2.TryGetContentDisposition(out var name2, out var filename2));
        Assert.Equal("pics", Encoding.ASCII.GetString(name2));
        Assert.Equal("file1.txt", Encoding.ASCII.GetString(filename2));
        Assert.Equal("... contents of file1.txt ...", Encoding.ASCII.GetString(part2.Content));

        Assert.False(parser.TryReadNextPart(out _));
    }
}
