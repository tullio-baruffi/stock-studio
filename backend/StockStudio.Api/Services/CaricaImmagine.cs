using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StockStudio.Shared.Vettoriale;

namespace StockStudio.Api.Services;

/// <summary>
/// Carica un'immagine come RGB **componendo la trasparenza su bianco**.
///
/// Serve dovunque si legga un file per poi guardarlo o mostrarlo, perche' caricare direttamente in
/// RGB butta via il canale alfa e lascia quel che c'e' sotto -- di norma nero. Un logo ritagliato
/// diventa cosi' un logo su fondo nero: sbagliato in un vettoriale, e altrettanto sbagliato in
/// un'anteprima mandata a un modello che deve descriverla, che scriverebbe "su sfondo nero" fra le
/// parole chiave.
///
/// Vedi <see cref="Trasparenza"/> per il perche' si componga su bianco e non su nero.
/// </summary>
internal static class CaricaImmagine
{
    public static async Task<Image<Rgb24>> SuBiancoAsync(string percorso, CancellationToken ct)
    {
        using var originale = await Image.LoadAsync<Rgba32>(percorso, ct);
        return SuBianco(originale);
    }

    public static Image<Rgb24> SuBianco(byte[] contenuto)
    {
        using var originale = Image.Load<Rgba32>(contenuto);
        return SuBianco(originale);
    }

    private static Image<Rgb24> SuBianco(Image<Rgba32> originale)
    {
        var rgba = new byte[originale.Width * originale.Height * 4];
        originale.CopyPixelDataTo(rgba);
        return Image.LoadPixelData<Rgb24>(Trasparenza.SuBianco(rgba), originale.Width, originale.Height);
    }
}
