using SayWhat.Core.Lines;

namespace SayWhat.Tests.Lines;

public class LineLayoutTests
{
    [Theory]
    [InlineData(LineState.PassThrough, "2026.01.01 12:00:00 Pilot original")]
    [InlineData(LineState.PassThroughTranslationOff, "2026.01.01 12:00:00 Pilot original")]
    [InlineData(LineState.SameLanguage, "2026.01.01 12:00:00 Pilot original")]
    [InlineData(LineState.LanguageUnknown, "2026.01.01 12:00:00 Pilot ? original")]
    [InlineData(LineState.Pending, "2026.01.01 12:00:00 Pilot RU … original")]
    [InlineData(LineState.Failed, "2026.01.01 12:00:00 Pilot RU ✕ original")]
    public void FormatsEachState(LineState state, string expected) =>
        Assert.Equal(expected, LineLayout.Format(LineTestData.Make(1, state)));

    [Fact]
    public void TranslatedShowsTranslationOnlyWithBadge()
    {
        var line = LineTestData.Make(1, LineState.Translated) with { TranslatedText = "hello" };
        Assert.Equal("2026.01.01 12:00:00 Pilot RU hello", LineLayout.Format(line));
    }

    [Theory]
    [InlineData("zh", "ZH")]
    [InlineData("zh-Hans", "ZH")]
    [InlineData("zh_TW", "ZH")]
    [InlineData("de", "DE")]
    public void BadgeIsTwoCapitalLetters(string code, string badge) =>
        Assert.Equal(badge, LineLayout.Badge(LineTestData.Make(1, LineState.Pending, code)));

    [Fact]
    public void BadgeComesBeforeMarker()
    {
        var parts = LineLayout.Parts(LineTestData.Make(1, LineState.Failed, "zh"));
        Assert.Equal("ZH", parts.Badge);
        Assert.Equal("✕", parts.Marker);
    }

    [Fact]
    public void MissingFieldsAreSkipped()
    {
        var line = LineTestData.Make(1, LineState.PassThrough) with { TimestampText = null, Speaker = null };
        Assert.Equal("original", LineLayout.Format(line));
    }

    [Fact]
    public void NoBadgeWithoutLanguage() =>
        Assert.Null(LineLayout.Badge(LineTestData.Make(1, LineState.Pending, null)));
}
