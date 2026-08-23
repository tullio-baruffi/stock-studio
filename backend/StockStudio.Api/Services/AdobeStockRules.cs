namespace StockStudio.Api.Services;

/// <summary>
/// The vocabularies and fixes that come straight from the Adobe Stock "Guide to Mastering
/// Metadata" (v2, August 2021). Shared so the validator that reports a breach and the generator
/// that must avoid it can never drift apart.
///
/// Written as code rather than left to the prompt on purpose: models follow a written ban most of
/// the time, and "most of the time" still pushes rejected terms onto the marketplaces.
/// </summary>
public static class AdobeStockRules
{
    /// <summary>"Adjectives should be descriptive, not subjective" — guide, page 2.</summary>
    public static readonly HashSet<string> SubjectiveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cute", "beautiful", "sensual", "gorgeous", "stunning", "lovely", "pretty", "amazing",
        "awesome", "perfect", "wonderful", "adorable", "sexy", "charming", "delightful", "exquisite",
        "breathtaking", "magnificent", "fabulous", "trendy", "stylish", "cool", "nice", "best",
        "graceful", "elegant", "majestic", "serene", "dramatic", "captivating", "striking",
        "impressive", "enchanting", "mesmerizing", "vibrant", "lively", "cheerful", "joyful",
    };

    /// <summary>Terms implying a human figure, used to catch a false "no people" claim.</summary>
    public static readonly string[] PeopleWords =
    {
        "people", "person", "man", "woman", "men", "women", "child", "children", "kid", "boy", "girl",
        "dancer", "dancers", "athlete", "runner", "worker", "couple", "family", "crowd", "human",
        "businessman", "businesswoman", "yoga", "ballerina", "musician", "singer", "player",
        "portrait", "face", "baby", "lady", "gentleman", "figure skater", "dancing",
    };

    /// <summary>Words ending in "s" that are not plurals, so the singular hint stays quiet.</summary>
    public static readonly HashSet<string> NotPlural = new(StringComparer.OrdinalIgnoreCase)
    {
        "christmas", "series", "species", "news", "chess", "fitness", "wellness", "happiness",
        "darkness", "business", "witness", "canvas", "atlas", "compass", "glass", "grass", "cross",
        "dress", "press", "kiss", "class", "bass", "brass", "moss", "boss", "loss", "gas", "bus",
        "virus", "focus", "campus", "circus", "cactus", "lotus", "iris", "axis", "oasis", "tennis",
        "physics", "mathematics", "graphics", "electronics", "headphones", "scissors", "jeans",
        "glasses", "sunglasses", "pants", "shorts", "clothes", "stairs",
        // Standard compound nouns whose plural form is the canonical term.
        "arts", "sports", "athletics", "acoustics", "logistics", "economics", "politics",
    };

    private static readonly string[] NoPeopleClaims = { "no people", "nobody" };

    /// <summary>True when the keywords or title name a human figure.</summary>
    public static bool MentionsPeople(IEnumerable<string> keywords, string title)
    {
        var haystack = string.Join(" ", keywords.Select(k => k.ToLowerInvariant())
                                                .Where(k => !NoPeopleClaims.Contains(k)))
                       + " " + (title ?? "").ToLowerInvariant();
        return PeopleWords.Any(p => System.Text.RegularExpressions.Regex.IsMatch(
            haystack, $@"\b{System.Text.RegularExpressions.Regex.Escape(p)}\b"));
    }

    /// <summary>
    /// Brings a freshly generated keyword list in line with the guide: drops subjective adjectives,
    /// removes a "no people" claim contradicted by the subject, and guarantees the medium keywords
    /// a vector must carry.
    /// </summary>
    public static List<string> Enforce(IEnumerable<string> keywords, string title, string? mode, int maxKeywords)
    {
        var list = keywords
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        list.RemoveAll(k => k.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(SubjectiveWords.Contains));

        if (MentionsPeople(list, title))
            list.RemoveAll(k => NoPeopleClaims.Contains(k, StringComparer.OrdinalIgnoreCase));

        if (string.Equals(mode, "vector", StringComparison.OrdinalIgnoreCase))
        {
            // Inserted near the front: the guide weighs the first ten keywords most.
            foreach (var required in new[] { "graphic", "vector" })
                if (!list.Contains(required, StringComparer.OrdinalIgnoreCase))
                    list.Insert(Math.Min(list.Count, 3), required);
        }

        return list.Take(Math.Clamp(maxKeywords, 10, 49)).ToList();
    }
}
