using SayWhat.Core.LanguageDetection;

namespace SayWhat.Tests;

public class SetAsideTests
{
    [Theory]
    [InlineData("see https://example.com/a?b=1 now", "see now")]
    [InlineData("go to www.example.com please", "go to please")]
    [InlineData("look <url=showinfo:587//123>Rifter</url> here", "look here")]
    [InlineData("hi <a href=\"x\">link text</a> there", "hi there")]
    [InlineData("jump 12 times in 2024", "jump times in")]
    [InlineData("привет 123 мир", "привет мир")]
    public void RemovesNonLanguageText(string input, string expected)
    {
        var result = SetAside.Apply(input);

        Assert.Equal(expected, result.Text);
        Assert.False(result.HasNoLetters);
    }

    [Theory]
    [InlineData("Vargur primary", "primary")]
    [InlineData("vargur PRIMARY", "PRIMARY")]
    [InlineData("Rifter 리프터 ривтер go", "go")]
    [InlineData("リフター 攻撃", "攻撃")]
    public void RemovesShipNamesInAnyLanguageAndCase(string input, string expected)
    {
        var result = SetAside.Apply(input, ["Vargur", "Rifter", "리프터", "ривтер", "リフター"]);

        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void LongestShipNameWinsOverOverlappingShorterName()
    {
        var result = SetAside.Apply("Cerberus Navy Issue attack", ["Cerberus", "Cerberus Navy Issue"]);

        Assert.Equal("attack", result.Text);
    }

    [Fact]
    public void ShipNameInsideLongerWordIsKept()
    {
        var result = SetAside.Apply("Rifters and Arrifter", ["Rifter"]);

        Assert.Equal("Rifters and Arrifter", result.Text);
    }

    [Fact]
    public void ShipNameWithRegexCharactersIsMatchedLiterally()
    {
        var result = SetAside.Apply("the Hel. is here", ["Hel."]);

        Assert.Equal("the is here", result.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("https://example.com 42")]
    [InlineData("Vargur")]
    [InlineData("<url=showinfo:1>Vargur</url> 7")]
    public void LineWithNoLettersLeftIsFlagged(string? input)
    {
        var result = SetAside.Apply(input, ["Vargur"]);

        Assert.True(result.HasNoLetters);
        Assert.Equal(string.Empty, result.Text);
    }
}
