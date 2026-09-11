using StockStudio.Api.Services.Integration;
using StockStudio.Api.Services.Sales;
using StockStudio.Api.Services.Scoring;

namespace StockStudio.Api.Services.Insights;

/// <summary>
/// L'aggancio fra le vendite e ciò che sta davvero in libreria.
///
/// Il portfolio Adobe è più grande della libreria: contiene anche i caricamenti fatti prima che
/// questa applicazione esistesse. Finché non si sa quali file venduti stiano anche in libreria,
/// ogni rapporto fra ricavi e magazzino mette al numeratore e al denominatore insiemi diversi.
///
/// Il campionamento era un ripiego, e mentiva due volte: leggere le prime seicento righe non è
/// campionare (il cursore scorre dai caricamenti recenti, proprio quelli che non hanno ancora
/// avuto tempo di vendere), e dedurre il tasso dalla serie di appartenenza lo sovrastimava --
/// 4,1% dedotto contro 1,8% misurato. Due numeri diversi per la stessa cosa significano che
/// almeno uno è sbagliato.
///
/// Qui si smette di stimare: si scorre la libreria per intero, una volta ogni ora, in sottofondo.
/// Costa una quarantina di secondi di attesa di rete e un paio di secondi di calcolo, e in cambio
/// tutti i numeri del cruscotto diventano esatti invece che estrapolati.
/// </summary>
public class AggancioVendite : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<AggancioVendite> _log;
    private static readonly TimeSpan Intervallo = TimeSpan.FromHours(1);
    private readonly ManualResetEventSlim _sveglia = new(false);

    public AggancioVendite(IServiceProvider sp, ILogger<AggancioVendite> log)
    {
        _sp = sp;
        _log = log;
    }

    /// <summary>Quel che si sa della libreria dopo l'ultimo giro completo.</summary>
    public record Quadro(
        DateTimeOffset Quando,
        string Library,
        int FileInLibreria,
        HashSet<string> Chiavi,
        List<int> PunteggiVenduti,
        List<int> PunteggiFermi,
        List<int> Attese,
        int Collisioni,
        bool Completo);

    private volatile Quadro? _ultimo;
    private volatile bool _inCorso;

    public Quadro? Ultimo => _ultimo;
    public bool InCorso => _inCorso;

    /// <summary>
    /// Forza un giro adesso invece di aspettare l'ora. Non blocca: chi chiede vede il quadro
    /// precedente finché il nuovo non è pronto, che è meglio di una pagina che non risponde.
    /// </summary>
    public void Risveglia() => _sveglia.Set();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Un minuto di grazia: all'avvio ci sono cose più urgenti che un cruscotto.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch { return; }

        while (!stop.IsCancellationRequested)
        {
            try { Ricostruisci("ImagesSent", stop); }
            catch (Exception ex) { _log.LogWarning(ex, "Aggancio vendite non riuscito"); }

            try
            {
                _sveglia.Reset();
                await Task.Run(() => _sveglia.Wait(Intervallo, stop), stop);
            }
            catch { return; }
        }
    }

    private void Ricostruisci(string library, CancellationToken stop)
    {
        using var scope = _sp.CreateScope();
        var sharePoint = scope.ServiceProvider.GetRequiredService<SharePointStore>();
        var vendite = scope.ServiceProvider.GetRequiredService<SalesStore>();
        var punteggiatore = scope.ServiceProvider.GetRequiredService<Punteggiatore>();

        var righe = vendite.Range(null, null);
        if (righe.Count == 0) return;

        var primaVendita = righe
            .GroupBy(v => ChiaveFile.Di(v.FileName), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(x => x.SoldAt), StringComparer.Ordinal);

        _inCorso = true;
        try
        {
            var chiavi = new HashSet<string>(StringComparer.Ordinal);
            var venduti = new List<int>();
            var fermi = new List<int>();
            var attese = new List<int>();
            var collisioni = 0;
            var letti = 0;
            string? cursore = null;
            var completo = false;

            while (!stop.IsCancellationRequested)
            {
                var page = sharePoint.ScorriPerRiempimento(library, 200, cursore);
                if (page.Items.Count == 0) { completo = true; break; }

                foreach (var gruppo in punteggiatore.Raggruppa(library, page.Items))
                {
                    var i = Punteggiatore.PortatoreDi(gruppo);
                    var k = ChiaveFile.Di(i.FileName);
                    if (!chiavi.Add(k)) { collisioni++; continue; }
                    letti++;

                    var p = punteggiatore.Valuta(gruppo).Score;
                    if (primaVendita.TryGetValue(k, out var quando))
                    {
                        venduti.Add(p);
                        var nato = !string.IsNullOrEmpty(i.Created) ? i.Created : i.Modified;
                        if (DateTimeOffset.TryParse(nato, out var ingresso) && quando > ingresso)
                            attese.Add((int)(quando - ingresso).TotalDays);
                    }
                    else fermi.Add(p);
                }

                cursore = page.NextPageToken;
                if (string.IsNullOrEmpty(cursore)) { completo = true; break; }
            }

            if (letti > 0)
            {
                _ultimo = new Quadro(DateTimeOffset.UtcNow, library, letti, chiavi,
                                     venduti, fermi, attese, collisioni, completo);
                _log.LogInformation(
                    "Aggancio vendite: {Letti} file, {Venduti} hanno venduto, {Collisioni} collisioni, completo={Completo}",
                    letti, venduti.Count, collisioni, completo);
            }
        }
        finally { _inCorso = false; }
    }
}

/// <summary>
/// La chiave con cui si riconosce lo stesso file scritto in modi diversi.
///
/// Adobe restituisce il nome file come lo ha ricevuto al momento della vendita, e la convenzione
/// è cambiata nel tempo: "Hype2Art 25-003897", "Hype2Art_25_29887", "Hype2art-25-018588".
/// Confrontarli alla lettera aggancia soltanto le righe in cui la forma coincide, e ne esce un
/// tasso di vendita falsamente basso -- misurato: 5 riscontri invece di 11 sullo stesso campione.
/// </summary>
public static class ChiaveFile
{
    public static string Di(string nome)
    {
        var b = Path.GetFileNameWithoutExtension(nome ?? "").Trim().ToLowerInvariant();
        b = System.Text.RegularExpressions.Regex.Replace(b, @"[\s_-]+", "-");
        return string.Join('-', b.Split('-').Select(s =>
            s.Length > 1 && s.All(char.IsDigit) ? s.TrimStart('0') is { Length: > 0 } t ? t : "0" : s));
    }

    /// <summary>La serie: la chiave senza il numero progressivo finale.</summary>
    public static string Serie(string nome)
    {
        var k = Di(nome);
        var i = k.LastIndexOf('-');
        return i > 0 && k[(i + 1)..].All(char.IsDigit) ? k[..i] : k;
    }
}
