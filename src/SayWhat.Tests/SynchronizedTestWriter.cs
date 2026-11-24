using System.Text;

namespace SayWhat.Tests;

public sealed class SynchronizedTestWriter : TextWriter
{
    private readonly object gate = new();
    private readonly StringBuilder buffer = new();

    public override Encoding Encoding => Encoding.UTF8;

    public string Snapshot()
    {
        lock (gate)
        {
            return buffer.ToString();
        }
    }

    public override void Write(char value)
    {
        lock (gate)
        {
            buffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        lock (gate)
        {
            buffer.Append(value);
        }
    }

    public override void WriteLine()
    {
        lock (gate)
        {
            buffer.Append(CoreNewLine);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (gate)
        {
            buffer.Append(value).Append(CoreNewLine);
        }
    }
}
