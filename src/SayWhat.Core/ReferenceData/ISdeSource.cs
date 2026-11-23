namespace SayWhat.Core.ReferenceData;

public interface ISdeSource
{
    Stream Open(string table);
}
