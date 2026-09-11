using System.Text.Json;

namespace StockStudio.Api.Services.Ai;

/// <summary>
/// Reasons about what is worth drawing and selling, instead of matching a hand-written dictionary.
/// </summary>
public interface IStockIntelligence
{
    bool IsAgentic { get; }
    string Describe();

    Task<StockRelevance> ClassifyAsync(string topic, string promptStyle, CancellationToken ct);

    Task<IReadOnlyList<ThemeOpportunity>> DiscoverAsync(
        string promptStyle, string? category, string geo, int count, CancellationToken ct);
}

/// <summary>
/// The agentic engine: an LLM decides <b>what</b> is worth creating and <b>how to draw it</b>,
/// while the code supplies the evidence and measures the numbers.
///
/// Two rules keep it honest:
///  1. The model never invents figures — every audience number comes back from the
///     <c>measure_interest</c> tool, which reads real Wikipedia pageviews.
///  2. The model must justify each suggestion, and the justification is shown to the user.
///
/// This replaces the fixed vocabulary of themes and keywords: the agent can propose any subject,
/// in any language, including ones nobody thought of when the app was written.
/// </summary>
public class AgenticStockIntelligence : IStockIntelligence
{
    private readonly LlmClient _llm;
    private readonly WikipediaTrendClient _wiki;
    private readonly GoogleTrendsClient _google;
    private readonly NewsTrendClient _news;
    private readonly ILogger<AgenticStockIntelligence> _log;

    private const int MaxToolRounds = 6;

    public AgenticStockIntelligence(LlmClient llm, WikipediaTrendClient wiki, GoogleTrendsClient google,
                                    NewsTrendClient news, ILogger<AgenticStockIntelligence> log)
    {
        _llm = llm;
        _wiki = wiki;
        _google = google;
        _news = news;
        _log = log;
    }

    public bool IsAgentic => _llm.IsConfigured;
    public string Describe() => _llm.Describe();

    // ---------------------------------------------------------------- tools

