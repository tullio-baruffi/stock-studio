using System.Text.RegularExpressions;

namespace StockStudio.Api.Services;

/// <summary>Whether a trending topic can actually become a sellable stock image, and what to draw.</summary>
public record StockRelevance(
    bool Usable,
    string Verdict,        // ok | adapt | reject | unknown
    int Score,             // 0-100 suitability as a stock subject
    string Reason,
    string? Theme,
    string? EntityKind,    // what Wikipedia says it is (team, company, event...)
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> Prompts);

/// <summary>
/// Filters trends down to the ones that make sense for this business: subjects that can be drawn
/// as a silhouette/vector AND legally sold on Adobe Stock and Freepik.
///
/// Three outcomes:
///  - <b>ok</b>     the topic itself is a valid stock subject (eclipse, halloween, yoga)
///  - <b>adapt</b>  the topic is a brand/person/work — unsellable as such — but its underlying theme
///                  is (Baltimore Orioles -> baseball), so we suggest the generic concepts instead
///  - <b>reject</b> nothing usable (tragedies, crime, pure abstractions with no visual)
///
/// The vocabulary is bilingual: the user works in Italian, and English Wikipedia resolves Italian
/// phrases to unrelated articles ("farfalla" lands on a German model), so an Italian phrase must be
/// recognised from the words themselves rather than from the encyclopaedia.
/// </summary>
public class StockRelevanceService
{
    private readonly WikipediaTrendClient _wiki;
    private readonly ILogger<StockRelevanceService> _log;

    public StockRelevanceService(WikipediaTrendClient wiki, ILogger<StockRelevanceService> log)
    {
        _wiki = wiki;
        _log = log;
    }

    // Wikipedia short-description patterns that make the *topic itself* unusable for stock.
    private static readonly (string Kind, Regex Rx, string Why)[] Blockers =
    {
        ("persona", new Regex(@"\b(politician|actor|actress|singer|rapper|musician|footballer|player|athlete|coach|author|journalist|presenter|youtuber|influencer|entrepreneur|businessman|businesswoman|comedian|director|model|boxer|driver|golfer|swimmer)\b", RegexOptions.IgnoreCase),
            "è una persona reale: servirebbe una licenza editoriale, non vendibile come vettoriale commerciale"),
        ("marchio", new Regex(@"\b(company|corporation|manufacturer|retailer|airline|bank|brand|conglomerate|startup|studio|publisher|broadcaster|network|platform|subsidiary|magazine)\b", RegexOptions.IgnoreCase),
            "è un'azienda/marchio: logo e nome sono protetti"),
        ("squadra", new Regex(@"\b(team|club|franchise|squad)\b", RegexOptions.IgnoreCase),
            "è una squadra: nome e stemma sono marchi registrati"),
        ("opera", new Regex(@"\b(film|movie|television series|tv series|sitcom|album|song|single|video game|novel|manga|anime|book by|miniseries|episode)\b", RegexOptions.IgnoreCase),
            "è un'opera protetta da copyright"),
    };

