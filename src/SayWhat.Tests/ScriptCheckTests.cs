using SayWhat.Core.LanguageDetection;

namespace SayWhat.Tests;

public class ScriptCheckTests
{
    [Theory]
    [InlineData("안녕하세요 친구", TextScript.Hangul, "ko")]
    [InlineData("Γειά σου φίλε", TextScript.Greek, "el")]
    [InlineData("สวัสดีครับ", TextScript.Thai, "th")]
    [InlineData("こんにちは", TextScript.Kana, "ja")]
    [InlineData("カタカナ", TextScript.Kana, "ja")]
    [InlineData("今日はいい天気です", TextScript.Kana, "ja")]
    [InlineData("你好朋友", TextScript.Han, "zh")]
    [InlineData("한국어 친구 ok", TextScript.Hangul, "ko")]
    public void Check_DecidesLanguageDirectly(string text, TextScript script, string language)
    {
        var result = ScriptCheck.Check(text);

        Assert.Equal(ScriptCheckKind.Decided, result.Kind);
        Assert.Equal(script, result.DominantScript);
        Assert.Equal(language, result.LanguageCode);
    }

    [Theory]
    [InlineData("Привет друг", TextScript.Cyrillic)]
    [InlineData("مرحبا يا صديقي", TextScript.Arabic)]
    public void Check_SharedScript_NarrowsCandidates(string text, TextScript script)
    {
        var result = ScriptCheck.Check(text);

        Assert.Equal(ScriptCheckKind.SharedScript, result.Kind);
        Assert.Equal(script, result.DominantScript);
        Assert.Null(result.LanguageCode);
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("Vargur primary")]
    [InlineData("Bonjour, ça va très bien")]
    public void Check_Latin_LeavesAllCandidates(string text)
    {
        var result = ScriptCheck.Check(text);

        Assert.Equal(ScriptCheckKind.Latin, result.Kind);
        Assert.Equal(TextScript.Latin, result.DominantScript);
        Assert.Null(result.LanguageCode);
    }

    [Fact]
    public void Check_MixedScript_UsesMostLetters()
    {
        var result = ScriptCheck.Check("привет привет hi");

        Assert.Equal(TextScript.Cyrillic, result.DominantScript);
        Assert.Equal(ScriptCheckKind.SharedScript, result.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("  !? 42 ")]
    [InlineData(null)]
    public void Check_NoLetters_ReturnsNoLetters(string? text)
    {
        var result = ScriptCheck.Check(text);

        Assert.Equal(ScriptCheckKind.NoLetters, result.Kind);
        Assert.Equal(TextScript.None, result.DominantScript);
        Assert.Null(result.LanguageCode);
    }

    [Fact]
    public void Check_UnlistedScript_IsUndetermined()
    {
        var result = ScriptCheck.Check("नमस्ते दोस्त");

        Assert.Equal(ScriptCheckKind.Undetermined, result.Kind);
        Assert.Null(result.LanguageCode);
    }
}
