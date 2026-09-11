using System.Threading.Channels;
using StockStudio.Api.Services.Integration;

namespace StockStudio.Api.Services.Scoring;

/// <summary>
/// Tiene allineata la colonna del punteggio senza far aspettare nessuno, e porta a termine da sé
/// il riempimento dello storico.
///
/// Il punteggio si ricalcola comunque a ogni lettura, perché è lì che si vede se il valore
/// depositato è ancora vero. Ma riscriverlo dentro la richiesta significherebbe far pagare a chi
/// sta solo sfogliando la galleria ventiquattro scritture su SharePoint, cioè qualche secondo di
/// attesa per un dato che a lui non serve: lui il punteggio ce l'ha già, glielo abbiamo appena
/// calcolato. Serve a chi filtrerà domani.
///
/// Quindi la lettura segnala e va avanti, e le scritture avvengono qui dietro.
///
/// Due lavori, in ordine di precedenza:
///
/// 1. **Smaltire le segnalazioni**, cioè i punteggi che non corrispondono più. Vengono da chi sta
///    guardando adesso, quindi hanno la precedenza.
/// 2. **Riempire lo storico**, quando non c'è altro da fare. Scorre le librerie una pagina per
///    volta e deposita i punteggi mancanti.
///
/// Il secondo lavoro sta qui e non in una pagina web aperta da qualche parte: guidato dal browser
/// funzionava, ma si fermava chiudendo la scheda, e un riempimento che dipende da una finestra
/// aperta per un'ora non è un riempimento, è una veglia.
///
/// La coda ha un tetto e, quando è piena, scarta le segnalazioni nuove invece di rallentare chi
/// legge: perdere un aggiornamento non fa danno -- la lettura successiva di quella stessa pagina lo
/// segnalerà di nuovo, e comunque il riempimento ci passerà sopra -- mentre bloccare la galleria sì.
/// </summary>
public class PunteggioStore : BackgroundService
{
    public readonly record struct DaScrivere(string Libreria, int Id, int Punteggio);

    private readonly Channel<DaScrivere> _coda =
        Channel.CreateBounded<DaScrivere>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly SharePointStore _sp;
    private readonly Punteggiatore _punteggiatore;
    private readonly ILogger<PunteggioStore> _log;

