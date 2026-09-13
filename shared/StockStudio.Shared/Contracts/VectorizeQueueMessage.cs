using Newtonsoft.Json;
using StockStudio.Shared.Vettoriale;

namespace StockStudio.Shared.Contracts
{
    /// <summary>
    /// Message that starts the durable path: the web application drops the original in blob
    /// storage and enqueues this, then it is out of the picture.
    ///
    /// Shared contract: the API writes it, the Function reads it. Keeping the shape in one place
    /// is what stops the two from drifting apart silently.
    /// </summary>
    public class VectorizeQueueMessage
    {
        /// <summary>Blob holding the uploaded original, inside the originals container.</summary>
        public string? BlobName { get; set; }

        /// <summary>Name the author uploaded, kept for the deliverables and the metadata hint.</summary>
        public string? OriginalFileName { get; set; }

        /// <summary>
        /// Cosa fare del file: "vector" traccia in bianco e nero, "colore" traccia a colori,
        /// "raster" consegna l'immagine com'e'. Vuoto o sconosciuto vale "auto": si guarda
        /// l'immagine e si decide fra silhouette e colori.
        /// </summary>
        public string? Mode { get; set; }

        /// <summary>
        /// Quante tinte nel tracciato a colori. Null usa il valore predefinito.
        ///
        /// Ogni tinta e' una passata di potrace: il numero non e' una preferenza estetica ma la
        /// misura di quanto lavoro si sta chiedendo.
        /// </summary>
        public int? Colori { get; set; }

        /// <summary>
        /// Quanto insistere nel rimettere insieme le tinte che descrivono la stessa cosa: un manto
        /// ombreggiato diviso fra due marroni indistinguibili, un contorno tracciato due volte.
        /// Null usa il valore predefinito, zero disattiva del tutto quella passata.
        ///
        /// Viaggia nel messaggio invece di stare nella configurazione della Function perche' e' una
        /// scelta che si fa **guardando l'immagine**, non una proprieta' dell'installazione: due
        /// disegni consegnati nello stesso minuto possono volerne due valori diversi.
        /// </summary>
        public double? Unione { get; set; }

        /// <summary>
        /// Come disegnare i contorni: quanto rumore togliere, quanto lisciare, con quanta fedelta'
        /// ridurre a curve. Null lascia la taratura di serie.
        ///
        /// Vale per questo messaggio lo stesso motivo di <see cref="Unione"/>: sono scelte che si
        /// fanno guardando il disegno. <see cref="Colori"/> e <see cref="Unione"/> restano campi a
        /// se' perche' c'erano gia' e ci sono messaggi in coda che li usano; quando sono valorizzati
        /// vincono loro, cosi' un messaggio vecchio continua a voler dire quel che voleva dire.
        /// </summary>
        public ParametriTracciato? Tracciato { get; set; }

        /// <summary>
        /// I parametri completi del tracciato, tenendo conto dei due campi storici.
        /// </summary>
        public ParametriTracciato ParametriDiTracciato()
        {
            var p = Tracciato ?? ParametriTracciato.Predefiniti;
            if (Colori.HasValue) p.NumeroColori = Colori.Value;
            if (Unione.HasValue) p.SogliaUnione = Unione.Value;
            return p.Convalidato();
        }

        /// <summary>
        /// Luminance cut, 0-255, chosen by the author while looking at the preview in the browser.
        /// Null leaves the decision to Otsu.
        ///
        /// Otsu reads the histogram and splits it where the two halves are furthest apart, which is
        /// right for a picture with a clear subject and wrong for a pale drawing on a pale ground —
        /// exactly the case where the author can see what the machine cannot. Carrying the number
        /// here keeps that judgement without bringing the tracing back into the web application.
        /// </summary>
        public int? Threshold { get; set; }

        public override string ToString() => JsonConvert.SerializeObject(this);
    }
}
