using System.Text.RegularExpressions;
using SayWhat.Core.LanguageDetection;

namespace SayWhat.Tests;

public class LinkMarkupFixtureTests
{
    private const string LogFolderVariable = "SAYWHAT_SCAN_LOCAL_LOGS";

    [Theory]
    [InlineData("<url=showinfo:587//90000001>Rifter</url> primary", "primary")]
    [InlineData("<URL=showinfo:587>Rifter</URL> primary", "primary")]
    [InlineData("<a href=\"https://example.com\">here</a> now", "now")]
    [InlineData("see <url=killReport:1:abc>report</url> and <url=showinfo:2>Vargur</url> end", "see and end")]
    [InlineData("<url=showinfo:587>two\nlines</url> after", "after")]
    public void WellFormedLinkMarkupIsSetAside(string input, string expected)
    {
        Assert.Equal(expected, SetAside.Apply(input).Text);
    }

    [Theory]
    [InlineData("<sigh i give up", "<sigh i give up")]
    [InlineData("a <b and c> d", "a <b and c> d")]
    [InlineData("<url=showinfo:x>unterminated link text", "<url=showinfo:x>unterminated link text")]
    public void AngleBracketChatTextWithoutLinkMarkupIsKept(string input, string expected)
    {
        Assert.Equal(expected, SetAside.Apply(input).Text);
    }

    [Fact]
    public void PlainTextUrlsAreSetAside()
    {
        Assert.Equal("go now", SetAside.Apply("go https://example.com/x?y=1 now").Text);
        Assert.Equal("go now", SetAside.Apply("go www.example.com now").Text);
    }

    [Fact]
    public void LiveLocalLogScanReportsMarkupShapeCountsOnly()
    {
        var folder = Environment.GetEnvironmentVariable(LogFolderVariable);
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder), "opt-in: set " + LogFolderVariable + " to the Chatlogs folder");

        var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var lines = 0;
        var files = 0;
        foreach (var path in Directory.EnumerateFiles(folder!, "Local_*.txt"))
        {
            files++;
            foreach (var line in File.ReadLines(path, System.Text.Encoding.Unicode))
            {
                var trimmed = line.TrimStart('﻿');
                var separator = trimmed.IndexOf(" > ", StringComparison.Ordinal);
                if (!trimmed.StartsWith('[') || separator < 0)
                {
                    continue;
                }

                lines++;
                foreach (Match tag in Regex.Matches(trimmed[(separator + 3)..], @"</?([A-Za-z]+)"))
                {
                    var shape = (tag.Value.StartsWith("</", StringComparison.Ordinal) ? "close:" : "open:") + tag.Groups[1].Value.ToLowerInvariant();
                    shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
                }
            }
        }

        TestContext.Current.SendDiagnosticMessage($"files={files} lines={lines} link-open={shapes.GetValueOrDefault("open:url") + shapes.GetValueOrDefault("open:a")}");
        Assert.True(files > 0);
    }
}
