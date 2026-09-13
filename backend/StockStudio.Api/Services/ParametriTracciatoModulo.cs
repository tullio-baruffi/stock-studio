using System.Text.Json;
using StockStudio.Shared.Vettoriale;

namespace StockStudio.Api.Services;

/// <summary>
/// I numeri del tracciato come arrivano dal browser: **tutti facoltativi**.
///
/// ## Perche' nullable, e non un <see cref="ParametriTracciato"/> intero
/// Perche' "non ho detto niente" e "ho scelto zero" sono due cose diverse, e su questi parametri la
/// differenza si vede: zero granelli vuol dire "non togliere niente", mentre non averlo detto vuol
/// dire "usa la taratura configurata". Con un oggetto a campi non nullabili, un modulo che non
/// porta quel campo -- una versione vecchia della pagina, una chiamata fatta a mano -- direbbe zero
/// senza volerlo, e il tracciato uscirebbe pieno di granelli senza che nessuno l'abbia chiesto.
///
/// Cosi' invece ogni campo assente lascia il posto al predefinito, e una pagina che ne conosce
/// meta' continua a funzionare per la meta' che conosce.
///
/// ## Perche' si legge sempre da JSON, anche dal modulo di caricamento
/// Perche' i valori di un modulo multipart ASP.NET li converte con la cultura **del server**. Su
/// una macchina italiana il punto di "2.7" e' un separatore di migliaia: quel numero diventerebbe
/// ventisette, il controllo sui limiti lo riporterebbe a dodici, e la fedelta' del tracciato
/// sarebbe al massimo senza che nessuno l'abbia chiesto e senza un errore da nessuna parte -- il
/// genere di difetto che si scopre solo guardando i file consegnati. Il JSON invece si legge sempre
/// con la cultura invariante, e cosi' le due strade che portano qui leggono gli stessi numeri.
/// </summary>
public class ParametriTracciatoModulo
{
    /// <summary>
    /// Come System.Text.Json interpreta questi campi: gli stessi nomi che scrive il browser, in
    /// minuscolo, e nessuna tolleranza per quel che non si riconosce.
    /// </summary>
    private static readonly JsonSerializerOptions Lettura = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// I parametri scritti in un campo JSON, o null se il campo non c'era.
    /// </summary>
    /// <exception cref="JsonException">Quando il campo c'e' ma non e' leggibile.</exception>
    public static ParametriTracciatoModulo? DaJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<ParametriTracciatoModulo>(json, Lettura);
    }

    public int? Colori { get; set; }
    public double? Unione { get; set; }
    public int? Rumore { get; set; }
    public int? Lisciatura { get; set; }
    public int? Granelli { get; set; }
    public double? Morbidezza { get; set; }
    public int? Giri { get; set; }
    public double? Tolleranza { get; set; }
    public double? Angolo { get; set; }

    /// <summary>Vero quando chi ha compilato il modulo ha scelto almeno una cosa.</summary>
    public bool Qualcosa =>
        Colori.HasValue || Unione.HasValue || Rumore.HasValue || Lisciatura.HasValue
        || Granelli.HasValue || Morbidezza.HasValue || Giri.HasValue
        || Tolleranza.HasValue || Angolo.HasValue;

    /// <summary>
    /// La taratura di partenza con sopra le scelte di chi ha caricato, e il tutto riportato dentro
    /// i limiti in cui ha senso. Null quando non e' stato scelto niente: cosi' chi chiama sa che
    /// puo' lasciare il predefinito invece di riscriverlo.
    /// </summary>
    public ParametriTracciato? Su(ParametriTracciato partenza)
    {
        if (!Qualcosa) return null;

        var p = partenza ?? ParametriTracciato.Predefiniti;
        return new ParametriTracciato
        {
            NumeroColori = Colori ?? p.NumeroColori,
            SogliaUnione = Unione ?? p.SogliaUnione,
            RiduzioneRumore = Rumore ?? p.RiduzioneRumore,
            RaggioLisciatura = Lisciatura ?? p.RaggioLisciatura,
            Granelli = Granelli ?? p.Granelli,
            Morbidezza = Morbidezza ?? p.Morbidezza,
            GiriLisciatura = Giri ?? p.GiriLisciatura,
            Tolleranza = Tolleranza ?? p.Tolleranza,
            AngoloSpigolo = Angolo ?? p.AngoloSpigolo,
        }.Convalidato();
    }
}
