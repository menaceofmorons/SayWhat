using SayWhat.Core.ReferenceData;

namespace SayWhat.Tests;

public sealed class ReferenceDatabaseTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "saywhat-ref-" + Guid.NewGuid().ToString("N"));
    private readonly ReferenceDatabase database;

    public ReferenceDatabaseTests()
    {
        Directory.CreateDirectory(directory);
        var sde = SyntheticSde.Create();
        sde.Set(
            "types",
            false,
            SyntheticSde.ShipLine(600, 25, code => code == "en" ? "Rifter" : $"Rifter-{code}"),
            SyntheticSde.ShipLine(601, 25, code => code == "en" ? "Rifter Navy Issue" : $"RNI-{code}"),
            SyntheticSde.ShipLine(602, 25, code => code == "en" ? "Ärgerlich" : $"A-{code}"));
        var path = Path.Combine(directory, "reference.db");
        new SdeImporter(sde, new SdeImportOptions { MinimumShipTypes = 3, MinimumSolarSystems = 3 }).Import(path);
        database = new ReferenceDatabase(path);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void ShipNames_AreOrderedLongestFirst()
    {
        var names = database.ShipNames("en").Select(entry => entry.Name).ToArray();

        Assert.Equal(["Rifter Navy Issue", "Ärgerlich", "Rifter"], names);
    }

    [Theory]
    [InlineData("rifter")]
    [InlineData("RIFTER")]
    [InlineData("  Rifter ")]
    public void FindShipType_IsCaseInsensitiveOnTheNormalisedName(string name)
    {
        Assert.Equal(600, database.FindShipType("en", name));
    }

    [Fact]
    public void FindShipType_IsPerLanguageAndUnknownNamesReturnNull()
    {
        Assert.Equal(600, database.FindShipType("de", "rifter-de"));
        Assert.Null(database.FindShipType("de", "Rifter"));
        Assert.Null(database.FindShipType("en", "Nothing"));
    }

    [Fact]
    public void FindShipType_MatchesNonAsciiCaseInsensitively()
    {
        Assert.Equal(602, database.FindShipType("en", "ÄRGERLICH"));
    }

    [Fact]
    public void ShipName_ReturnsTheNameInTheTargetLanguage()
    {
        Assert.Equal("Rifter-fr", database.ShipName(600, "fr"));
        Assert.Null(database.ShipName(600, "xx"));
        Assert.Null(database.ShipName(12345, "en"));
    }

    [Fact]
    public void TranslateShipName_UsesTheClientTargetLanguage()
    {
        Assert.Equal("Rifter-ja", database.TranslateShipName("en", "rifter", "ja"));
        Assert.Equal("Rifter", database.TranslateShipName("de", "Rifter-de", "en"));
    }

    [Fact]
    public void TranslateShipName_NonClientTargetUsesTheShipNameLanguage()
    {
        Assert.Equal("Rifter", database.TranslateShipName("de", "Rifter-de", "pt"));
        Assert.Equal("Rifter-ru", database.TranslateShipName("de", "Rifter-de", "pt", "ru"));
        Assert.Null(database.TranslateShipName("de", "Unknown", "pt"));
    }

    [Fact]
    public void SystemLookups_WorkInBothDirectionsAndPerLanguage()
    {
        Assert.Equal(30000001, database.FindSystem("en", "sys1"));
        Assert.Equal(30000001, database.FindSystem("zh", "Sys1-zh"));
        Assert.Null(database.FindSystem("zh", "Sys1"));
        Assert.Equal("Sys1-ko", database.SystemName(30000001, "ko"));
        Assert.Equal("Sys2", database.SystemNames("en")[^1]);
        Assert.Equal(3, database.SystemNames("en").Count);
    }

    [Fact]
    public void SdeVersion_IsNullWhenTheDatabaseIsAbsent()
    {
        Assert.Null(new ReferenceDatabase(Path.Combine(directory, "missing.db")).SdeVersion);
        Assert.Equal("4242", database.SdeVersion);
    }
}
