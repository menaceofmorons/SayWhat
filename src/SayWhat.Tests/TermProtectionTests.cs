using SayWhat.Core.Engines;
using SayWhat.Core.TermProtection;

namespace SayWhat.Tests;

public class TermProtectionTests
{
    private static string RoundTrip(string text)
    {
        var line = TermProtector.Protect(text, "en", "de");
        var result = TermProtector.Restore(line, line.Text);
        Assert.True(result.IsSuccess, result.Error?.Detail);
        return result.Text!;
    }

    [Theory]
    [InlineData("see https://example.com/a?b=1 now")]
    [InlineData("смотри www.example.org/путь 12 раз")]
    [InlineData("見て https://example.jp/ 3回")]
    [InlineData("هذا <url=showinfo:587//123>Rifter</url> في 5 قفزات")]
    [InlineData("check <a href=\"showinfo:587\">Rifter</a> and http://x.y/z 42")]
    [InlineData("no protected content at all")]
    [InlineData("1 2 3 and 10,000 isk")]
    [InlineData("")]
    public void IdentityTranslationReturnsOriginal(string text)
    {
        Assert.Equal(text, RoundTrip(text));
    }

    [Fact]
    public void UrlAndLinkAreReplacedByTokens()
    {
        var line = TermProtector.Protect("go https://a.b/c <url=x>Y</url> 7", "en", "de");

        Assert.Equal("go [[0]] [[1]] 7", line.Text);
        Assert.Equal(["https://a.b/c", "<url=x>Y</url>"], line.Items);
        Assert.Equal(["7"], line.DigitSequences);
    }

    [Fact]
    public void LineThatIsOnlyAUrl()
    {
        var line = TermProtector.Protect("https://a.b/c", "en", "de");

        Assert.Equal("[[0]]", line.Text);
        Assert.Equal("https://a.b/c", TermProtector.Restore(line, "[[0]]").Text);
    }

    [Fact]
    public void AdjacentTokensRestore()
    {
        var line = TermProtector.Protect("https://a.b <url=x>Y</url>", "en", "de");
        var result = TermProtector.Restore(line, "[[1]][[0]]");

        Assert.Equal("<url=x>Y</url>https://a.b", result.Text);
    }

    [Fact]
    public void ReorderedTokensRestoreByIndex()
    {
        var line = TermProtector.Protect("a https://one.x b https://two.x", "en", "de");
        var result = TermProtector.Restore(line, "b' [[1]] a' [[0]]");

        Assert.Equal("b' https://two.x a' https://one.x", result.Text);
    }

    [Theory]
    [InlineData("hallo [[ 0 ]] welt")]
    [InlineData("hallo [[0] welt")]
    [InlineData("hallo [0]] welt")]
    [InlineData("hallo [0] welt")]
    [InlineData("hallo [ [0] ] welt")]
    [InlineData("hallo [ 0 ] welt")]
    [InlineData("hallo 〚0〛 welt")]
    [InlineData("hallo [[  0]] welt")]
    public void DamagedTokenIsRepaired(string reply)
    {
        var line = TermProtector.Protect("hello https://a.b world", "en", "de");
        var result = TermProtector.Restore(line, reply);

        Assert.True(result.IsSuccess);
        Assert.Equal("hallo https://a.b welt", result.Text);
    }

    [Theory]
    [InlineData("hallo 0 welt")]
    [InlineData("hallo [[0) welt")]
    [InlineData("hallo [[ welt")]
    [InlineData("hallo [[x]] welt")]
    public void BareDigitOrUnrecognisableDamageIsBadResponse(string reply)
    {
        var line = TermProtector.Protect("hello https://a.b world", "en", "de");

        Assert.Equal(EngineErrorCategory.BadResponse, TermProtector.Restore(line, reply).Error!.Category);
    }

    [Theory]
    [InlineData("hallo [[0]] [0] welt")]
    [InlineData("hallo [0] [[0]] welt")]
    [InlineData("hallo [[0]] [[5]] welt")]
    public void DuplicatedOrUnknownDamagedIndexIsBadResponse(string reply)
    {
        var line = TermProtector.Protect("hello https://a.b world", "en", "de");

        Assert.False(TermProtector.Restore(line, reply).IsSuccess);
    }

    [Theory]
    [InlineData("pick [3] or [[7]] now")]
    [InlineData("pick [ 3 ] and [[ 7 ]] and [[7] 5")]
    public void RealTokenLookingTextIsProtectedAndRestoredVerbatim(string text)
    {
        var line = TermProtector.Protect(text, "en", "de");

        Assert.NotEmpty(line.Items);
        Assert.DoesNotContain("[3]", line.Text);
        Assert.Equal(text, TermProtector.Restore(line, line.Text).Text);
    }

    [Fact]
    public void MissingTokenIsBadResponse()
    {
        var line = TermProtector.Protect("hello https://a.b world", "en", "de");
        var result = TermProtector.Restore(line, "hallo welt");

        Assert.False(result.IsSuccess);
        Assert.Equal(EngineErrorCategory.BadResponse, result.Error!.Category);
    }

    [Fact]
    public void UnrecognisablyDamagedTokenIsBadResponse()
    {
        var line = TermProtector.Protect("hello https://a.b world", "en", "de");
        var result = TermProtector.Restore(line, "hallo X0X welt");

        Assert.Equal(EngineErrorCategory.BadResponse, result.Error!.Category);
    }

    [Fact]
    public void DuplicatedOrUnknownTokenIsBadResponse()
    {
        var line = TermProtector.Protect("hello https://a.b world", "en", "de");

        Assert.False(TermProtector.Restore(line, "[[0]] [[0]]").IsSuccess);
        Assert.False(TermProtector.Restore(line, "[[0]] [[5]]").IsSuccess);
    }

    [Fact]
    public void DigitsAreRestoredInOrderWhenCountMatches()
    {
        var line = TermProtector.Protect("3 jumps to 12", "en", "ar");
        var result = TermProtector.Restore(line, "٣ قفزات إلى ١٢");

        Assert.Equal("3 قفزات إلى 12", result.Text);
    }

    [Fact]
    public void DigitCountMismatchIsBadResponse()
    {
        var line = TermProtector.Protect("3 jumps to 12", "en", "de");

        Assert.Equal(EngineErrorCategory.BadResponse, TermProtector.Restore(line, "3 Sprünge").Error!.Category);
        Assert.Equal(EngineErrorCategory.BadResponse, TermProtector.Restore(line, "3 12 7 Sprünge").Error!.Category);
    }

    [Fact]
    public void UrlDigitsDoNotCountAsLineDigits()
    {
        var line = TermProtector.Protect("see https://a.b/123 for 5", "en", "de");

        Assert.Equal(["5"], line.DigitSequences);
        Assert.Equal("siehe https://a.b/123 für 5", TermProtector.Restore(line, "siehe [[0]] für 5").Text);
    }

    [Fact]
    public void TextAlreadyContainingTokenLikeCharactersIsPreserved()
    {
        var text = "weird [[0]] and [[ 7 ]] with https://a.b";
        var line = TermProtector.Protect(text, "en", "de");

        Assert.Equal(3, line.Items.Count);
        Assert.Equal(text, TermProtector.Restore(line, line.Text).Text);
    }

    [Fact]
    public void TokenFormIsOneConstant()
    {
        Assert.Equal("[[3]]", TermProtector.FormatToken(3));
        Assert.Equal(TermProtector.TokenOpen + "0" + TermProtector.TokenClose, TermProtector.Protect("www.a.b", "en", "de").Text);
    }
}