    /// <summary>Le librerie da riempire, e a che punto è arrivato lo scorrimento di ciascuna.</summary>
    private readonly Dictionary<string, string?> _cursori = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ImagesToClassify"] = null,
        ["ImagesToSend"] = null,
        ["ImagesSent"] = null,
    };

    private readonly HashSet<string> _complete = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quanto è stato visitato e depositato in ciascuna libreria, in questo giro.</summary>
    private readonly Dictionary<string, (int Visitati, int Depositati)> _avanzamento =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quanti elementi ha ciascuna libreria, per dare un denominatore al progresso.</summary>
    private readonly Dictionary<string, int> _totali = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quante pagine sono state chieste per libreria: se sono più del dovuto, si rilegge.</summary>
    private readonly Dictionary<string, int> _pagine = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Librerie il cui scorrimento si è fermato per un guasto, con il motivo.</summary>
    private readonly Dictionary<string, string> _interrotte = new(StringComparer.OrdinalIgnoreCase);

    private int _scritti;
    private int _scartati;
    private int _riempiti;
    private int _visitati;
    private string _lavoro = "in attesa";
    private string? _ultimoErrore;
    private DateTimeOffset _ultimaAttivita = DateTimeOffset.UtcNow;
    private DateTimeOffset _avvio = DateTimeOffset.UtcNow;

    public PunteggioStore(SharePointStore sp, Punteggiatore punteggiatore, ILogger<PunteggioStore> log)
    {
        _sp = sp;
        _punteggiatore = punteggiatore;
        _log = log;
    }

    /// <summary>Se questa libreria è già stata riempita per intero dal servizio di sfondo.</summary>
    public bool Completa(string libreria) => _complete.Contains(libreria);

    /// <summary>Segnala che un punteggio depositato non corrisponde più. Non attende.</summary>
    public void Segnala(string libreria, int id, int punteggio)
    {
        if (!_coda.Writer.TryWrite(new DaScrivere(libreria, id, punteggio)))
            Interlocked.Increment(ref _scartati);
    }

    public object Stato() => new
    {
        scritti = _scritti,
        scartati = _scartati,
        inCoda = _coda.Reader.Count,
        riempimento = new
        {
            depositati = _riempiti,
            visitati = _visitati,
            complete = _complete.ToArray(),
            lavoro = _lavoro,
            da = _ultimaAttivita,
            avvio = _avvio,
            librerie = _cursori.Keys.Select(l => new
            {
                libreria = l,
                completa = _complete.Contains(l),
                visitati = _avanzamento.TryGetValue(l, out var a) ? a.Visitati : 0,
                depositati = _avanzamento.TryGetValue(l, out var b) ? b.Depositati : 0,
                totale = _totali.TryGetValue(l, out var t) ? t : 0,
                cursore = _cursori[l],
                pagine = _pagine.TryGetValue(l, out var p) ? p : 0,
                interrotta = _interrotte.TryGetValue(l, out var m) ? m : null,
                inCorso = string.Equals(_lavoro, $"riempio {l}", StringComparison.Ordinal),
            }).ToArray(),
        },
        ultimoErrore = _ultimoErrore,
    };

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Si lascia respirare l'avvio: all'accensione il sito ha di meglio da fare che scrivere
        // numeri che nessuno sta guardando.
        try { await Task.Delay(TimeSpan.FromSeconds(25), stop); }
        catch (OperationCanceledException) { return; }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                // Le segnalazioni hanno la precedenza: vengono da chi sta guardando adesso.
                if (_coda.Reader.Count > 0)
                {
                    await SmaltisciCoda(stop);
                    continue;
                }

                // Niente da smaltire: si avanza sul riempimento dello storico.
                if (await AvanzaRiempimento(stop)) continue;

                // Tutto in ordine: si aspetta la prossima segnalazione senza consumare niente.
                _lavoro = "in attesa";
                var primo = await _coda.Reader.ReadAsync(stop);
                Accumula(_lotto, primo);
                await SmaltisciCoda(stop);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Un errore qui non deve spegnere il servizio: la colonna resterebbe indietro per
                // sempre, e nessuno se ne accorgerebbe finché non serve un filtro.
                _ultimoErrore = $"{DateTimeOffset.UtcNow:HH:mm:ss} {ex.Message.Split('\n')[0]}";
                _log.LogWarning(ex, "Deposito dei punteggi non riuscito, si riprova");
                _lotto.Clear();
                try { await Task.Delay(TimeSpan.FromSeconds(30), stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private readonly Dictionary<string, Dictionary<int, int>> _lotto =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raccoglie per qualche secondo e scrive in blocco: un viaggio invece di cento.</summary>
    private async Task SmaltisciCoda(CancellationToken stop)
    {
        _lavoro = "smaltisco le segnalazioni";
        using (var finestra = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            finestra.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while (Conta(_lotto) < LottoMax && await _coda.Reader.WaitToReadAsync(finestra.Token))
                    while (Conta(_lotto) < LottoMax && _coda.Reader.TryRead(out var altro))
                        Accumula(_lotto, altro);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                // Finestra di raccolta chiusa: si scrive quello che si è messo insieme.
            }
        }

        foreach (var (libreria, punteggi) in _lotto)
        {
            var fatti = _sp.SetScores(libreria, punteggi);
            Interlocked.Add(ref _scritti, fatti);
            _ultimaAttivita = DateTimeOffset.UtcNow;
            if (fatti > 0) _log.LogInformation("Punteggi depositati su {List}: {N}", libreria, fatti);
        }
        _lotto.Clear();
    }

    /// <summary>
    /// Deposita i punteggi mancanti di una pagina, e avanza il cursore.
    ///
    /// Restituisce false quando non c'è più niente da riempire da nessuna parte: a quel punto il
    /// servizio torna semplicemente ad ascoltare le segnalazioni.
    /// </summary>
    private async Task<bool> AvanzaRiempimento(CancellationToken stop)
    {
        var libreria = _cursori.Keys.FirstOrDefault(l => !_complete.Contains(l) && !_interrotte.ContainsKey(l));
        if (libreria == null) return false;

        _lavoro = $"riempio {libreria}";

        // Il totale si chiede una volta per libreria e per giro: serve solo a dare un denominatore
        // al progresso, non a fare i conti.
        if (!_totali.ContainsKey(libreria))
        {
            try { _totali[libreria] = _sp.ItemCount(libreria); }
            catch { _totali[libreria] = 0; }
        }

        var page = _sp.ScorriPerRiempimento(libreria, 100, _cursori[libreria]);
        _pagine[libreria] = (_pagine.TryGetValue(libreria, out var np) ? np : 0) + 1;

        // Il cursore deve scendere sempre: è l'identificativo dell'ultima riga vista, e la lettura
        // ordina per identificativo decrescente. Se risalisse vorrebbe dire che si sta ricominciando
        // da capo, e il riempimento girerebbe in tondo senza finire mai.
        if (int.TryParse(_cursori[libreria], out var precedente)
            && int.TryParse(page.NextPageToken, out var nuovo)
            && nuovo >= precedente)
        {
            // Fermare sì, ma senza dichiararla completa: sarebbe una bugia, e per giunta una bugia
            // che nasconde proprio il guasto. Si dice che è interrotta e perché.
            _interrotte[libreria] = $"il cursore non è sceso ({precedente} -> {nuovo})";
            _log.LogWarning("Riempimento {List}: cursore non monotono {Prima} -> {Dopo}", libreria, precedente, nuovo);
            return true;
        }

        var daScrivere = new Dictionary<int, int>();
        foreach (var gruppo in _punteggiatore.Raggruppa(libreria, page.Items))
        {
            var portatore = Punteggiatore.PortatoreDi(gruppo);
            var punteggio = _punteggiatore.Valuta(gruppo).Score;
            if (portatore.PunteggioSalvato != punteggio) daScrivere[portatore.Id] = punteggio;
        }

        _visitati += page.Items.Count;
        var depositatiOra = 0;
        if (daScrivere.Count > 0)
        {
            depositatiOra = _sp.SetScores(libreria, daScrivere);
            Interlocked.Add(ref _riempiti, depositatiOra);
        }
        _ultimaAttivita = DateTimeOffset.UtcNow;

        var prima = _avanzamento.TryGetValue(libreria, out var a) ? a : (0, 0);
        _avanzamento[libreria] = (prima.Item1 + page.Items.Count, prima.Item2 + depositatiOra);

        _cursori[libreria] = page.NextPageToken;
        if (string.IsNullOrEmpty(page.NextPageToken))
        {
            _complete.Add(libreria);
            _log.LogInformation("Riempimento punteggi completato su {List}", libreria);
        }

        // Un respiro fra una pagina e l'altra: il riempimento è un lavoro di sfondo e non deve
        // competere con chi sta usando il sito, su un piano con sessanta minuti di CPU al giorno.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stop); }
        catch (OperationCanceledException) { return false; }
        return true;
    }

    private const int LottoMax = 100;

    private static void Accumula(Dictionary<string, Dictionary<int, int>> lotto, DaScrivere d)
    {
        if (!lotto.TryGetValue(d.Libreria, out var perLibreria))
            lotto[d.Libreria] = perLibreria = new Dictionary<int, int>();
        perLibreria[d.Id] = d.Punteggio;
    }

    private static int Conta(Dictionary<string, Dictionary<int, int>> lotto) =>
        lotto.Sum(x => x.Value.Count);
}
