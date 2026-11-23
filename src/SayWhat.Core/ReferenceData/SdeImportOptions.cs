namespace SayWhat.Core.ReferenceData;

public sealed record SdeImportOptions
{
    public const int DefaultMinimumShipTypes = 100;
    public const int DefaultMinimumSolarSystems = 5000;

    public int MinimumShipTypes { get; init; } = DefaultMinimumShipTypes;

    public int MinimumSolarSystems { get; init; } = DefaultMinimumSolarSystems;

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
