using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SayWhat.Core.LanguageDetection;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Core.TermProtection;

internal static class NameProtection
{
    private static readonly ConditionalWeakTable<ReferenceDatabase, NameIndex> Indexes = new();

    public static string Apply(string text, string sourceLanguage, string targetLanguage, ReferenceDatabase reference, string shipNameLanguage, Func<string, string> addItem)
    {
        if (string.IsNullOrEmpty(text) || !reference.Exists)
        {
            return text;
        }

        var index = Indexes.GetValue(reference, database => new NameIndex(database));
        var withShips = ReplaceShips(text, sourceLanguage, targetLanguage, shipNameLanguage, reference, index, addItem);
        return ReplaceSystems(withShips, index, addItem);
    }

    private static string ReplaceShips(string text, string sourceLanguage, string targetLanguage, string shipNameLanguage, ReferenceDatabase reference, NameIndex index, Func<string, string> addItem)
    {
        var ships = index.Ships(sourceLanguage);
        if (ships is null)
        {
            return text;
        }

        var outputLanguage = index.IsClientLanguage(targetLanguage) ? targetLanguage : shipNameLanguage;
        return ships.Matcher.Replace(text, matched =>
        {
            var replacement = ships.TypeIds.TryGetValue(NameNormaliser.Normalise(matched), out var typeId)
                ? reference.ShipName(typeId, outputLanguage)
                : null;
            return addItem(replacement ?? matched);
        });
    }

    private static string ReplaceSystems(string text, NameIndex index, Func<string, string> addItem)
    {
        var names = index.SystemNames;
        if (names.Count == 0)
        {
            return text;
        }

        var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
        var longest = index.LongestSystemName;
        var builder = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf(TermProtector.TokenOpen, position, StringComparison.Ordinal);
            var segmentEnd = open < 0 ? text.Length : open;
            ScanSegment(text.AsSpan(position, segmentEnd - position), lookup, longest, addItem, builder);
            if (open < 0)
            {
                break;
            }

            var close = text.IndexOf(TermProtector.TokenClose, open, StringComparison.Ordinal);
            var end = close < 0 ? text.Length : close + TermProtector.TokenClose.Length;
            builder.Append(text, open, end - open);
            position = end;
        }

        return builder.ToString();
    }

    private static void ScanSegment(ReadOnlySpan<char> segment, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup, int longest, Func<string, string> addItem, StringBuilder builder)
    {
        var i = 0;
        while (i < segment.Length)
        {
            var length = i > 0 && IsWordChar(segment[i - 1]) ? 0 : LongestMatchAt(segment, i, lookup, longest);
            if (length > 0)
            {
                builder.Append(addItem(segment.Slice(i, length).ToString()));
                i += length;
            }
            else
            {
                builder.Append(segment[i]);
                i++;
            }
        }
    }

    private static int LongestMatchAt(ReadOnlySpan<char> segment, int start, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup, int longest)
    {
        for (var length = Math.Min(longest, segment.Length - start); length > 0; length--)
        {
            var end = start + length;
            if (end < segment.Length && IsWordChar(segment[end]))
            {
                continue;
            }

            if (lookup.Contains(segment.Slice(start, length)))
            {
                return length;
            }
        }

        return 0;
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c)
        || c == '_'
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private sealed record ShipIndex(ShipNameMatcher Matcher, Dictionary<string, int> TypeIds);

    private sealed class NameIndex
    {
        private readonly ReferenceDatabase reference;
        private readonly ConcurrentDictionary<string, ShipIndex?> ships = new();
        private readonly Lazy<IReadOnlyList<string>> clientLanguages;
        private readonly Lazy<HashSet<string>> systemNames;

        public NameIndex(ReferenceDatabase reference)
        {
            this.reference = reference;
            clientLanguages = new Lazy<IReadOnlyList<string>>(reference.ClientLanguages);
            systemNames = new Lazy<HashSet<string>>(BuildSystemNames);
        }

        public HashSet<string> SystemNames => systemNames.Value;

        public int LongestSystemName => SystemNames.Count == 0 ? 0 : SystemNames.Max(name => name.Length);

        public bool IsClientLanguage(string language) => clientLanguages.Value.Contains(language, StringComparer.Ordinal);

        public ShipIndex? Ships(string language) => ships.GetOrAdd(language, BuildShips);

        private ShipIndex? BuildShips(string language)
        {
            var entries = reference.ShipNames(language);
            if (entries.Count == 0)
            {
                return null;
            }

            var typeIds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var key = NameNormaliser.Normalise(entry.Name);
                if (!typeIds.TryGetValue(key, out var existing) || entry.TypeId < existing)
                {
                    typeIds[key] = entry.TypeId;
                }
            }

            var spacedBoundaryOnly = language is "ja" or "zh";
            return new ShipIndex(new ShipNameMatcher(entries.Select(entry => entry.Name), spacedBoundaryOnly), typeIds);
        }

        private HashSet<string> BuildSystemNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var language in clientLanguages.Value)
            {
                foreach (var name in reference.SystemNames(language))
                {
                    var trimmed = name.Trim();
                    if (trimmed.Length > 0)
                    {
                        names.Add(trimmed);
                    }
                }
            }

            return names;
        }
    }
}