    private static readonly LlmTool[] Tools =
    {
        new("measure_interest",
            "Misura l'interesse reale del pubblico per un tema, leggendo le visite della voce Wikipedia. " +
            "Restituisce visite medie mensili, rapporto di stagionalità (picco/media), mese di picco, " +
            "momentum e la traiettoria degli ultimi 15 giorni. Usalo SEMPRE prima di affermare che un tema " +
            "è richiesto: non inventare numeri.",
            new
            {
                type = "object",
                properties = new
                {
                    topic = new { type = "string", description = "Tema o soggetto in inglese, es. 'Halloween', 'Yoga', 'Solar eclipse'." },
                },
                required = new[] { "topic" },
            }),
        new("get_trending",
            "Elenca ciò che viene cercato in questo momento su Google Trends per un paese, con il traffico " +
            "approssimativo e una notizia collegata.",
            new
            {
                type = "object",
                properties = new
                {
                    geo = new { type = "string", description = "Codice paese ISO, es. US, IT, GB." },
                },
                required = new[] { "geo" },
            }),
        new("get_news",
            "Cerca titoli di notizie reali su un argomento. Utile per capire quali eventi futuri stanno " +
            "guadagnando copertura e quindi genereranno domanda.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Query di ricerca, es. 'winter olympics 2028'." },
                },
                required = new[] { "query" },
            }),
    };

    private async Task<string> RunToolAsync(LlmToolCall call, CancellationToken ct)
    {
        try
        {
            using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var root = args.RootElement;

            switch (call.Name)
            {
                case "measure_interest":
                {
                    var topic = Str(root, "topic");
                    if (string.IsNullOrWhiteSpace(topic)) return Json(new { error = "topic mancante" });

                    var (article, _) = await _wiki.LookupAsync(topic, ct);
                    if (article is null) return Json(new { topic, found = false, note = "Nessuna voce Wikipedia corrispondente." });

                    var signal = await _wiki.GetAsync(article, ct);
                    if (signal is null)
                        return Json(new { topic, article, found = false, note = "Dati di traffico non disponibili al momento." });

                    var daily = await _wiki.GetDailySeriesAsync(article, 15, ct);
                    double? ratio = null;
                    string trajectory = "unknown";
                    if (daily is { Count: >= 14 })
                    {
                        double prev = daily.Take(7).Average(), last = daily.Skip(daily.Count - 7).Average();
                        if (prev > 0)
                        {
                            ratio = Math.Round(last / prev, 2);
                            trajectory = ratio >= 1.15 ? "rising" : ratio >= 0.92 ? "peaking" : "fading";
                        }
                    }

                    return Json(new
                    {
                        topic,
                        article,
                        found = true,
                        averageMonthlyViews = signal.AverageViews,
                        seasonalityRatio = signal.SeasonalityRatio,
                        peakMonth = signal.PeakMonth,
                        momentum = signal.Momentum,
                        last15DaysRatio = ratio,
                        trajectory,
                    });
                }

                case "get_trending":
                {
                    var geo = Str(root, "geo") ?? "US";
                    var trending = await _google.GetTrendingAsync(geo, ct);
                    return Json(new
                    {
                        geo,
                        trends = trending.Take(20).Select(t => new
                        {
                            title = t.Title,
                            traffic = t.ApproxTraffic,
                            news = t.RelatedNews.FirstOrDefault(),
                        }),
                    });
                }

                case "get_news":
                {
                    var query = Str(root, "query");
                    if (string.IsNullOrWhiteSpace(query)) return Json(new { error = "query mancante" });
                    var items = await _news.SearchAsync(query!, ct);
                    return Json(new
                    {
                        query,
                        headlines = items.Take(12).Select(h => new { h.Title, h.Source, published = h.Published?.ToString("yyyy-MM-dd") }),
                    });
                }

                default:
                    return Json(new { error = $"strumento sconosciuto: {call.Name}" });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Strumento {Tool} fallito", call.Name);
            return Json(new { error = ex.Message });
        }
    }

    // ---------------------------------------------------------------- classify

    public async Task<StockRelevance> ClassifyAsync(string topic, string promptStyle, CancellationToken ct)
    {
        var system = $$"""
            Sei un esperto di microstock che aiuta un autore a decidere cosa produrre e vendere su
            Adobe Stock e Freepik. L'autore lavora con supporti diversi -- fotografia, illustrazione,
            grafica vettoriale -- e la scelta del supporto viene DOPO: qui si decide il tema.

            Devi giudicare se un tema può diventare un contenuto stock vendibile. Ragiona su:
            - DIRITTI: persone reali riconoscibili, marchi, loghi, squadre, personaggi, opere protette
              NON sono vendibili come contenuto commerciale.
            - OPPORTUNITÀ: dietro un nome protetto c'è quasi sempre un tema generico sfruttabile
              (una squadra di baseball → il baseball; un cantante → la musica).
            - SENSIBILITÀ: cronaca nera, tragedie, guerre, disastri non sono adatti a contenuti commerciali.
            - CONCRETEZZA: il soggetto deve essere raffigurabile. Concetti puramente astratti
              (inflazione, un nome proprio) vanno tradotti in oggetti o scene concrete. Non giudicare
              in base a un supporto particolare: un tema può essere fotografato, disegnato o vettorializzato.

            L'utente può scrivere in italiano o in inglese. I PROMPT devono essere in inglese, e devono
            descrivere il SOGGETTO e la SCENA senza imporre uno stile grafico: chi li userà sceglierà
            da sé se realizzarli come fotografia, illustrazione o vettoriale.

            Rispondi SOLO con un oggetto JSON:
            {
              "verdict": "ok" | "adapt" | "reject",
              "reason": "spiegazione in italiano, una o due frasi, concreta",
              "theme": "il tema generico sfruttabile, in italiano, oppure null",
              "entityKind": "cosa è il soggetto (persona, marchio, squadra, festività, animale...), oppure null",
              "concepts": ["4-6 soggetti concreti da disegnare, in inglese"],
              "prompts": ["3-4 prompt completi in inglese, pronti per un generatore di immagini"],
              "keywords": ["6-10 keyword in inglese utili per la vendita"]
            }

            "ok" = il soggetto stesso è vendibile. "adapt" = il nome non si può usare ma il tema sì.
            "reject" = non c'è nulla di sfruttabile.
            """;

        var messages = new List<object>
        {
            new { role = "system", content = system },
            new { role = "user", content = $"Valuta questo tema: «{topic}»" },
        };

        var json = await RunAgentAsync(messages, ct);
        if (json is null) throw new InvalidOperationException("L'agente non ha prodotto una risposta valida.");

        return ParseRelevance(json.Value, topic);
    }

    private StockRelevance ParseRelevance(JsonElement root, string topic)
    {
        var verdict = (Str(root, "verdict") ?? "unknown").ToLowerInvariant();
        if (verdict is not ("ok" or "adapt" or "reject")) verdict = "unknown";

        var concepts = Arr(root, "concepts");
        var prompts = Arr(root, "prompts");

        int score = verdict switch
        {
            "ok" => 90,
            "adapt" => 55,
            "reject" => 0,
            _ => 25,
        };

        return new StockRelevance(
            verdict == "ok",
            verdict,
            score,
            Str(root, "reason") ?? "",
            Str(root, "theme"),
            Str(root, "entityKind"),
            concepts,
            prompts);
    }

    // ---------------------------------------------------------------- discover

    public async Task<IReadOnlyList<ThemeOpportunity>> DiscoverAsync(
        string promptStyle, string? category, string geo, int count, CancellationToken ct)
    {
        var today = DateTime.UtcNow;
        var catLine = string.IsNullOrWhiteSpace(category)
            ? "Copri ambiti diversi fra loro (festività, sport, natura, lifestyle, tecnologia, stagioni...)."
            : $"Proponi SOLO temi che appartengono all'ambito «{category}».";

        var system = $$"""
            Sei un analista di mercato per il microstock. Aiuti un autore a decidere cosa creare
            ADESSO per vendere su Adobe Stock e Freepik. L'autore lavora con supporti diversi
            (fotografia, illustrazione, vettoriale): tu scegli il TEMA, non il supporto.

            Oggi è {{today:yyyy-MM-dd}}.

            Regola commerciale fondamentale: i siti stock premiano i contenuti pubblicati fra 90 e 20 giorni
            PRIMA del picco di domanda. Chi arriva a ridosso dell'evento vende poco. Tieni conto che fra
            la consegna e la messa in vendita passano giorni di revisione, quindi la finestra utile per
            PRODURRE si chiude prima di quella per pubblicare.

            Seconda regola, sul come: Adobe mette in evidenza gli autori ordinandoli per il rapporto fra
            quanto hanno caricato e quanto hanno venduto, considerando solo i file degli ultimi sei mesi.
            Conviene quindi un tema su cui si possano fare POCHI pezzi che vendono, non molti pezzi
            qualsiasi: preferisci nicchie precise e poco affollate a temi generalisti dove la concorrenza
            è enorme e la percentuale di file che vende crolla.

            Metodo che devi seguire:
            1. Raccogli indizi reali con gli strumenti: cosa è di tendenza, quali eventi futuri hanno copertura
               stampa, e soprattutto MISURA l'interesse dei temi candidati con measure_interest.
            2. Scarta ciò che non è vendibile: persone reali, marchi, squadre, opere protette, cronaca e tragedie.
               Se dietro c'è un tema generico sfruttabile, usa quello.
            3. Valuta il tempismo confrontando il mese di picco con la data di oggi.
            4. Proponi ESATTAMENTE {{count}} temi, ordinati dal più conveniente oggi.
               L'array "opportunities" DEVE contenere {{count}} elementi: non concludere prima di averli.
               Per non sprecare giri, misura più candidati nello stesso turno invocando measure_interest
               più volte in parallelo, invece di uno alla volta.

            {{catLine}}

            Non inventare numeri: usa solo quelli restituiti da measure_interest, e riporta in
            "measuredTopic" esattamente il topic che hai passato allo strumento.
            I prompt devono essere in inglese e descrivere SOGGETTO e SCENA senza imporre uno stile
            grafico: chi li userà deciderà se realizzarli come fotografia, illustrazione o vettoriale.

            Rispondi SOLO con un oggetto JSON:
            {
              "opportunities": [
                {
                  "theme": "nome del tema in italiano",
                  "category": "ambito in italiano (Festività, Sport, Natura, Lifestyle, Tecnologia, Stagione...)",
                  "measuredTopic": "il topic esatto passato a measure_interest, in inglese",
                  "timing": "now" | "soon" | "plan" | "evergreen",
                  "why": "perché conviene oggi, in italiano, citando i dati che hai misurato",
                  "hotNow": ["eventuali temi di tendenza ora che ricadono qui"],
                  "concepts": ["4-6 soggetti concreti da disegnare, in inglese"],
                  "prompts": ["3-4 prompt completi in inglese"],
                  "keywords": ["6-10 keyword in inglese"]
                }
              ]
            }

            "now" = sei nella finestra 90-20 giorni prima del picco, oppure è caldo adesso.
            "soon" = il picco si avvicina, conviene iniziare a produrre.
            "plan" = fuori stagione, da mettere in calendario.
            "evergreen" = vende tutto l'anno senza picchi.
            """;

        var messages = new List<object>
        {
            new { role = "system", content = system },
            new { role = "user", content = $"Cosa conviene creare oggi? Paese di riferimento per le tendenze: {geo}." },
        };

        var json = await RunAgentAsync(messages, ct);
        if (json is null) throw new InvalidOperationException("L'agente non ha prodotto una risposta valida.");

        var proposals = Opportunities(json.Value);

        // Small models routinely stop early even when told the exact number. Ask once for the
        // missing themes, reusing the same conversation so the measurements already collected
        // are not thrown away.
        if (proposals.Count > 0 && proposals.Count < count)
        {
            var seen = proposals.Select(p => Str(p, "theme"))
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Select(s => s!)
                                .ToList();

            _log.LogInformation("L'agente ha proposto {Got} temi sui {Want} richiesti: chiedo i mancanti",
                                proposals.Count, count);

            messages.Add(new
            {
                role = "user",
                content = $"Hai proposto solo {proposals.Count} temi sui {count} richiesti. Proponi altri "
                        + $"{count - proposals.Count} temi NUOVI, diversi da questi: {string.Join(", ", seen)}. "
                        + "Stesso identico formato JSON, con la sola chiave \"opportunities\".",
            });

            try
            {
                var more = await RunAgentAsync(messages, ct);
                if (more is not null)
                {
                    foreach (var extra in Opportunities(more.Value))
                    {
                        var name = Str(extra, "theme");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (seen.Contains(name!, StringComparer.OrdinalIgnoreCase)) continue;
                        seen.Add(name!);
                        proposals.Add(extra);
                    }
                }
            }
            catch (Exception ex)
            {
                // A shorter list is still useful: never fail the whole request over the top-up.
                _log.LogWarning(ex, "Richiesta dei temi mancanti non riuscita");
            }
        }

        var results = new List<ThemeOpportunity>();
        foreach (var o in proposals.Take(count))
        {
            var theme = Str(o, "theme");
            if (string.IsNullOrWhiteSpace(theme)) continue;

            // The model chose the subject; the figures are re-read from the source so nothing shown
            // to the user can be hallucinated.
            var measured = Str(o, "measuredTopic") ?? theme!;
            var facts = await MeasureAsync(measured, ct);

            var timing = (Str(o, "timing") ?? "plan").ToLowerInvariant();
            if (timing is not ("now" or "soon" or "plan" or "late" or "evergreen")) timing = "plan";

            // La verifica sul mercato riguarda il tema, non il supporto: aggiungere "silhouette" alla
            // ricerca mostrava quanti concorrenti hanno fatto quel tema IN QUEL MODO, che è una
            // domanda diversa e più stretta di quella che serve per decidere se il tema esiste.
            var q = theme!;

            results.Add(new ThemeOpportunity(
                theme!,
                Str(o, "category") ?? "Generale",
                ScoreFrom(timing, facts),
                timing,
                VerifiedAdvice(timing, facts),
                facts.Average,
                facts.Seasonality,
                facts.PeakMonth,
                facts.DaysToPeak,
                timing == "now",
                facts.Momentum,
                facts.TrendRatio,
                facts.Trajectory,
                facts.Series,
                Arr(o, "hotNow"),
                Arr(o, "concepts"),
                Arr(o, "prompts"),
                $"https://stock.adobe.com/search?k={Uri.EscapeDataString(q)}",
                $"https://www.freepik.com/search?query={Uri.EscapeDataString(q)}&type=vector",
                facts.Article ?? ""));
        }

        return results;
    }

    /// <summary>The "opportunities" array as a detached list, or empty when the shape is wrong.</summary>
    private static List<JsonElement> Opportunities(JsonElement json) =>
        json.TryGetProperty("opportunities", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : new List<JsonElement>();

    private record Facts(string? Article, long Average, double? Seasonality, int? PeakMonth,
                         int? DaysToPeak, double? Momentum, double? TrendRatio, string Trajectory,
                         IReadOnlyList<long> Series);

    private async Task<Facts> MeasureAsync(string topic, CancellationToken ct)
    {
        try
        {
            var (article, _) = await _wiki.LookupAsync(topic, ct);
            if (article is null) return Empty();

            var signal = await _wiki.GetAsync(article, ct);
            if (signal is null) return Empty() with { Article = article };

            int? peak = signal.PeakMonth > 0 ? signal.PeakMonth : null;
            int? days = null;
            if (peak is int pm)
            {
                var next = new DateTime(DateTime.UtcNow.Year, pm, 15);
                if (next < DateTime.UtcNow.Date) next = next.AddYears(1);
                days = (int)(next - DateTime.UtcNow.Date).TotalDays;
            }

            var daily = await _wiki.GetDailySeriesAsync(article, 15, ct);
            double? ratio = null;
            string trajectory = "unknown";
            if (daily is { Count: >= 14 })
            {
                double prev = daily.Take(7).Average(), last = daily.Skip(daily.Count - 7).Average();
                if (prev > 0)
                {
                    ratio = Math.Round(last / prev, 2);
                    trajectory = ratio >= 1.15 ? "rising" : ratio >= 0.92 ? "peaking" : "fading";
                }
            }

            return new Facts(article, signal.AverageViews, Math.Round(signal.SeasonalityRatio, 2),
                             peak, days, signal.Momentum, ratio, trajectory,
                             daily ?? (IReadOnlyList<long>)Array.Empty<long>());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Misurazione fallita per {Topic}", topic);
            return Empty();
        }

        static Facts Empty() => new(null, 0, null, null, null, null, null, "unknown", Array.Empty<long>());
    }

    private static int ScoreFrom(string timing, Facts f)
    {
        double timingScore = timing switch
        {
            "now" => 40,
            "soon" => 28,
            "evergreen" => 18,
            "late" => 10,
            _ => 12,
        };
        double demand = Math.Clamp(Math.Log10(Math.Max(f.Average, 100)) / 6.5, 0, 1) * 45;
        double trajectory = f.Trajectory switch { "rising" => 10, "peaking" => 6, "fading" => 1, _ => 4 };
        double season = f.Seasonality is double s && s >= 2 ? 5 : 0;
        return (int)Math.Round(Math.Clamp(timingScore + demand + trajectory + season, 0, 100));
    }

    private static string VerifiedAdvice(string timing, Facts facts)
    {
        var timingText = timing switch
        {
            "now" => "L'agente lo propone come opportunità da affrontare ora.",
            "soon" => "L'agente consiglia di iniziare a prepararlo.",
            "evergreen" => "L'agente lo considera un tema sempreverde.",
            "late" => "L'agente segnala che il picco è molto vicino o già trascorso.",
            _ => "L'agente lo propone come tema da pianificare.",
        };

        if (facts.Average <= 0)
            return $"{timingText} La fonte non ha restituito dati sufficienti: valuta manualmente domanda e tempismo.";

        var evidence = new List<string> { $"{facts.Average:N0} visite medie/mese" };
        if (facts.Seasonality is double seasonality)
            evidence.Add($"picco {seasonality:0.##}×");
        if (facts.PeakMonth is int month && month is >= 1 and <= 12)
            evidence.Add($"mese di picco: {MonthNames[month - 1]}");
        if (facts.DaysToPeak is int days)
            evidence.Add($"circa {days} giorni al prossimo picco");
        if (facts.Trajectory != "unknown")
            evidence.Add($"traiettoria 15 giorni: {facts.Trajectory}");

        return $"{timingText} Dati verificati: {string.Join("; ", evidence)}.";
    }

    private static readonly string[] MonthNames =
    {
        "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno",
        "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre",
    };

    // ---------------------------------------------------------------- agent loop

    /// <summary>Runs the model until it stops asking for tools, then parses its JSON answer.</summary>
    private async Task<JsonElement?> RunAgentAsync(List<object> messages, CancellationToken ct)
    {
        for (int round = 0; round < MaxToolRounds; round++)
        {
            // JSON mode is only safe once tools are done: some providers reject it alongside tool calls.
            var reply = await _llm.ChatAsync(messages, Tools, 0.3, jsonMode: false, ct);

            if (reply.ToolCalls.Count == 0)
            {
                var parsed = TryParse(reply.Content);
                if (parsed is not null) return parsed;

                // Nudge once towards clean JSON rather than failing outright.
                messages.Add(new { role = "assistant", content = reply.Content ?? "" });
                messages.Add(new { role = "user", content = "Rispondi di nuovo, SOLO con l'oggetto JSON richiesto, senza testo attorno." });
                continue;
            }

            messages.Add(new
            {
                role = "assistant",
                content = (string?)null,
                tool_calls = reply.ToolCalls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.ArgumentsJson },
                }).ToArray(),
            });

            foreach (var call in reply.ToolCalls)
            {
                var result = await RunToolAsync(call, ct);
                messages.Add(new { role = "tool", tool_call_id = call.Id, content = result });
            }
        }

        _log.LogWarning("L'agente ha esaurito i giri di strumenti senza concludere");
        // The last permitted round may have produced useful tool results. Give the model one final,
        // tool-free turn to turn those results into the required JSON instead of discarding them.
        messages.Add(new
        {
            role = "user",
            content = "Gli strumenti non sono più disponibili. Concludi ora usando i risultati raccolti e rispondi SOLO con l'oggetto JSON richiesto.",
        });
        var finalReply = await _llm.ChatAsync(messages, null, 0.3, jsonMode: false, ct);
        return TryParse(finalReply.Content);
    }

    private static JsonElement? TryParse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var text = content.Trim();
        // Models often wrap JSON in a markdown fence.
        if (text.StartsWith("```"))
        {
            int nl = text.IndexOf('\n');
            if (nl > 0) text = text[(nl + 1)..];
            int fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text[..fence];
            text = text.Trim();
        }
        if (!text.StartsWith("{"))
        {
            int open = text.IndexOf('{');
            int close = text.LastIndexOf('}');
            if (open < 0 || close <= open) return null;
            text = text[open..(close + 1)];
        }

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Come deve essere scritto un prompt di soggetto.
    ///
    /// Non nomina più un supporto. Prima restituiva "silhouette nera piena su sfondo bianco" e
    /// simili, il che legava la ricerca di opportunità a un modo di realizzarle: un tema che vale
    /// la pena fare vale la pena farlo comunque lo si produca, e la scelta fra fotografia,
    /// illustrazione e vettoriale viene dopo, guardando in quale delle tre graduatorie di Adobe
    /// conviene presentarsi.
    /// </summary>
    private static string PromptStyleHint(string style) =>
        "descrivi soggetto, azione e ambiente in modo concreto e neutro rispetto al supporto, " +
        "senza nominare tecniche o stili grafici, senza testo nell'immagine";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
               .Select(x => x.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
            : Array.Empty<string>();

    private static string Json(object o) => JsonSerializer.Serialize(o);
}
