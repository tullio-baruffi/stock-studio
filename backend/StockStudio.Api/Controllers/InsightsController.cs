using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockStudio.Api.Services;
using StockStudio.Api.Services.Integration;
using StockStudio.Api.Services.Sales;
using StockStudio.Api.Services.Scoring;

namespace StockStudio.Api.Controllers;

/// <summary>
/// I numeri che aiutano a decidere, non quelli che fanno scena.
///
/// Un cruscotto è utile solo se ogni riquadro risponde a una domanda che qualcuno si è fatto
/// davvero. "Quante vendite in tutto" non è una di quelle: è un numero che sale e basta, e non
/// dice mai cosa fare domani. Le domande vere sono altre -- sto migliorando o peggiorando, quanto
/// rende un file da quando l'ho caricato, quanto dipendo da pochi pezzi fortunati, quando conviene
/// pubblicare -- e ognuna di queste ha una risposta calcolabile.
///
/// La più importante non l'ho trovata da nessuna parte: **i metadati fanno vendere?** Si spendono
/// giornate a rifinire titoli e keyword sulla fede che servano, e il modo di verificarlo era lì da
/// sempre -- confrontare il punteggio dei file che hanno venduto con quello dei file fermi. Se non
/// ci fosse differenza, tutto l'impianto andrebbe ripensato; e sarebbe meglio saperlo.
/// </summary>
[ApiController]
[Route("api/insights")]
public partial class InsightsController : ControllerBase
{
    private readonly SalesStore _sales;
    private readonly SharePointStore _sp;
    private readonly Punteggiatore _punteggiatore;
    private readonly Services.Insights.AggancioVendite _aggancio;
    private readonly PipelineSettings _s;
    private readonly ILogger<InsightsController> _log;

    public InsightsController(SalesStore sales, SharePointStore sp, Punteggiatore punteggiatore,
                              Services.Insights.AggancioVendite aggancio,
                              IOptions<PipelineSettings> s, ILogger<InsightsController> log)
    {
        _sales = sales;
        _sp = sp;
        _punteggiatore = punteggiatore;
        _aggancio = aggancio;
        _s = s.Value;
        _log = log;
    }

    private static decimal R(decimal v) => Math.Round(v, 2);

    /// <summary>
    /// Il quadro costa una decina di secondi: campionare la libreria richiede letture vere, e non
    /// c'è modo di renderle gratis. Ma i dati sottostanti si muovono di ore, non di secondi, quindi
    /// riaspettare a ogni visita sarebbe solo attesa sprecata. La risposta resta valida cinque
    /// minuti; il pulsante Aggiorna la forza comunque.
    /// </summary>
    private static readonly Dictionary<string, (DateTimeOffset Quando, object Dati)> Cache = new();
    private static readonly object Serratura = new();
    private static readonly TimeSpan Freschezza = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Una sola definizione della chiave, condivisa con l'aggancio in sottofondo: due definizioni
    /// dello stesso confronto divergono in silenzio, e qui il confronto è tutto.
    /// </summary>
    private static string Chiave(string nome) => Services.Insights.ChiaveFile.Di(nome);

    private static string Serie(string nome) => Services.Insights.ChiaveFile.Serie(nome);

