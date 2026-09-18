using System.Globalization;
using Windows.Data.Text;

namespace NeteaseLyricsOverlay;

internal readonly record struct KeywordSpan(int Start, int Length, string Text);
internal readonly record struct LyricAnimationUnit(string Text, bool IsHighlighted);

internal static class KeywordSelector
{
    private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "that", "this", "with", "from", "into", "your", "you", "our", "are", "was",
        "were", "have", "has", "had", "for", "not", "but", "all", "can", "will", "just", "then",
        "than", "when", "where", "what", "who", "why", "how", "its", "it's", "i'm", "i'll", "i've",
        "we're", "we'll", "don't", "can't", "won't", "me", "my", "his", "her", "their", "there"
    };

    private static readonly HashSet<string> ChineseStopWords = new(StringComparer.Ordinal)
    {
        "我们", "你们", "他们", "自己", "这个", "那个", "这里", "那里", "已经", "还是", "只是",
        "因为", "所以", "如果", "但是", "然后", "什么", "怎么", "没有", "不是", "可以", "一切"
    };

    private static readonly HashSet<string> JapaneseKatakanaStopWords = new(StringComparer.Ordinal)
    {
        "コレ", "ソレ", "アレ", "ココ", "ソコ", "ダカラ", "ケレド", "ダケド", "スル",
        "イル", "アル", "ナル", "ナイ", "デス", "マス", "カラ", "マデ", "ヨリ", "コト", "モノ"
    };

    private static readonly HashSet<string> JapaneseParticleOnlyRuns = new(StringComparer.Ordinal)
    {
        "は", "が", "を", "に", "へ", "と", "も", "の", "で", "や", "か", "ね", "よ", "ぞ", "さ",
        "から", "まで", "より", "には", "では", "とは", "へと", "でも", "しか", "だけ", "ほど", "って",
        "です", "でした", "だ", "だった", "じゃない"
    };

    private static readonly HashSet<string> MeaningfulSingleHan = new(StringComparer.Ordinal)
    {
        "爱", "愛", "梦", "夢", "光", "夜", "心", "火", "风", "風", "雨", "雪", "海", "空",
        "花", "月", "星", "神", "命", "血", "罪", "歌", "声", "聲", "泪", "涙", "道", "影"
    };

    public static IReadOnlyDictionary<int, IReadOnlyList<KeywordSpan>> Select(
        IReadOnlyList<string> lines,
        string stableSeed)
    {
        var candidatesByLine = new List<List<Candidate>>(lines.Count);
        var frequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            var candidates = ExtractCandidates(lines[i], i);
            candidatesByLine.Add(candidates);
            foreach (var candidate in candidates
                         .GroupBy(candidate => candidate.Normalized, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                frequencies[candidate.Normalized] =
                    frequencies.GetValueOrDefault(candidate.Normalized) + 1;
            }
        }

        var result = new Dictionary<int, IReadOnlyList<KeywordSpan>>();
        var recentlyUsed = new Queue<HashSet<string>>();
        var start = 0;
        var groupNumber = 0;
        while (start < lines.Count)
        {
            var size = ChooseGroupSize(lines.Count - start, stableSeed, groupNumber);
            var groupCandidates = candidatesByLine
                .Skip(start)
                .Take(size)
                .SelectMany(list => list)
                .ToList();
            var recentTerms = recentlyUsed.SelectMany(set => set)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selected = SelectTwo(groupCandidates, frequencies, recentTerms, stableSeed, groupNumber);
            foreach (var lineGroup in selected.GroupBy(candidate => candidate.LineIndex))
            {
                result[lineGroup.Key] = lineGroup
                    .OrderBy(candidate => candidate.Start)
                    .Select(candidate => new KeywordSpan(candidate.Start, candidate.Length, candidate.Text))
                    .ToArray();
            }
            recentlyUsed.Enqueue(selected.Select(candidate => candidate.Normalized)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
            while (recentlyUsed.Count > 2) recentlyUsed.Dequeue();
            start += size;
            groupNumber++;
        }
        return result;
    }

    public static IReadOnlyList<LyricAnimationUnit> BuildAnimationUnits(
        string text,
        IReadOnlyList<KeywordSpan>? highlights)
    {
        if (highlights is null || highlights.Count == 0)
            return EnumerateTextElements(text)
                .Select(element => new LyricAnimationUnit(element, false))
                .ToArray();

        var result = new List<LyricAnimationUnit>();
        var cursor = 0;
        foreach (var span in highlights.OrderBy(span => span.Start))
        {
            if (span.Start < cursor || span.Start < 0 || span.Length <= 0 ||
                span.Start + span.Length > text.Length) continue;
            AddNormalUnits(result, text[cursor..span.Start]);
            result.Add(new LyricAnimationUnit(text.Substring(span.Start, span.Length), true));
            cursor = span.Start + span.Length;
        }
        AddNormalUnits(result, text[cursor..]);
        return result;
    }

    private static List<Candidate> ExtractCandidates(string text, int lineIndex)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var language = DetectLanguage(text);
        try
        {
            var tag = language switch
            {
                LyricLanguage.Japanese => "ja-JP",
                LyricLanguage.Chinese => "zh-CN",
                _ => "en-US"
            };
            var tokens = new WordsSegmenter(tag).GetTokens(text);
            var result = new List<Candidate>();
            foreach (var token in tokens)
            {
                var start = checked((int)token.SourceTextSegment.StartPosition);
                if (language == LyricLanguage.Japanese)
                    AddJapaneseRuns(result, token.Text, start, lineIndex);
                else if (language == LyricLanguage.Chinese)
                    AddChineseToken(result, token.Text, start, lineIndex);
                else
                    AddEnglishToken(result, token.Text, start, lineIndex);
            }
            if (language == LyricLanguage.Japanese)
                AddJapaneseVerbStemKanji(result, text, lineIndex);
            return result
                .GroupBy(candidate => (candidate.Start, candidate.Length))
                .Select(group => group.MaxBy(candidate => candidate.BaseScore)!)
                .ToList();
        }
        catch
        {
            return ExtractFallbackCandidates(text, lineIndex, language);
        }
    }

    private static List<Candidate> ExtractFallbackCandidates(
        string text,
        int lineIndex,
        LyricLanguage language)
    {
        var result = new List<Candidate>();
        if (language == LyricLanguage.Japanese)
        {
            AddJapaneseRuns(result, text, 0, lineIndex);
            AddJapaneseVerbStemKanji(result, text, lineIndex);
            return result
                .GroupBy(candidate => (candidate.Start, candidate.Length))
                .Select(group => group.MaxBy(candidate => candidate.BaseScore)!)
                .ToList();
        }
        if (language == LyricLanguage.Chinese)
        {
            var start = 0;
            while (start < text.Length)
            {
                while (start < text.Length && !IsHan(text[start])) start++;
                var end = start;
                while (end < text.Length && IsHan(text[end])) end++;
                for (var offset = start; offset + 1 < end; offset += 2)
                {
                    var length = Math.Min(4, end - offset);
                    AddChineseToken(result, text.Substring(offset, length), offset, lineIndex);
                }
                start = Math.Max(end, start + 1);
            }
            return result;
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            while (cursor < text.Length && !IsLatinLetter(text[cursor])) cursor++;
            var start = cursor;
            while (cursor < text.Length &&
                   (IsLatinLetter(text[cursor]) || text[cursor] is '\'' or '-')) cursor++;
            if (cursor > start) AddEnglishToken(result, text[start..cursor], start, lineIndex);
        }
        return result;
    }

    private static void AddEnglishToken(List<Candidate> result, string value, int start, int lineIndex)
    {
        var leading = 0;
        while (leading < value.Length && !IsLatinLetter(value[leading])) leading++;
        var trailing = value.Length;
        while (trailing > leading && !IsLatinLetter(value[trailing - 1])) trailing--;
        if (trailing - leading < 3) return;
        var word = value[leading..trailing];
        if (!word.Any(IsLatinLetter) || EnglishStopWords.Contains(word)) return;
        result.Add(NewCandidate(lineIndex, start + leading, word, 1.0 + Math.Min(word.Length, 10) * 0.12));
    }

    private static void AddChineseToken(List<Candidate> result, string value, int start, int lineIndex)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.All(IsHan)) return;
        if (value.Length == 1 && !MeaningfulSingleHan.Contains(value)) return;
        if (value.Length > 6 || ChineseStopWords.Contains(value)) return;
        result.Add(NewCandidate(lineIndex, start, value, 1.3 + Math.Min(value.Length, 4) * 0.28));
    }

    private static void AddJapaneseRuns(List<Candidate> result, string value, int tokenStart, int lineIndex)
    {
        var cursor = 0;
        while (cursor < value.Length)
        {
            var kind = IsHan(value[cursor]) ? 1 : IsKatakanaRunCharacter(value[cursor]) ? 2 : 0;
            if (kind == 0)
            {
                cursor++;
                continue;
            }
            var start = cursor;
            while (cursor < value.Length &&
                   (kind == 1 ? IsHan(value[cursor]) : IsKatakanaRunCharacter(value[cursor]))) cursor++;
            var run = value[start..cursor];
            if (kind == 1)
            {
                if (run.Length <= 6 && (run.Length > 1 || MeaningfulSingleHan.Contains(run)))
                    result.Add(NewCandidate(lineIndex, tokenStart + start, run,
                        1.5 + Math.Min(run.Length, 4) * 0.3));
            }
            else if (run.Length is >= 2 and <= 12 && !JapaneseKatakanaStopWords.Contains(run))
            {
                result.Add(NewCandidate(lineIndex, tokenStart + start, run,
                    1.1 + Math.Min(run.Length, 6) * 0.16));
            }
        }
    }

    private static void AddJapaneseVerbStemKanji(
        List<Candidate> result,
        string text,
        int lineIndex)
    {
        for (var index = 0; index + 1 < text.Length; index++)
        {
            if (!IsHan(text[index]) || !IsHiragana(text[index + 1])) continue;
            if (index > 0 && IsHan(text[index - 1])) continue;

            var suffixEnd = index + 1;
            while (suffixEnd < text.Length && IsHiragana(text[suffixEnd])) suffixEnd++;
            var hiraganaSuffix = text[(index + 1)..suffixEnd];
            if (JapaneseParticleOnlyRuns.Contains(hiraganaSuffix)) continue;

            var stem = text[index].ToString();
            result.Add(NewCandidate(lineIndex, index, stem, 2.25));
        }
    }

    private static Candidate NewCandidate(
        int lineIndex,
        int start,
        string text,
        double baseScore) =>
        new(lineIndex, start, text.Length, text, text.ToLowerInvariant(), baseScore);

    private static IReadOnlyList<Candidate> SelectTwo(
        List<Candidate> candidates,
        IReadOnlyDictionary<string, int> frequencies,
        IReadOnlySet<string> recentTerms,
        string stableSeed,
        int groupNumber)
    {
        if (candidates.Count == 0) return [];
        double Score(Candidate candidate, Candidate? first = null)
        {
            var frequencyBoost = Math.Min(frequencies.GetValueOrDefault(candidate.Normalized), 4) * 0.32;
            var recentPenalty = recentTerms.Contains(candidate.Normalized) ? 1.15 : 0;
            var differentLineBoost = first is not null && first.LineIndex != candidate.LineIndex ? 0.55 : 0;
            var distinctTermBoost = first is not null &&
                                    !string.Equals(first.Normalized, candidate.Normalized,
                                        StringComparison.OrdinalIgnoreCase) ? 0.4 : 0;
            var jitter = StableHash($"{stableSeed}|{groupNumber}|{candidate.LineIndex}|{candidate.Start}") % 100 / 500.0;
            return candidate.BaseScore + frequencyBoost + differentLineBoost + distinctTermBoost + jitter - recentPenalty;
        }

        var first = candidates.OrderByDescending(candidate => Score(candidate)).First();
        var second = candidates
            .Where(candidate => candidate != first &&
                                !string.Equals(candidate.Normalized, first.Normalized,
                                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => Score(candidate, first))
            .FirstOrDefault();
        return second is null ? [first] : [first, second];
    }

    private static int ChooseGroupSize(int remaining, string seed, int groupNumber)
    {
        if (remaining <= 5) return remaining;
        var feasible = Enumerable.Range(3, 3)
            .Where(size => remaining - size == 0 || remaining - size >= 3)
            .ToArray();
        return feasible[StableHash($"{seed}|group|{groupNumber}") % feasible.Length];
    }

    private static LyricLanguage DetectLanguage(string text)
    {
        var kana = text.Count(character => IsHiragana(character) || IsKatakana(character));
        if (kana > 0) return LyricLanguage.Japanese;
        return text.Any(IsHan) ? LyricLanguage.Chinese : LyricLanguage.English;
    }

    private static void AddNormalUnits(List<LyricAnimationUnit> target, string text)
    {
        foreach (var element in EnumerateTextElements(text))
            target.Add(new LyricAnimationUnit(element, false));
    }

    private static List<string> EnumerateTextElements(string text)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) result.Add(enumerator.GetTextElement());
        return result;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619;
            }
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    private static bool IsLatinLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsHan(char value) =>
        value is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or
            >= '\uF900' and <= '\uFAFF' or '\u3005';

    private static bool IsHiragana(char value) => value is >= '\u3041' and <= '\u3096';

    private static bool IsKatakana(char value) =>
        value is >= '\u30A1' and <= '\u30FA' or >= '\u30FD' and <= '\u30FF' or
            >= '\u31F0' and <= '\u31FF' or >= '\uFF66' and <= '\uFF9D';

    private static bool IsKatakanaRunCharacter(char value) =>
        IsKatakana(value) || value is '\u30FC' or '\uFF70';

    private enum LyricLanguage
    {
        Chinese,
        Japanese,
        English
    }

    private sealed record Candidate(
        int LineIndex,
        int Start,
        int Length,
        string Text,
        string Normalized,
        double BaseScore);
}
