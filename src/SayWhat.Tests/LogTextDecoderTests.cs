using System.Text;
using SayWhat.Core.LogIngest;

namespace SayWhat.Tests;

public class LogTextDecoderTests
{
    private const string Content = "first é中\r\nsecond\r\n";

    public static TheoryData<string> Encodings => ["utf16le", "utf16be", "utf8bom", "utf8"];

    [Theory]
    [MemberData(nameof(Encodings))]
    public void CompleteLines_AreDecodedWithByteOffsets(string kind)
    {
        var bytes = Build(kind, Content);
        var encoding = LogTextDecoder.DetectEncoding(bytes);

        var chunk = LogTextDecoder.ReadCompleteLines(bytes, encoding.PreambleLength, encoding);

        Assert.Equal(["first é中", "second"], chunk.Lines.Select(l => l.Text));
        Assert.Equal(encoding.PreambleLength, chunk.Lines[0].ByteOffset);
        var firstLineBytes = Build(kind, "first é中\r\n").Length - encoding.PreambleLength;
        Assert.Equal(encoding.PreambleLength + firstLineBytes, chunk.Lines[1].ByteOffset);
        Assert.Equal(bytes.Length, chunk.NextOffset);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void PartialLastLine_IsWithheldUntilComplete(string kind)
    {
        var bytes = Build(kind, "done\r\npart");
        var encoding = LogTextDecoder.DetectEncoding(bytes);

        var chunk = LogTextDecoder.ReadCompleteLines(bytes, encoding.PreambleLength, encoding);

        Assert.Equal(["done"], chunk.Lines.Select(l => l.Text));
        var completed = Build(kind, "done\r\npart ial\r\n");
        var next = LogTextDecoder.ReadCompleteLines(completed, chunk.NextOffset, encoding);
        Assert.Equal(["part ial"], next.Lines.Select(l => l.Text));
        Assert.Equal(chunk.NextOffset, next.Lines[0].ByteOffset);
    }

    [Fact]
    public void PartialUtf16Unit_IsWithheld()
    {
        var bytes = Build("utf16le", "ab\r\ncd").Append((byte)0x65).ToArray();
        var encoding = LogTextDecoder.DetectEncoding(bytes);

        var chunk = LogTextDecoder.ReadCompleteLines(bytes, encoding.PreambleLength, encoding);

        Assert.Equal(["ab"], chunk.Lines.Select(l => l.Text));
    }

    [Theory]
    [InlineData("utf16le", 2, true)]
    [InlineData("utf16be", 2, true)]
    [InlineData("utf8bom", 3, false)]
    [InlineData("utf8", 0, false)]
    public void Detection_ReadsByteOrderMark(string kind, int preamble, bool utf16)
    {
        var encoding = LogTextDecoder.DetectEncoding(Build(kind, "x\n"));

        Assert.Equal(preamble, encoding.PreambleLength);
        Assert.Equal(utf16, encoding.UnitSize == 2);
    }

    [Fact]
    public void EmptyInput_YieldsNoLines()
    {
        var encoding = LogTextDecoder.DetectEncoding([]);

        var chunk = LogTextDecoder.ReadCompleteLines([], 0, encoding);

        Assert.Empty(chunk.Lines);
        Assert.Equal(0, chunk.NextOffset);
    }

    private static byte[] Build(string kind, string text) => kind switch
    {
        "utf16le" => [0xFF, 0xFE, .. new UnicodeEncoding(false, false).GetBytes(text)],
        "utf16be" => [0xFE, 0xFF, .. new UnicodeEncoding(true, false).GetBytes(text)],
        "utf8bom" => [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(text)],
        _ => new UTF8Encoding(false).GetBytes(text),
    };
}