    [HttpGet("quadro")]
    public IActionResult Quadro([FromQuery] string library = "ImagesSent",
                                [FromQuery] int campione = 600,
                                [FromQuery] bool aggiorna = false)
    {
        var chiaveCache = $"{library}|{campione}";
        if (!aggiorna)
        {
            lock (Serratura)
            {
                if (Cache.TryGetValue(chiaveCache, out var c) && DateTimeOffset.UtcNow - c.Quando < Freschezza)
                    return Ok(c.Dati);
            }
        }

        var vendite = _sales.Range(null, null);
        if (vendite.Count == 0)
            return Ok(new { ok = false, error = "Nessuna vendita in archivio: importa prima l'esportazione da Adobe." });

        var oggi = DateTimeOffset.UtcNow;

        // --- Andamento: la sola domanda che conta è se si sta salendo o scendendo -------------
        object Finestra(int giorni)
        {
            var da = oggi.AddDays(-giorni);
            var daPrima = oggi.AddDays(-giorni * 2);
            var ora = vendite.Where(v => v.SoldAt >= da).ToList();
            var prima = vendite.Where(v => v.SoldAt >= daPrima && v.SoldAt < da).ToList();
            var rOra = ora.Sum(v => v.Royalty);
            var rPrima = prima.Sum(v => v.Royalty);
            return new
            {
                giorni,
                vendite = ora.Count,
                ricavi = R(rOra),
                venditePrima = prima.Count,
                ricaviPrima = R(rPrima),
                variazione = rPrima > 0 ? Math.Round((rOra - rPrima) / rPrima * 100, 1) : (decimal?)null,
            };
        }

        // --- Concentrazione: quanto si dipende da pochi pezzi ---------------------------------
        var perFile = vendite
            .GroupBy(v => Chiave(v.FileName), StringComparer.Ordinal)
            .Select(g => new
            {
                file = g.Key,
                nome = g.First().FileName,
                serie = Serie(g.First().FileName),
                n = g.Count(),
                ricavi = g.Sum(x => x.Royalty),
                prima = g.Min(x => x.SoldAt),
                ultima = g.Max(x => x.SoldAt),
            })
            .OrderByDescending(x => x.ricavi)
            .ToList();

        var ricaviTotali = perFile.Sum(x => x.ricavi);

        // --- Stagionalità: in quali mesi il mercato compra -------------------------------------
        var perMeseDellAnno = vendite
            .GroupBy(v => v.SoldAt.Month)
            .Select(g => new
            {
                mese = g.Key,
                nome = new DateTime(2000, g.Key, 1).ToString("MMMM", new System.Globalization.CultureInfo("it-IT")),
                vendite = g.Count(),
                ricavi = R(g.Sum(x => x.Royalty)),
            })
            .OrderBy(x => x.mese)
            .ToList();

        var mediaMensile = perMeseDellAnno.Count > 0 ? perMeseDellAnno.Average(x => x.ricavi) : 0m;

        // --- Quanto ci mette un file a vendere la prima volta ----------------------------------
        // Serve a sapere quanta pazienza è ragionevole prima di dire che un'immagine è ferma.
        var attese = new List<int>();

        // --- Il punteggio dei metadati fa vendere? --------------------------------------------
        // Si confronta il punteggio dei file che hanno venduto con quello dei file fermi, su un
        // campione della libreria. È una correlazione, non una dimostrazione: chi ha metadati
        // migliori potrebbe anche avere immagini migliori. Ma se la differenza non ci fosse
        // affatto, sarebbe un fatto pesante -- e questo è il modo di accorgersene.
        var vendutiPunteggi = new List<int>();
        var fermiPunteggi = new List<int>();
        var esaminati = 0;
        var serieInLibreria = new HashSet<string>(StringComparer.Ordinal);
        var campioneRiuscito = false;
        var collisioni = 0;
        var esatto = _aggancio.Ultimo;

        if (esatto is { Completo: true, FileInLibreria: > 0 } && esatto.Library == library)
        {
            // Aggancio esatto: la libreria è stata scorsa per intero, non c'è niente da stimare.
            vendutiPunteggi.AddRange(esatto.PunteggiVenduti);
            fermiPunteggi.AddRange(esatto.PunteggiFermi);
            attese.AddRange(esatto.Attese);
            esaminati = esatto.FileInLibreria;
            collisioni = esatto.Collisioni;
            campioneRiuscito = true;
        }
        else
        {
        try
        {
            var venduto = perFile.ToDictionary(x => x.file, x => x, StringComparer.Ordinal);
            var quante = Math.Clamp(campione, 100, 2000);

            // Leggere le prime N righe non è campionare: il cursore scorre dagli identificativi
            // alti ai bassi, quindi si otterrebbero soltanto i caricamenti più recenti -- proprio
            // quelli che non hanno ancora avuto il tempo di vendere. Misurato: sulle prime 600
            // righe risultavano 5 file venduti invece dei ~25 attesi. Si prendono quindi finestre
            // sparse lungo tutto l'intervallo di identificativi.
            var maxId = 0;
            var minId = 0;
            try
            {
                var prima = _sp.ListItems(library, 1, null, null, null);
                maxId = prima.Items.Count > 0 ? prima.Items[0].Id : 0;
                minId = _sp.MinItemId(library);
            }
            catch { /* senza estremi si ripiega sullo scorrimento sequenziale */ }

            const int perFinestra = 100;
            var finestre = Math.Max(1, quante / perFinestra);
            var partenze = new List<string?>();
            if (maxId > minId && minId > 0 && finestre > 1)
            {
                var passo = (maxId - minId) / (double)finestre;
                for (var k = 0; k < finestre; k++)
                    partenze.Add(((int)(maxId - k * passo) + 1).ToString());
            }
            else partenze.Add(null);

            var visti = new HashSet<int>();
            var chiaviViste = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var partenza in partenze)
            {
                var token = partenza;
                var nellaFinestra = 0;
                while (nellaFinestra < perFinestra && esaminati < quante)
                {
                    var page = _sp.ListItems(library, Math.Min(perFinestra, quante - esaminati), token, null, null);
                    if (page.Items.Count == 0) break;

                    foreach (var gruppo in _punteggiatore.Raggruppa(library, page.Items))
                    {
                        var i = Punteggiatore.PortatoreDi(gruppo);
                        if (!visti.Add(i.Id)) continue;  // le finestre possono sovrapporsi in coda

                        var p = _punteggiatore.Valuta(gruppo).Score;
                        var b = Chiave(i.FileName);
                        if (chiaviViste.TryGetValue(b, out var gia) && gia != i.FileName) collisioni++;
                        else chiaviViste[b] = i.FileName;
                        serieInLibreria.Add(Serie(i.FileName));
                        esaminati++;
                        nellaFinestra++;

                        if (venduto.TryGetValue(b, out var v))
                        {
                            vendutiPunteggi.Add(p);
                            // Created e non Modified: rilavorare i metadati sposta Modified, e
                            // misureremmo l'ultima modifica invece dell'attesa vera.
                            var quando = !string.IsNullOrEmpty(i.Created) ? i.Created : i.Modified;
                            if (DateTimeOffset.TryParse(quando, out var pubblicata) && v.prima > pubblicata)
                                attese.Add((int)(v.prima - pubblicata).TotalDays);
                        }
                        else fermiPunteggi.Add(p);
                    }

                    token = page.NextPageToken;
                    if (string.IsNullOrEmpty(token)) break;
                }
                if (esaminati >= quante) break;
            }
            campioneRiuscito = esaminati > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Campionamento della libreria non riuscito");
        }
        }

