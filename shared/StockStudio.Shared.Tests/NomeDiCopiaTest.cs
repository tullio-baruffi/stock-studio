using StockStudio.Shared.Pipeline;

namespace StockStudio.Shared.Tests;

/// <summary>
/// Quali nomi valgono come copia di un altro.
///
/// Sbagliare per eccesso fa avvisare a vuoto su immagini nuove; sbagliare per difetto lascia
/// risalire sui marketplace un lavoro già pubblicato.
/// </summary>
public class NomeDiCopiaTest
{
    [Theory]
    [InlineData("Collie_dog_illustrations_in_poses_2K_20260919184233 copia", "Collie_dog_illustrations_in_poses_2K_20260919184233")]
    [InlineData("Volpe2 - Copia", "Volpe2")]
    [InlineData("Volpe2 - Copia (2)", "Volpe2")]
    [InlineData("Volpe2 copia 2", "Volpe2")]
    [InlineData("Volpe2 Copy", "Volpe2")]
    [InlineData("Volpe2 - Copy", "Volpe2")]
    [InlineData("Illustrazione_di_un_gallo (1)", "Illustrazione_di_un_gallo")]
    [InlineData("Volpe2(3)", "Volpe2")]
    [InlineData("Volpe2 copia (1)", "Volpe2")]
    public void TogliIlSegnoDiCopia(string nome, string atteso)
    {
        Assert.Equal(atteso, NomeDiCopia.Originale(nome));
    }

    [Theory]
    [InlineData("Akita_dog_in_funny_poses_2K_20260919184425 b")]
    [InlineData("Akita_dog_in_funny_poses_2K_20260919184425 2")]
    [InlineData("AB34")]
    [InlineData("Scopia")]
    [InlineData("Una copiatrice")]
    public void LasciaStareINomiCheNonSonoCopie(string nome)
    {
        Assert.Equal(nome, NomeDiCopia.Originale(nome));
    }

    [Theory]
    [InlineData("copia")]
    [InlineData("(1)")]
    [InlineData("")]
    public void NonRiduceAUnNomeVuoto(string nome)
    {
        Assert.Equal(nome, NomeDiCopia.Originale(nome));
    }
}
