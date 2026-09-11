namespace StockStudio.Api.Services.Integration;

/// <summary>
/// Gli indirizzi con cui il browser chiede le immagini a SharePoint.
///
/// Le anteprime non passano più dall'API: chi guarda ha accesso alla libreria, quindi il tag
/// &lt;img&gt; le chiede a SharePoint per conto suo e il nostro server resta fuori dal percorso
/// delle immagini. Questo però sposta un requisito sul browser -- deve avere una sessione
/// SharePoint valida -- ed è la ragione per cui esiste un pulsante che la stabilisce.
///
/// Queste due funzioni stavano scritte due volte, in Backoffice e in Bonifica. Due copie della
/// stessa costruzione divergono in silenzio appena una viene ritoccata, e qui il risultato non
/// sarebbe un errore ma un riquadro vuoto: il modo più difficile da diagnosticare. Una sola.
/// </summary>
public static class UrlSharePoint
{
    /// <summary>L'indirizzo assoluto del file, con ogni segmento del percorso codificato.</summary>
    public static string Diretto(string siteUrl, string serverRelativeUrl)
    {
        var host = new Uri(siteUrl).GetLeftPart(UriPartial.Authority);
        return host + string.Join("/", serverRelativeUrl.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// La miniatura già pronta di SharePoint. La risoluzione è una scala, non un numero di pixel:
    /// 2 dà il lato lungo intorno agli 800, che su una scheda da 168 basta anche a densità doppia.
    /// </summary>
    public static string Miniatura(string siteUrl, string serverRelativeUrl, int risoluzione = 2) =>
        $"{siteUrl.TrimEnd('/')}/_layouts/15/getpreview.ashx" +
        $"?path={Uri.EscapeDataString(Diretto(siteUrl, serverRelativeUrl))}&resolution={risoluzione}";
}
