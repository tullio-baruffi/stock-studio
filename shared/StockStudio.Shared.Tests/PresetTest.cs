using System.Linq;
using StockStudio.Shared.Vettoriale;
using Xunit;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Che i preset siano tarature vere e non nomi con dentro numeri a caso.
///
/// Un preset sbagliato non produce un errore: produce un file consegnato male, e chi lo guarda non
/// ha modo di sapere che la colpa e' di un numero e non del disegno. Sono quindi i controlli che
/// contano di piu' su questo pezzo, perche' sostituiscono un giudizio che nessuno rifara' a mano.
/// </summary>
public class PresetTest
{
    [Fact]
    public void Ogni_preset_ha_un_codice_unico_e_non_vuoto()
    {
        var codici = Preset.Tutti.Select(p => p.Codice).ToList();
        Assert.All(codici, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        Assert.Equal(codici.Count, codici.Distinct().Count());
    }

    [Fact]
    public void Ogni_preset_si_spiega()
    {
        // Un preset senza "quando usarlo" e' un nome in un elenco: chi lo legge deve tirare a
        // indovinare, che e' esattamente il lavoro che i preset dovevano togliere.
        Assert.All(Preset.Tutti, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Nome));
            Assert.False(string.IsNullOrWhiteSpace(p.Famiglia));
            Assert.False(string.IsNullOrWhiteSpace(p.Descrizione));
            Assert.False(string.IsNullOrWhiteSpace(p.QuandoUsarlo));
        });
    }

    [Fact]
    public void Il_predefinito_esiste_ed_e_quello_che_misura()
    {
        var p = Preset.Trova(Preset.CodicePredefinito);
        Assert.NotNull(p);
        Assert.True(p!.MisuraLImmagine);
        Assert.Null(p.Parametri);
        // Deve stare in cima: chi apre l'elenco senza sapere cosa scegliere si ferma alla prima
        // voce, e la prima voce dev'essere quella che non puo' essere sbagliata.
        Assert.Equal(Preset.CodicePredefinito, Preset.Tutti[0].Codice);
    }

    [Fact]
    public void Solo_lautomatico_chiede_di_misurare_limmagine()
    {
        // E' la distinzione che costava una lettura dell'immagine buttata via: i preset in bianco
        // e nero non hanno numeri perche' non servono, non perche' vadano misurati.
        var misurano = Preset.Tutti.Where(p => p.MisuraLImmagine).Select(p => p.Codice).ToList();
        Assert.Equal(new[] { Preset.CodicePredefinito }, misurano);
    }

    [Fact]
    public void I_preset_a_colori_portano_numeri_e_quelli_in_bianco_e_nero_no()
    {
        foreach (var p in Preset.Tutti)
        {
            if (p.ModalitaTracciato == Modalita.BiancoENero)
            {
                // Li' il disegno lo fa la soglia: i cursori delle tinte non toccano niente, e
                // portarli sarebbe raccontare a chi guarda una taratura che non viene applicata.
                Assert.Null(p.Parametri);
                continue;
            }
            if (p.MisuraLImmagine) continue;
            Assert.NotNull(p.Parametri);
        }
    }

    [Fact]
    public void Ogni_preset_e_gia_dentro_i_limiti()
    {
        // Se la convalida cambiasse un numero, quel che l'interfaccia mostra e quel che il motore
        // applica sarebbero due cose diverse -- e il preset racconterebbe una taratura che non e'
        // la sua. Meglio accorgersene qui che guardando un file consegnato.
        foreach (var p in Preset.Tutti)
        {
            if (p.Parametri == null) continue;
            var c = p.Parametri.Convalidato();
            Assert.Equal(p.Parametri.NumeroColori, c.NumeroColori);
            Assert.Equal(p.Parametri.SogliaUnione, c.SogliaUnione);
            Assert.Equal(p.Parametri.RiduzioneRumore, c.RiduzioneRumore);
            Assert.Equal(p.Parametri.RaggioLisciatura, c.RaggioLisciatura);
            Assert.Equal(p.Parametri.Granelli, c.Granelli);
            Assert.Equal(p.Parametri.Morbidezza, c.Morbidezza);
            Assert.Equal(p.Parametri.GiriLisciatura, c.GiriLisciatura);
            Assert.Equal(p.Parametri.Tolleranza, c.Tolleranza);
            Assert.Equal(p.Parametri.AngoloSpigolo, c.AngoloSpigolo);
            Assert.Equal(p.Parametri.ScalaDiGrigi, c.ScalaDiGrigi);
        }
    }

    [Fact]
    public void La_fedelta_segue_il_nome_del_preset()
    {
        // "Alta fedelta'" deve dare curve piu' attaccate al bordo di "bassa fedelta'". Sembra
        // ovvio, e non lo e': in Illustrator lo stesso comando e' invertito rispetto al cursore
        // che si vede, ed e' il genere di dettaglio in cui un ricalco si sbaglia in silenzio.
        var alta = Preset.Trova("foto-alta-fedelta")!.Parametri!;
        var bassa = Preset.Trova("foto-bassa-fedelta")!.Parametri!;
        Assert.True(alta.Tolleranza < bassa.Tolleranza,
            $"alta fedelta' {alta.Tolleranza} dovrebbe essere piu' stretta di bassa {bassa.Tolleranza}");
        Assert.True(alta.NumeroColori > bassa.NumeroColori);
        Assert.True(alta.Granelli < bassa.Granelli);
    }

    [Fact]
    public void I_preset_a_tinte_contate_chiedono_le_tinte_che_dicono()
    {
        Assert.Equal(3, Preset.Trova("colori-3")!.Parametri!.NumeroColori);
        Assert.Equal(6, Preset.Trova("colori-6")!.Parametri!.NumeroColori);
        Assert.Equal(16, Preset.Trova("colori-16")!.Parametri!.NumeroColori);
    }

    [Fact]
    public void Con_poche_tinte_lunione_e_piu_decisa()
    {
        // Con poche tinte la riduzione puo' spaccare una campitura in due sfumature della stessa
        // tinta, e una delle due sparisce: la passata di unione va tirata su proprio li'.
        var tre = Preset.Trova("colori-3")!.Parametri!;
        var sedici = Preset.Trova("colori-16")!.Parametri!;
        Assert.True(tre.SogliaUnione > sedici.SogliaUnione);
    }

    [Fact]
    public void Il_line_art_a_colori_non_liscia_e_non_pulisce()
    {
        // E' la misura di Disegno, messa in un preset: su un disegno a tinte piatte l'originale non
        // liscia niente, e ogni sfocatura si legge come un difetto.
        var p = Preset.Trova("line-art-colori")!.Parametri!;
        Assert.Equal(0, p.RaggioLisciatura);
        Assert.Equal(0, p.RiduzioneRumore);
        Assert.False(p.LisciaturaAutomatica);
        Assert.True(p.Granelli < ParametriTracciato.Predefiniti.Granelli,
            "su un line art una macchiolina e' un occhio o una narice, non rumore");
        Assert.True(p.Tolleranza < ParametriTracciato.Predefiniti.Tolleranza,
            "i tratti sono sottili: una curva che puo' vagare quanto il tratto lo attraversa");
    }

    [Fact]
    public void Il_line_art_a_colori_resta_a_colori()
    {
        // E' il difetto che ha fatto consegnare silhouette nere al posto di line art blu: il
        // preset che si chiama "line art" deve essere quello che il colore lo tiene.
        var p = Preset.Trova("line-art-colori")!;
        Assert.Equal(Modalita.Colore, p.ModalitaTracciato);
        Assert.True(Modalita.EAColori(p.ModalitaTracciato));
    }

    [Fact]
    public void Solo_la_scala_di_grigi_toglie_il_colore()
    {
        var grigi = Preset.Tutti.Where(p => p.Parametri?.ScalaDiGrigi == true).Select(p => p.Codice);
        Assert.Equal(new[] { "grigi" }, grigi);
    }

    [Fact]
    public void Le_soglie_dei_preset_in_bianco_e_nero_sono_ordinate_come_il_loro_mestiere()
    {
        // Un line art scansionato vuole un taglio severo (resta solo l'inchiostro), una silhouette
        // un taglio largo (la sagoma si chiude), un disegno tecnico il piu' largo di tutti (le
        // linee chiare non vanno perse). Se quest'ordine si invertisse, i tre preset direbbero il
        // contrario di quel che fanno.
        var line = Preset.Trova("line-art-bn")!.Soglia!.Value;
        var silhouette = Preset.Trova("silhouette")!.Soglia!.Value;
        var tecnico = Preset.Trova("disegno-tecnico")!.Soglia!.Value;
        Assert.True(line < silhouette, $"line art {line} < silhouette {silhouette}");
        Assert.True(silhouette < tecnico, $"silhouette {silhouette} < tecnico {tecnico}");
        Assert.All(new[] { line, silhouette, tecnico }, s => Assert.InRange(s, 0, 255));
    }

    [Fact]
    public void Il_logo_in_bianco_e_nero_lascia_decidere_la_soglia_allimmagine()
    {
        // Un marchio puo' essere nero su bianco o bianco su nero: una soglia fissa ne sbaglierebbe
        // meta', mentre Otsu la legge dall'istogramma.
        Assert.Null(Preset.Trova("logo-bn")!.Soglia);
    }

    [Fact]
    public void Una_soglia_e_solo_sui_preset_in_bianco_e_nero()
    {
        // A colori la soglia non viene nemmeno letta: averla sarebbe una scelta che non arriva da
        // nessuna parte, cioe' la forma peggiore di configurazione.
        Assert.All(Preset.Tutti.Where(p => p.Soglia.HasValue),
                   p => Assert.Equal(Modalita.BiancoENero, p.ModalitaTracciato));
    }

    [Fact]
    public void Ogni_modalita_dichiarata_e_una_modalita_vera()
    {
        // Modalita.Normalizza manda a "automatico" tutto cio' che non riconosce: un refuso qui
        // non darebbe un errore, darebbe un preset che sceglie da solo invece di fare quel che
        // dice il suo nome.
        Assert.All(Preset.Tutti,
                   p => Assert.Equal(p.ModalitaTracciato, Modalita.Normalizza(p.ModalitaTracciato)));
    }

    [Fact]
    public void Nessun_preset_traccia_in_raster()
    {
        // "Raster" vuol dire non tracciare: un preset di tracciato che non traccia sarebbe una
        // voce che non fa niente in un elenco dove tutte le altre fanno qualcosa.
        Assert.All(Preset.Tutti, p => Assert.True(Modalita.EVettoriale(p.ModalitaTracciato)));
    }

    [Fact]
    public void Trova_ignora_maiuscole_e_spazi_ma_non_inventa()
    {
        Assert.NotNull(Preset.Trova("  COLORI-3 "));
        Assert.Equal("colori-3", Preset.Trova("Colori-3")!.Codice);
        // Un codice sconosciuto torna null invece di ripiegare sull'automatico: chi chiama deve
        // poter distinguere "non ha scelto" da "ha scelto una cosa che non esiste piu'".
        Assert.Null(Preset.Trova("preset-che-non-esiste"));
        Assert.Null(Preset.Trova(null));
        Assert.Null(Preset.Trova("   "));
    }

    [Fact]
    public void ParametriCopia_non_restituisce_loggetto_condiviso()
    {
        // L'elenco e' statico: chi ricevesse l'oggetto vero e ne cambiasse un numero -- che e'
        // esattamente quel che fa PerImmagine -- lo cambierebbe per tutte le richieste successive,
        // fino al riavvio. E' il difetto che non si riproduce mai in locale.
        var p = Preset.Trova("colori-6")!;
        var a = p.ParametriCopia()!;
        var b = p.ParametriCopia()!;
        Assert.NotSame(a, b);
        Assert.NotSame(a, p.Parametri);

        a.NumeroColori = 61;
        Assert.Equal(6, p.ParametriCopia()!.NumeroColori);
        Assert.Equal(6, p.Parametri!.NumeroColori);
    }

    [Fact]
    public void ParametriCopia_e_null_dove_non_ci_sono_numeri()
    {
        Assert.Null(Preset.Trova(Preset.CodicePredefinito)!.ParametriCopia());
        Assert.Null(Preset.Trova("logo-bn")!.ParametriCopia());
    }

    [Fact]
    public void I_numeri_dei_preset_sopravvivono_alladattamento_alla_grandezza()
    {
        // PerImmagine scala le lunghezze e i granelli: su un'immagine grande un preset non deve
        // finire contro un limite e diventare un altro preset. Il controllo e' che l'ordine fra i
        // preset resti quello anche dopo l'adattamento.
        foreach (var lato in new[] { 800, 3000, 8000 })
        {
            var alta = Preset.Trova("foto-alta-fedelta")!.Parametri!.PerImmagine(lato, lato / 2);
            var bassa = Preset.Trova("foto-bassa-fedelta")!.Parametri!.PerImmagine(lato, lato / 2);
            Assert.True(alta.Tolleranza < bassa.Tolleranza, $"a {lato} px");
            Assert.True(alta.Granelli < bassa.Granelli, $"a {lato} px");
        }
    }

    [Fact]
    public void La_scala_di_grigi_rende_uguali_i_tre_canali()
    {
        // Un rosso pieno, un verde pieno e un blu pieno: se si desaturasse con la media dei tre
        // canali uscirebbero tutti e tre uguali, e un disegno verde su blu diventerebbe un
        // rettangolo grigio. Con i pesi della luminanza restano tre grigi diversi.
        var rgb = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        var g = ParametriTracciato.SenzaColore(rgb);

        for (var i = 0; i < 9; i += 3)
        {
            Assert.Equal(g[i], g[i + 1]);
            Assert.Equal(g[i], g[i + 2]);
        }
        Assert.True(g[3] > g[0], "il verde deve restare piu' chiaro del rosso");
        Assert.True(g[0] > g[6], "il rosso deve restare piu' chiaro del blu");
    }

    [Fact]
    public void La_scala_di_grigi_non_modifica_i_pixel_di_partenza()
    {
        var rgb = new byte[] { 10, 20, 30 };
        var g = ParametriTracciato.SenzaColore(rgb);

        Assert.Equal(g[0], g[1]);
        // L'originale non deve essere stato modificato sul posto: chi chiama lo riusa per misurare
        // la sfumatura, che si stima sui pixel veri e non su quelli gia' ridotti.
        Assert.Equal(new byte[] { 10, 20, 30 }, rgb);
    }
}
