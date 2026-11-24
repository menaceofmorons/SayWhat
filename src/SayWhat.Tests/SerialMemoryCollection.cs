namespace SayWhat.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialMemoryCollection
{
    public const string Name = "serial-memory";
}
