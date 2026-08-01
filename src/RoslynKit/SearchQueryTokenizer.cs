namespace RoslynKit;

/// <summary>
/// Represents a normalized identifier and the deterministic terms used to search it.
/// </summary>
internal sealed record SearchTokenization(string NormalizedText, IReadOnlyList<string> Tokens);

/// <summary>
/// Normalizes English-oriented identifiers and natural-language search queries for full-text search.
/// </summary>
internal static class SearchQueryTokenizer
{
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "by",
        "did",
        "do",
        "does",
        "for",
        "from",
        "how",
        "in",
        "is",
        "it",
        "of",
        "on",
        "or",
        "that",
        "the",
        "to",
        "what",
        "when",
        "where",
        "which",
        "with",
    };

    public static SearchTokenization TokenizeIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var normalizedText = NormalizeIdentifier(identifier);
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        AddToken(normalizedText, tokens, seen);

        foreach (var part in EnumerateIdentifierParts(identifier))
        {
            AddToken(part, tokens, seen);
        }

        return new SearchTokenization(normalizedText, Array.AsReadOnly(tokens.ToArray()));
    }

    public static IReadOnlyList<string> TokenizeQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var term in EnumerateQueryTerms(query))
        {
            var normalizedTerm = NormalizeIdentifier(term);
            if (normalizedTerm.Length == 0 || FillerWords.Contains(normalizedTerm))
            {
                continue;
            }

            var tokenization = TokenizeIdentifier(term);
            foreach (var token in tokenization.Tokens)
            {
                AddToken(token, tokens, seen);
            }
        }

        return Array.AsReadOnly(tokens.ToArray());
    }

    private static void AddToken(string token, List<string> tokens, HashSet<string> seen)
    {
        if (token.Length > 0 && seen.Add(token))
        {
            tokens.Add(token);
        }
    }

    private static IEnumerable<string> EnumerateIdentifierParts(string identifier)
    {
        var runStart = -1;
        for (var index = 0; index <= identifier.Length; index++)
        {
            if (index < identifier.Length && IsEnglishLetterOrDigit(identifier[index]))
            {
                if (runStart < 0)
                {
                    runStart = index;
                }

                continue;
            }

            if (runStart >= 0)
            {
                foreach (var part in SplitIdentifierRun(identifier.AsSpan(runStart, index - runStart)))
                {
                    yield return part;
                }

                runStart = -1;
            }
        }
    }

    private static IEnumerable<string> EnumerateQueryTerms(string query)
    {
        var termStart = -1;
        for (var index = 0; index <= query.Length; index++)
        {
            if (index < query.Length && (IsEnglishLetterOrDigit(query[index]) || query[index] == '_'))
            {
                if (termStart < 0)
                {
                    termStart = index;
                }

                continue;
            }

            if (termStart >= 0)
            {
                yield return query[termStart..index];
                termStart = -1;
            }
        }
    }

    private static IReadOnlyList<string> SplitIdentifierRun(ReadOnlySpan<char> run)
    {
        var parts = new List<string>();
        var partStart = 0;
        for (var index = 1; index < run.Length; index++)
        {
            if (StartsNewPart(run, index))
            {
                parts.Add(NormalizeIdentifier(run[partStart..index]));
                partStart = index;
            }
        }

        parts.Add(NormalizeIdentifier(run[partStart..]));
        return parts;
    }

    private static bool StartsNewPart(ReadOnlySpan<char> value, int index)
    {
        var previous = value[index - 1];
        var current = value[index];

        if (char.IsDigit(previous) != char.IsDigit(current))
        {
            return true;
        }

        if (char.IsLower(previous) && char.IsUpper(current))
        {
            return true;
        }

        return char.IsUpper(previous)
            && char.IsUpper(current)
            && index + 1 < value.Length
            && char.IsLower(value[index + 1]);
    }

    private static string NormalizeIdentifier(ReadOnlySpan<char> value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (IsEnglishLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool IsEnglishLetterOrDigit(char value)
    {
        return value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';
    }
}
