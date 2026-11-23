using System.Text;

namespace SayWhat.Core.LogIngest;

public sealed record DecodedLine(string Text, long ByteOffset);

public sealed record DecodedChunk(IReadOnlyList<DecodedLine> Lines, long NextOffset);

public sealed record LogEncoding(Encoding Encoding, int PreambleLength, bool BigEndian)
{
    public int UnitSize => Encoding is UnicodeEncoding ? 2 : 1;
}

public static class LogTextDecoder
{
    public static LogEncoding DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new LogEncoding(new UnicodeEncoding(false, false), 2, false);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new LogEncoding(new UnicodeEncoding(true, false), 2, true);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new LogEncoding(new UTF8Encoding(false), 3, false);
        }

        return new LogEncoding(new UTF8Encoding(false), 0, false);
    }

    public static DecodedChunk ReadCompleteLines(byte[] bytes, long startOffset, LogEncoding encoding)
    {
        var unit = encoding.UnitSize;
        var lines = new List<DecodedLine>();
        var lineStart = (int)startOffset;
        var position = lineStart;

        while (position + unit <= bytes.Length)
        {
            if (!IsNewline(bytes, position, encoding))
            {
                position += unit;
                continue;
            }

            lines.Add(new DecodedLine(Decode(bytes, lineStart, position - lineStart, encoding), lineStart));
            position += unit;
            lineStart = position;
        }

        return new DecodedChunk(lines, lineStart);
    }

    private static bool IsNewline(byte[] bytes, int position, LogEncoding encoding)
    {
        if (encoding.UnitSize == 1)
        {
            return bytes[position] == 0x0A;
        }

        return encoding.BigEndian
            ? bytes[position] == 0x00 && bytes[position + 1] == 0x0A
            : bytes[position] == 0x0A && bytes[position + 1] == 0x00;
    }

    private static string Decode(byte[] bytes, int start, int length, LogEncoding encoding)
    {
        var text = encoding.Encoding.GetString(bytes, start, length);
        return text.EndsWith('\r') ? text[..^1] : text;
    }
}