        // --- Cosa di queste vendite riguarda ciò che l'applicazione gestisce ---------------------
        // Il portfolio Adobe è più grande della libreria: contiene anche i caricamenti precedenti a
        // questa applicazione. Dividere i ricavi totali per i file in libreria darebbe un rapporto
        // fra due insiemi diversi -- numeratore e denominatore che non parlano degli stessi file.
        // Con l'aggancio esatto si sa quali file venduti stanno in libreria; senza, si ripiega
        // sull'appartenenza alla serie, che è una stima e viene dichiarata tale.
        var perChiave = esatto?.Chiavi;
        var gestiti = perChiave != null
            ? perFile.Where(x => perChiave.Contains(x.file)).ToList()
            : campioneRiuscito
                ? perFile.Where(x => serieInLibreria.Contains(x.serie)).ToList()
                : perFile;
        var fuori = perChiave != null
            ? perFile.Where(x => !perChiave.Contains(x.file)).ToList()
            : campioneRiuscito
                ? perFile.Where(x => !serieInLibreria.Contains(x.serie)).ToList()
                : perFile.Take(0).ToList();

        var ricaviGestiti = gestiti.Sum(x => x.ricavi);
        var venditeGestite = gestiti.Sum(x => x.n);

        // Quanti file fanno metà dei ricavi: si conta sui gestiti, così il rapporto col totale
        // della libreria resta fra grandezze omogenee.
        var cumulato = 0m;
        var fileMeta = 0;
        foreach (var f in gestiti.OrderByDescending(x => x.ricavi))
        {
            cumulato += f.ricavi;
            fileMeta++;
            if (cumulato >= ricaviGestiti / 2) break;
        }

