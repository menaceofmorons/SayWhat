using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using SayWhat.Core.LanguageDetection;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Core.TermProtection;

internal static class NameProtection
{
    internal const int MinimumUnspacedLength = 4;

    private static readonly ConditionalWeakTable<ReferenceDatabase, IndexHolder> Indexes = new();

    private static readonly Regex Token = new(Regex.Escape(TermProtector.TokenOpen) + @"\d+" + Regex.Escape(TermProtector.TokenClose), RegexOptions.CultureInvariant);

    public static string Apply(string text, string sourceLanguage, string targetLanguage, ReferenceDatabase reference, string shipNameLanguage, Func<string, string> addItem, int minimumUnspacedLength = MinimumUnspacedLength)
    {
        if (string.IsNullOrEmpty(text) || !reference.Exists)
        {
            return text;
        }

        var index = Indexes.GetValue(reference, _ => new IndexHolder()).Current(reference);
        var withShips = ReplaceShips(text, sourceLanguage, targetLanguage, shipNameLanguage, index, addItem);
        return ReplaceSystems(withShips, index, addItem, minimumUnspacedLength);
    }

    private static string ReplaceShips(string text, string sourceLanguage, string targetLanguage, string shipNameLanguage, NameIndex index, Func<string, string> addItem)
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
                ? index.ShipName(typeId, outputLanguage)
                : null;
            return addItem(replacement ?? matched);
        });
    }

    private static string ReplaceSystems(string text, NameIndex index, Func<string, string> addItem, int minimumUnspacedLength)
    {
        var names = index.SystemNames;
        if (names.Count == 0)
        {
            return text;
        }

        var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
        var koLookup = index.KoreanSystemNames.GetAlternateLookup<ReadOnlySpan<char>>();
        var longest = index.LongestSystemName;
        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var token in Token.Matches(text).Cast<Match>())
        {
            ScanSegment(text.AsSpan(position, token.Index - position), lookup, koLookup, longest, minimumUnspacedLength, addItem, builder);
            builder.Append(token.Value);
            position = token.Index + token.Length;
        }

        ScanSegment(text.AsSpan(position), lookup, koLookup, longest, minimumUnspacedLength, addItem, builder);

        return builder.ToString();
    }

    private static void ScanSegment(ReadOnlySpan<char> segment, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> koLookup, int longest, int minimumUnspacedLength, Func<string, string> addItem, StringBuilder builder)
    {
        var i = 0;
        while (i < segment.Length)
        {
            var length = LongestMatchAt(segment, i, lookup, koLookup, longest, minimumUnspacedLength);
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

    private static int LongestMatchAt(ReadOnlySpan<char> segment, int start, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> koLookup, int longest, int minimumUnspacedLength)
    {
        for (var length = Math.Min(longest, segment.Length - start); length > 0; length--)
        {
            var end = start + length;
            Func<char, bool> blocks = length < minimumUnspacedLength ? IsWordChar : IsBoundaryBlocker;
            if (start > 0 && blocks(segment[start - 1]))
            {
                continue;
            }

            var candidate = segment.Slice(start, length);
            if (end < segment.Length && blocks(segment[end]))
            {
                var koreanParticle = length >= minimumUnspacedLength && IsHangul(segment[end]);
                if (!koreanParticle || !koLookup.Contains(candidate))
                {
                    continue;
                }
            }

            if (lookup.Contains(candidate))
            {
                return length;
            }
        }

        return 0;
    }

    private static bool IsBoundaryBlocker(char c) => IsWordChar(c) && !IsUnspacedScript(c);

    private static bool IsHangul(char c) => c is >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' or >= '가' and <= '힣';

    private static bool IsUnspacedScript(char c) => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿';

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c)
        || c == '_'
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private sealed record ShipIndex(ShipNameMatcher Matcher, Dictionary<string, int> TypeIds);

    private sealed class IndexHolder
    {
        private readonly object gate = new();
        private (DateTime WriteTime, long Length)? stamp;
        private NameIndex? index;

        public NameIndex Current(ReferenceDatabase reference)
        {
            var file = new FileInfo(reference.Path);
            var current = (file.LastWriteTimeUtc, file.Length);
            lock (gate)
            {
                if (index is null || stamp != current)
                {
                    index = new NameIndex(reference);
                    stamp = current;
                }

                return index;
            }
        }
    }

    private sealed class NameIndex
    {
        private readonly ReferenceDatabase reference;
        private readonly ConcurrentDictionary<string, ShipIndex?> ships = new();
        private readonly ConcurrentDictionary<string, Dictionary<int, string>> shipNamesByType = new();
        private readonly Lazy<IReadOnlyList<string>> clientLanguages;
        private readonly Lazy<HashSet<string>> systemNames;
        private readonly Lazy<int> longestSystemName;
        private readonly Lazy<HashSet<string>> koreanSystemNames;

        public NameIndex(ReferenceDatabase reference)
        {
            this.reference = reference;
            clientLanguages = new Lazy<IReadOnlyList<string>>(reference.ClientLanguages);
            systemNames = new Lazy<HashSet<string>>(BuildSystemNames);
            koreanSystemNames = new Lazy<HashSet<string>>(BuildKoreanSystemNames);
            longestSystemName = new Lazy<int>(() => SystemNames.Count == 0 ? 0 : SystemNames.Max(name => name.Length));
        }

        public HashSet<string> SystemNames => systemNames.Value;

        public HashSet<string> KoreanSystemNames => koreanSystemNames.Value;

        public int LongestSystemName => longestSystemName.Value;

        public bool IsClientLanguage(string language) => clientLanguages.Value.Contains(language, StringComparer.Ordinal);

        public ShipIndex? Ships(string language) => ships.GetOrAdd(language, BuildShips);

        public string? ShipName(int typeId, string language) =>
            shipNamesByType.GetOrAdd(language, BuildShipNamesByType).GetValueOrDefault(typeId);

        private Dictionary<int, string> BuildShipNamesByType(string language)
        {
            var names = new Dictionary<int, string>();
            foreach (var entry in reference.ShipNames(language))
            {
                names.TryAdd(entry.TypeId, entry.Name);
            }

            return names;
        }

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
            return new ShipIndex(new ShipNameMatcher(entries.Select(entry => entry.Name), spacedBoundaryOnly, hangulMayFollow: language == "ko"), typeIds);
        }

        private HashSet<string> BuildKoreanSystemNames() =>
            clientLanguages.Value.Contains("ko", StringComparer.Ordinal)
                ? reference.SystemNames("ko").Select(name => name.Trim()).Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