    private static readonly Regex GoodEntity = new(
        @"\b(event|celebration|festival|holiday|phenomenon|season|sport|activity|practice|discipline|species|animal|plant|dish|cuisine|natural|astronomical|weather|tradition|custom)\b",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Topics tied to tragedy, crime or civil unrest. Checked against the topic's *identity*
    /// (phrase, article name, short description) — never the full article body, where a passing
    /// historical mention of "war" would wrongly disqualify an innocent subject.
    /// </summary>
    private static readonly Regex Sensitive = new(
        @"\b(shoot(ing|er)s?|murder\w*|killing|killed|homicide|massacre|terror\w*|bombing|attack\w*|assault|invasion|war|warfare|genocide|assassinat\w*|kidnap\w*|hostage|rape|abuse|crash|died|death|deaths|dead|funeral|obituary|arrest\w*|indict\w*|convict\w*|lawsuit|scandal|fraud|riot\w*|unrest|protest\w*|disaster|earthquake|wildfire|flood\w*|famine|outbreak|pandemic|overdose|suicide|shipwreck|explosion|strage|omicidio|attentato|terremoto|alluvione)\b",
        RegexOptions.IgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> KeyRx = new();

    /// <summary>
    /// Drops diacritics so "caffe" matches "caffè" — users rarely type accents, and a missed match
    /// silently falls back to the unreliable Wikipedia signal.
    /// </summary>
    private static string Fold(string s)
    {
        var norm = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(norm.Length);
        foreach (var c in norm)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>
    /// Word-boundary match, allowing a short suffix on longer keys so "golf" also catches "golfer".
    /// Naive substring matching caused real misfiles: "ai" fired inside "chairman", "art" inside "article".
    /// </summary>
    public static bool Mentions(string? text, string key)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var rx = KeyRx.GetOrAdd(key, k =>
        {
            var body = Regex.Escape(Fold(k));
            // Only plural endings are tolerated. A looser rule matched "eastern" as "easter"
            // and wrongly flagged Easter as trending.
            var suffix = k.Length >= 5 ? "(?:s|es)?" : "";
            return new Regex($@"\b{body}{suffix}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        });
        return rx.IsMatch(Fold(text));
    }

    private static bool Has(string? text, string key) => Mentions(text, key);

    /// <summary>
    /// Fast classification for live feeds. It uses the trend title plus its real news context and
    /// deliberately performs no Wikipedia search: names of people and brands are the majority of a
    /// live feed and resolving each one is slow and heavily rate-limited.
    /// </summary>
    public StockRelevance ClassifyFromContext(string phrase, string context, string style)
    {
        if (Sensitive.IsMatch($"{phrase} {context}"))
            return Rejected(
                "Scartato: riguarda cronaca, tragedie o conflitti — inadatto a contenuti commerciali.",
                null);

        // Treat the news text as a strong description: it usually says which sport/industry/event
        // sits behind a proper name, without requiring us to identify that person or brand.
        var match = MatchTheme(phrase, null, context, "");
        if (match.FromPhraseGeneric && match.Theme != null)
            return new StockRelevance(true, "ok", 90,
                $"Soggetto generico e disegnabile (tema «{match.Theme.Name}»).",
                match.Theme.Name, null, match.Theme.Subjects, Build(match.Theme, style, phrase));

        if (match.Theme != null)
        {
            var reason = match.ViaBrand
                ? $"Il nome è protetto: non usarlo in titolo o keyword. Il tema «{match.Theme.Name}» resta sfruttabile con soggetti generici."
                : $"Il tema «{match.Theme.Name}» emerge dalla notizia collegata: usa soggetti generici, non il nome del trend.";
            return new StockRelevance(false, "adapt", 55, reason,
                match.Theme.Name, match.ViaBrand ? "marchio" : "tema da notizia",
                match.Theme.Subjects, Build(match.Theme, style));
        }

        return new StockRelevance(false, "unknown", 25,
            "Nessun tema stock riconosciuto nel titolo o nella notizia collegata.",
            null, null, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>Classifies one trend phrase, optionally reusing an already-resolved Wikipedia article.</summary>
    public async Task<StockRelevance> ClassifyAsync(string phrase, string? article, string style, CancellationToken ct)
    {
        string desc = "", extract = "";
        try
        {
            if (article == null)
            {
                // One call does resolve + description; two separate ones hit the rate limit.
                var (found, summary) = await _wiki.LookupAsync(phrase, ct);
                article = found;
                desc = summary.Description ?? "";
                extract = summary.Extract ?? "";
            }
            else
            {
                var s = await _wiki.GetSummaryAsync(article, ct);
                desc = s.Description ?? "";
                extract = s.Extract ?? "";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Classificazione senza contesto Wikipedia per {Phrase}", phrase);
        }

        // Safety gate first: nothing about a tragedy or crime becomes a sellable vector.
        if (Sensitive.IsMatch(phrase))
            return Rejected("Scartato: riguarda cronaca, tragedie o conflitti — inadatto a contenuti commerciali.", null);

        var match = MatchTheme(phrase, article, desc, extract);

        // The user's own words matched our generic vocabulary, so the subject is generic by
        // construction. Trust it over Wikipedia, which resolves Italian phrases to unrelated
        // articles ("farfalla" -> a German model) and would wrongly flag them.
        if (match.FromPhraseGeneric && match.Theme != null)
            return new StockRelevance(true, "ok", 90,
                $"Soggetto generico e disegnabile (tema «{match.Theme.Name}»).",
                match.Theme.Name, null, match.Theme.Subjects, Build(match.Theme, style, phrase));

        if (Sensitive.IsMatch($"{article?.Replace('_', ' ')} {desc}"))
            return Rejected("Scartato: riguarda cronaca, tragedie o conflitti — inadatto a contenuti commerciali.",
                DescribeKind(desc));

        var blocker = Blockers.FirstOrDefault(b => b.Rx.IsMatch(desc) || (desc.Length == 0 && b.Rx.IsMatch(extract)));

        if (blocker.Kind != null)
        {
            if (match.Theme == null)
                return Rejected($"Scartato: {blocker.Why}, e non emerge un tema disegnabile alternativo.", blocker.Kind);

            return new StockRelevance(false, "adapt", 55,
                $"Non usare il nome: {blocker.Why}. Però il tema «{match.Theme.Name}» è sfruttabile — crea soggetti generici.",
                match.Theme.Name, blocker.Kind, match.Theme.Subjects, Build(match.Theme, style));
        }

        if (match.Theme != null)
        {
            // The name itself is a brand ("NFL", "Nintendo"): the theme is fine, the wording is not.
            if (match.ViaBrand)
                return new StockRelevance(false, "adapt", 55,
                    $"Il nome è un marchio registrato: non usarlo in titolo o keyword. Il tema «{match.Theme.Name}» resta sfruttabile con soggetti generici.",
                    match.Theme.Name, "marchio", match.Theme.Subjects, Build(match.Theme, style));

            // Strength 1 = only found deep in the article body: plausible but worth a human check.
            if (match.Strength == 1)
                return new StockRelevance(true, "adapt", 45,
                    $"Tema «{match.Theme.Name}» dedotto dal contesto, non dal titolo: verifica che sia pertinente prima di creare.",
                    match.Theme.Name, DescribeKind(desc), match.Theme.Subjects, Build(match.Theme, style));

            bool clearlyVisual = GoodEntity.IsMatch(desc) || GoodEntity.IsMatch(extract);
            return new StockRelevance(true, "ok", clearlyVisual ? 92 : 78,
                clearlyVisual
                    ? $"Soggetto valido e disegnabile (tema «{match.Theme.Name}»)."
                    : $"Utilizzabile tramite il tema «{match.Theme.Name}».",
                match.Theme.Name, DescribeKind(desc), match.Theme.Subjects,
                Build(match.Theme, style, match.FromPhrase ? phrase : null));
        }

        if (string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(extract))
            return new StockRelevance(false, "unknown", 25,
                "Non classificabile: nessun tema riconosciuto e Wikipedia non ha restituito informazioni. Valutalo a mano.",
                null, null, Array.Empty<string>(), Array.Empty<string>());

        return Rejected($"Nessun tema visivo riconosciuto ({desc}): difficile da rendere come immagine vendibile.",
            DescribeKind(desc));
    }

    private static StockRelevance Rejected(string reason, string? kind) =>
        new(false, "reject", 0, reason, null, kind, Array.Empty<string>(), Array.Empty<string>());

    private sealed record ThemeMatch(StockTheme? Theme, int Strength, bool FromPhrase, bool FromPhraseGeneric, bool ViaBrand);

    /// <summary>
    /// Finds the best theme, preferring evidence closest to the topic's identity: a hit in the trend
    /// title outranks one in the description, which outranks a passing mention in the article body.
    /// Also reports whether the match came from the user's own words via a generic (non-brand) term.
    /// </summary>
    private static ThemeMatch MatchTheme(string phrase, string? article, string desc, string extract)
    {
        var title = $"{phrase} {article?.Replace('_', ' ')}";
        StockTheme? best = null;
        int bestStrength = 0, bestLen = 0;
        bool fromPhrase = false, fromPhraseGeneric = false, viaBrand = false;

        foreach (var t in StockThemeCatalog.All)
            foreach (var (k, generic) in t.Keys.Select(k => (k, true)).Concat(t.Brands.Select(b => (b, false))))
            {
                int s = Has(title, k) ? 3
                      : Has(desc, k) ? 2
                      // Deep in the body only specific words count, or every country mentions "mountain".
                      : k.Length >= 7 && Has(extract, k) ? 1
                      : 0;
                if (s == 0) continue;

                if (s > bestStrength || (s == bestStrength && k.Length > bestLen))
                {
                    best = t;
                    bestStrength = s;
                    bestLen = k.Length;
                    fromPhrase = Has(phrase, k);
                    fromPhraseGeneric = fromPhrase && generic;
                    viaBrand = !generic;
                }
            }

        return new ThemeMatch(best, bestStrength, fromPhrase, fromPhraseGeneric, viaBrand);
    }

    private static string? DescribeKind(string desc) =>
        string.IsNullOrWhiteSpace(desc) ? null : desc.Length > 80 ? desc[..80] : desc;

    /// <summary>Turns drawable subjects into prompts ready to paste into the image generator.</summary>
    private static IReadOnlyList<string> Build(StockTheme theme, string style, string? flavour = null) =>
        StockThemeCatalog.BuildPrompts(theme, style, flavour);
}
