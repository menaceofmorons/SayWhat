namespace SayWhat.Core.ReferenceData;

public sealed class SdeImportException : Exception
{
    public SdeImportException(string message)
        : base(message)
    {
    }

    public SdeImportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
