using System.Text.RegularExpressions;

namespace StockStudio.Api.Services;

public record ValidationIssue(string Severity, string Field, string Message, string? Site = null);

public record SiteScore(string Site, int Score);

public record ValidationResult(
    int Score,
    bool BlocksDispatch,
    IReadOnlyList<SiteScore> Sites,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Validates title / description / keywords against Adobe Stock and Freepik rules — both to avoid
/// rejection (hard errors) and to improve discoverability/ranking in each site's search engine
/// (warnings + SEO hints). Produces a 0-100 score and actionable issues. Pure/offline, no I/O.
///
/// The rules follow the Adobe Stock "Guide to Mastering Metadata" (v2, updated August 2021):
/// https://adobestock.adobe.com/Metadata-Field-Guide.html
/// </summary>
public class StockValidator
{
    // Common trademark / brand terms that get stock submissions rejected. Not exhaustive.
    private static readonly string[] Trademarks =
    {
        "nike", "adidas", "disney", "marvel", "pixar", "coca-cola", "coca cola", "pepsi", "apple",
        "iphone", "ipad", "android", "google", "facebook", "instagram", "tiktok", "youtube", "twitter",
        "lego", "barbie", "ferrari", "lamborghini", "gucci", "prada", "chanel", "louis vuitton",
        "starbucks", "mcdonald", "rolex", "playstation", "xbox", "nintendo", "pokemon", "star wars",
        "batman", "superman", "spiderman", "harry potter", "olympics", "fifa", "uefa", "nba", "nfl",
    };

    // Words too generic to count as useful keywords (SEO noise), plus the function words the guide
    // itself bans from keywords: they must never be demanded back by the title-coverage check.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "with", "for", "on", "in", "to", "image", "picture", "photo", "file",
        "beside", "above", "below", "under", "over", "near", "behind", "between", "against", "through",
        "into", "onto", "upon", "within", "without", "across", "along", "around", "before", "after",
        "during", "from", "that", "this", "these", "those", "its", "his", "her", "their",
    };




    private const int TitleMinChars = 15;
    private const int TitleIdealChars = 70;   // guide: "70 characters is ideal, although the system allows up to 200"
    private const int TitleIptcChars = 64;    // hard limit of the IPTC IIM "Object Name" (title) field
    private const int TitleMaxChars = 200;
    private const int TitleMinWords = 3;
    private const int KeywordsMin = 10;       // below this, discoverability suffers
    private const int KeywordsIdealMin = 15;  // guide: "The ideal number of keywords is 15 to 35"
    private const int KeywordsIdealMax = 35;
    private const int KeywordsMax = 49;       // guide: "please keep it under 50"

    /// <param name="mode">"vector" or "raster". When vector, the guide requires vector/graphic tags.</param>
    public ValidationResult Validate(string title, string description, IReadOnlyList<string> keywords, string? mode = null)
    {
        var issues = new List<ValidationIssue>();
        title = (title ?? "").Trim();
        description = (description ?? "").Trim();
        keywords ??= Array.Empty<string>();

        bool blocks = false;

        // --- Title ---
        if (title.Length == 0)
        {
            issues.Add(new("error", "title", "Il titolo è vuoto."));
            blocks = true;
        }
        else
        {
            if (title.Length < TitleMinChars)
                issues.Add(new("warning", "title", $"Titolo corto ({title.Length} caratteri): punta a una frase descrittiva (15-70)."));
            if (title.Length > TitleMaxChars)
            {
                issues.Add(new("error", "title", $"Titolo troppo lungo ({title.Length}): massimo {TitleMaxChars} caratteri.", "adobe"));
                blocks = true;
            }
            else if (title.Length > TitleIptcChars)
            {
                // Two separate facts: Adobe's stated ideal, and the hard storage limit of the IPTC
                // IIM "Object Name" field, where exiftool silently truncates on write.
                issues.Add(new("info", "title",
                    $"Titolo di {title.Length} caratteri: Adobe indica {TitleIdealChars} come ideale, e oltre {TitleIptcChars} il campo IPTC del titolo viene troncato.", "adobe"));
            }
            var words = title.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < TitleMinWords)
                issues.Add(new("warning", "title", "Titolo con poche parole: descrivi soggetto + stile (es. 'silhouette vettoriale ballerina')."));

            if (Regex.IsMatch(title, "[<>|\\\\{}\\[\\]#@\\^~`]"))
                issues.Add(new("warning", "title", "Il titolo contiene caratteri speciali sconsigliati (<>|\\{}[]#@^~`)."));
            if (Regex.IsMatch(title, "[!?.]{2,}") || title.Contains("...."))
                issues.Add(new("warning", "title", "Punteggiatura ripetuta nel titolo: evita '!!' o '...'."));

            // Keyword stuffing: same significant word repeated in the title.
            var repeats = words
                .Select(w => Regex.Replace(w.ToLowerInvariant(), "[^a-z0-9]", ""))
                .Where(w => w.Length > 3 && !StopWords.Contains(w))
                .GroupBy(w => w)
                .Where(g => g.Count() >= 3)
                .Select(g => g.Key)
                .ToList();
            foreach (var r in repeats)
                issues.Add(new("warning", "title", $"La parola '{r}' è ripetuta nel titolo: il keyword stuffing penalizza il ranking."));
        }

        // --- Description ---
        if (description.Length > 0 && description.Length < 10)
            issues.Add(new("info", "description", "Descrizione molto breve: una frase chiara aiuta la SEO."));

        // --- Keywords ---
        var kw = keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
        var distinct = kw.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (kw.Count == 0)
        {
            issues.Add(new("error", "keywords", "Nessuna keyword: obbligatorie per Adobe Stock e Freepik."));
            blocks = true;
        }
        else
        {
            if (kw.Count < KeywordsMin)
                issues.Add(new("warning", "keywords", $"Poche keyword ({kw.Count}): Adobe indica {KeywordsIdealMin}-{KeywordsIdealMax} come intervallo ideale."));
            else if (kw.Count < KeywordsIdealMin)
                issues.Add(new("info", "keywords", $"{kw.Count} keyword: Adobe indica {KeywordsIdealMin}-{KeywordsIdealMax} come intervallo ideale."));
            else if (kw.Count > KeywordsIdealMax && kw.Count <= KeywordsMax)
                issues.Add(new("info", "keywords", $"{kw.Count} keyword: oltre le {KeywordsIdealMax} consigliate da Adobe. Meglio poche e precise che molte e generiche.", "adobe"));

            if (kw.Count > KeywordsMax)
            {
                issues.Add(new("error", "keywords", $"Troppe keyword ({kw.Count}): Adobe accetta max {KeywordsMax}.", "adobe"));
                blocks = true;
            }

            if (distinct.Count < kw.Count)
                issues.Add(new("warning", "keywords", $"{kw.Count - distinct.Count} keyword duplicate: rimuovile, non aiutano il ranking."));

            var multiWord = distinct.Count(k => k.Split(' ').Length > 3);
            if (multiWord > 0)
                issues.Add(new("info", "keywords", $"{multiWord} keyword molto lunghe: Adobe scarta le locuzioni non comuni, quelle che non troveresti in un dizionario."));

            // --- Guide: "Adjectives should be descriptive, not subjective" ---
            var subjective = distinct
                .Where(k => k.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => AdobeStockRules.SubjectiveWords.Contains(w)))
                .Take(8)
                .ToList();
            if (subjective.Count > 0)
                issues.Add(new("warning", "keywords",
                    $"Aggettivi soggettivi ({string.Join(", ", subjective)}): Adobe chiede termini descrittivi come 'red' o 'furry', non giudizi.", "adobe"));

            // --- Guide: "Nouns should be singular" ---
            var plurals = distinct
                .Select(k => k.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
                .Where(LooksPlural)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            if (plurals.Count > 0)
                issues.Add(new("info", "keywords",
                    $"Possibili plurali ({string.Join(", ", plurals)}): Adobe chiede il singolare, 'cat' invece di 'cats'.", "adobe"));

            // --- Guide: important title words belong in the first 10 keywords ---
            if (title.Length > 0 && kw.Count >= KeywordsMin)
            {
                // Compared on stems, not on exact spelling: the guide asks for singular nouns and
                // root-form verbs, so "sits" in the title is legitimately covered by "sit".
                var first10 = kw.Take(10)
                    .SelectMany(k => k.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .SelectMany(Stems)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = title
                    .Split(new[] { ' ', '\t', ',', '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => Regex.Replace(w.ToLowerInvariant(), "[^a-z0-9]", ""))
                    .Where(w => w.Length > 3 && !StopWords.Contains(w))
                    .Distinct()
                    .Where(w => !Stems(w).Any(first10.Contains))
                    .Take(6)
                    .ToList();
                if (missing.Count > 0)
                    issues.Add(new("warning", "keywords",
                        $"Parole del titolo assenti dalle prime 10 keyword ({string.Join(", ", missing)}): Adobe pesa soprattutto le prime 10.", "adobe"));
            }

            // "no people" next to a person is a factual error, and Adobe penalises keywords that do
            // not match the asset. A silhouette of someone still depicts a person. Not vector-only:
            // the contradiction is just as wrong on a photograph.
            var flatAll = distinct.Select(k => k.ToLowerInvariant()).ToHashSet();
            if (flatAll.Contains("no people") || flatAll.Contains("nobody"))
            {
                // The declaration itself contains the word "people": comparing against it would
                // always match, so MentionsPeople looks only at the other keywords and the title.
                if (AdobeStockRules.MentionsPeople(flatAll, title))
                    issues.Add(new("warning", "keywords",
                        "Contraddizione: ci sono 'no people'/'nobody' ma il soggetto è una persona. Le silhouette di persone sono comunque persone.", "adobe"));
            }

            // --- Guide, illustration section: vectors must carry vector/graphic ---
            if (string.Equals(mode, "vector", StringComparison.OrdinalIgnoreCase))
            {
                var required = new[] { "vector", "graphic" }.Where(r => !flatAll.Contains(r)).ToList();
                if (required.Count > 0)
                    issues.Add(new("warning", "keywords",
                        $"Mancano le keyword che Adobe richiede per i vettoriali: {string.Join(", ", required)}.", "adobe"));
            }
        }

        // --- Trademarks (title + keywords) ---
        var haystack = (title + " " + string.Join(" ", kw)).ToLowerInvariant();
        var foundTm = Trademarks.Where(t => Regex.IsMatch(haystack, $@"\b{Regex.Escape(t)}\b")).ToList();
        foreach (var tm in foundTm)
        {
            issues.Add(new("error", "trademark", $"Termine a marchio '{tm}': causa il rifiuto. Rimuovilo da titolo e keyword."));
            blocks = true;
        }

        // --- Scoring (0-100) ---
        int score = 100;
        foreach (var i in issues)
            score -= i.Severity switch { "error" => 30, "warning" => 10, _ => 3 };
        score = Math.Clamp(score, 0, 100);

        // Per-site scores (Adobe is stricter on keyword count/cap; Freepik values descriptive titles).
        int adobe = ScoreSite(score, issues, "adobe");
        int freepik = ScoreSite(score, issues, "freepik");

        return new ValidationResult(
            score,
            blocks,
            new[] { new SiteScore("Adobe Stock", adobe), new SiteScore("Freepik", freepik) },
            issues);
    }

    private static int ScoreSite(int baseScore, List<ValidationIssue> issues, string site)
    {
        int s = baseScore;
        foreach (var i in issues.Where(i => i.Site == site))
            s -= i.Severity == "error" ? 10 : 5;
        return Math.Clamp(s, 0, 100);
    }

    /// <summary>
    /// Candidate forms of a word (itself plus light stem variants), so title/keyword matching
    /// survives the singular-noun and root-verb rules the guide imposes. Deliberately generative
    /// rather than a real stemmer: it only needs one shared form to declare a match.
    /// </summary>
    private static IEnumerable<string> Stems(string word)
    {
        yield return word;
        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal)) yield return word[..^3] + "y";
        if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal)) yield return word[..^2];
        if (word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal)) yield return word[..^1];
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            yield return word[..^3];
            yield return word[..^3] + "e";
        }
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
        {
            yield return word[..^2];
            yield return word[..^1];
        }
    }

    /// <summary>
    /// Conservative plural heuristic: only a hint, so it must not cry wolf. Words ending in
    /// ss/us/is/as/os are almost never plural, and the exception list covers the usual traps.
    /// </summary>
    private static bool LooksPlural(string word)
    {
        if (word.Length < 4) return false;
        if (!word.EndsWith("s", StringComparison.OrdinalIgnoreCase)) return false;
        if (AdobeStockRules.NotPlural.Contains(word)) return false;

        var tail = word[^2..].ToLowerInvariant();
        if (tail is "ss" or "us" or "is" or "as" or "os") return false;

        return true;
    }
}