        double Mediana(List<int> v)
        {
            if (v.Count == 0) return 0;
            var o = v.OrderBy(x => x).ToList();
            return o.Count % 2 == 1 ? o[o.Count / 2] : (o[o.Count / 2 - 1] + o[o.Count / 2]) / 2.0;
        }

        // --- Chi vendeva e ha smesso ------------------------------------------------------------
        var spenti = gestiti
            .Where(x => x.n >= 3 && x.ultima < oggi.AddDays(-180))
            .OrderByDescending(x => x.ricavi)
            .Take(10)
            .Select(x => new
            {
                file = x.nome,
                vendite = x.n,
                ricavi = R(x.ricavi),
                fermoDa = (int)(oggi - x.ultima).TotalDays,
            })
            .ToList();

        var totaleLibreria = 0;
        try { totaleLibreria = _sp.ItemCount(library); } catch { /* il totale è una comodità */ }

        var risposta = new
        {
            ok = true,            library,
            aggiornatoAl = oggi,

            andamento = new { ultimi30 = Finestra(30), ultimi90 = Finestra(90), ultimi365 = Finestra(365) },

            efficienza = new
            {
                fileInPortfolio = totaleLibreria,
                fileCheVendono = gestiti.Count,
                percentualeCheVende = totaleLibreria > 0
                    ? Math.Round(gestiti.Count * 100.0 / totaleLibreria, 1) : 0,
                // Il numero che conta davvero: quanto ha reso in media ogni file prodotto, non
                // ogni file venduto. Il secondo lusinga, il primo dice se il lavoro rende.
                // Al numeratore solo i ricavi dei file che stanno anche in libreria: altrimenti
                // si dividerebbero le vendite di tutto il portfolio Adobe per i soli file gestiti.
                ricavoPerFileProdotto = totaleLibreria > 0 ? R(ricaviGestiti / totaleLibreria) : 0,
                ricavoPerFileCheVende = gestiti.Count > 0 ? R(ricaviGestiti / gestiti.Count) : 0,
                ricaviTotali = R(ricaviGestiti),
                venditeTotali = venditeGestite,
            },

            // Quanta parte dell'archivio vendite riguarda file che questa applicazione gestisce.
            // Senza questo, ogni rapporto qui sopra sembrerebbe riferito a tutto il portfolio.
            copertura = new
            {
                misurata = campioneRiuscito,
                // La differenza fra un numero contato e uno dedotto dalla serie di appartenenza.
                // Tenerla visibile è l'unico modo perché nessuno prenda il secondo per il primo.
                esatta = perChiave != null,
                scorsaIl = esatto?.Quando,
                serieRiconosciute = serieInLibreria.OrderBy(x => x).Take(12),
                venditeGestite,
                ricaviGestiti = R(ricaviGestiti),
                venditeFuori = fuori.Sum(x => x.n),
                ricaviFuori = R(fuori.Sum(x => x.ricavi)),
                fileFuori = fuori.Count,
                percentualeRicaviGestiti = ricaviTotali > 0
                    ? Math.Round(ricaviGestiti * 100 / ricaviTotali, 1) : 0,
            },

            concentrazione = new
            {
                fileCheFannoMetaRicavi = fileMeta,
                percentualeDelPortfolio = totaleLibreria > 0
                    ? Math.Round(fileMeta * 100.0 / totaleLibreria, 2) : 0,
                migliore = gestiti.OrderByDescending(x => x.ricavi).Take(5)
                    .Select(x => new { file = x.nome, vendite = x.n, ricavi = R(x.ricavi) }),
            },

            mercato = new
            {
                perTipo = vendite.GroupBy(v => v.AssetType)
                    .Select(g => new
                    {
                        tipo = g.Key,
                        vendite = g.Count(),
                        ricavi = R(g.Sum(x => x.Royalty)),
                        perDownload = R(g.Sum(x => x.Royalty) / g.Count()),
                    })
                    .OrderByDescending(x => x.ricavi),
                perLicenza = vendite.GroupBy(v => v.License)
                    .Select(g => new { licenza = g.Key, vendite = g.Count(), ricavi = R(g.Sum(x => x.Royalty)) })
                    .OrderByDescending(x => x.ricavi)
                    .Take(6),
            },

            stagionalita = new
            {
                mesi = perMeseDellAnno,
                mediaMensile = R(mediaMensile),
                migliori = perMeseDellAnno.OrderByDescending(x => x.ricavi).Take(3).Select(x => x.nome),
                peggiori = perMeseDellAnno.OrderBy(x => x.ricavi).Take(3).Select(x => x.nome),
            },

            metadatiFannoVendere = new
            {
                esaminati,
                venduti = vendutiPunteggi.Count,
                fermi = fermiPunteggi.Count,
                punteggioMedianoVenduti = Math.Round(Mediana(vendutiPunteggi), 1),
                punteggioMedianoFermi = Math.Round(Mediana(fermiPunteggi), 1),
                differenza = Math.Round(Mediana(vendutiPunteggi) - Mediana(fermiPunteggi), 1),
                // La mediana può coincidere e le code no. Se anche le medie coincidono, il
                // risultato regge; se divergono, si vede subito che la mediana nascondeva qualcosa.
                mediaVenduti = vendutiPunteggi.Count > 0 ? Math.Round(vendutiPunteggi.Average(), 1) : 0,
                mediaFermi = fermiPunteggi.Count > 0 ? Math.Round(fermiPunteggi.Average(), 1) : 0,
                sopra90Venduti = vendutiPunteggi.Count > 0
                    ? Math.Round(vendutiPunteggi.Count(x => x >= 90) * 100.0 / vendutiPunteggi.Count, 1) : 0,
                sopra90Fermi = fermiPunteggi.Count > 0
                    ? Math.Round(fermiPunteggi.Count(x => x >= 90) * 100.0 / fermiPunteggi.Count, 1) : 0,
                // Una mediana su una manciata di file non dice niente, e mostrarla come se dicesse
                // qualcosa è peggio del non mostrarla: sembra una risposta. Sotto questa soglia il
                // riquadro dichiara che il campione è troppo magro invece di dare un verdetto.
                attendibile = vendutiPunteggi.Count >= 30 && fermiPunteggi.Count >= 30,
                soglia = 30,
                // Nomi diversi ridotti alla stessa chiave: se fossero molti, l'aggancio fra
                // vendite e libreria sarebbe da rivedere invece che da usare.
                collisioni,
            },

            pazienza = new
            {
                misurate = attese.Count,
                giorniMedianiAllaPrimaVendita = attese.Count > 0 ? Math.Round(Mediana(attese), 0) : 0,
                entroUnMese = attese.Count > 0 ? Math.Round(attese.Count(x => x <= 30) * 100.0 / attese.Count, 0) : 0,
                oltreSeiMesi = attese.Count > 0 ? Math.Round(attese.Count(x => x > 180) * 100.0 / attese.Count, 0) : 0,
            },

            spenti,
        };

        lock (Serratura) { Cache[chiaveCache] = (DateTimeOffset.UtcNow, risposta); }
        return Ok(risposta);
    }

    /// <summary>
    /// Rifà l'aggancio adesso invece di aspettare il giro dell'ora. Non blocca: risponde subito e
    /// il lavoro procede in sottofondo, perché scorrere diecimila file dura una quarantina di
    /// secondi e tenere ferma la pagina per tutto quel tempo non servirebbe a nessuno.
    /// </summary>
    [HttpPost("riaggancia")]
    public IActionResult Riaggancia()
    {
        _aggancio.Risveglia();
        lock (Serratura) { Cache.Clear(); }
        return Ok(new { ok = true, inCorso = _aggancio.InCorso, ultimo = _aggancio.Ultimo?.Quando });
    }
}
