using System.Text.RegularExpressions;

namespace StockStudio.Shared.Pipeline;

/// <summary>
/// Il nome di un file senza i segni di copia che ci aggiungono il sistema operativo e il browser.
///
/// Serve a riconoscere un'immagine già passata dalla pipeline quando torna con un nome appena
/// diverso. Misurato l'11/10/2026: «Collie_dog_illustrations_in_poses_2K_20260919184233 copia» è
/// salita su Adobe insieme all'originale, e «… (1)» è il nome che un browser dà al secondo
/// scaricamento dello stesso file.
///
/// Si tolgono solo i segni che nessuno scrive apposta -- « copia», « - Copia», « copy», « (1)»,
/// anche ripetuti -- e nient'altro. Un « 2» o un « b» in fondo restano: «Akita… a» e «Akita… b»
/// sono ritagli diversi dello stesso foglio, e scambiarli per copie farebbe avvisare a vuoto.
/// </summary>
public static class NomeDiCopia
{
    private static readonly Regex Segno = new(
        @"(?:\s*-\s*(?:copia|copy)(?:\s*\(\d+\))?|\s+(?:copia|copy)(?:\s+\d+)?|\s*\(\d+\))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Il nome senza estensione, ripulito dai segni di copia in fondo.</summary>
    public static string Originale(string? radice)
    {
        var r = (radice ?? string.Empty).Trim();
        while (true)
        {
            var senza = Segno.Replace(r, string.Empty).TrimEnd();
            // Un nome che è solo il segno («copia», «(1)») resta com'è: un nome vuoto non
            // identifica niente, e confrontarlo troverebbe qualunque cosa.
            if (senza.Length == 0 || senza == r) return r;
            r = senza;
        }
    }
}
