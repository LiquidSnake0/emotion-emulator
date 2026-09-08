using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class GridAgreementTests
{
    /// <summary>
    /// Des frappes parfaitement en mesure confirment le tempo. C'est le cas nominal, et il
    /// verifie que le mecanisme sait dire oui.
    /// </summary>
    [Fact]
    public void Des_frappes_en_mesure_confirment_le_tempo()
    {
        var fam = new EventFamilies();
        var accord = new GridAgreement(fam);

        const float bpm = 90f;
        var tempsMs = (long)(60_000f / bpm);         // 666 ms

        // Une famille sur le temps, une autre sur la croche.
        var surLeTemps = fam.Ranger(new EventSignature(0.2f, 0.3f, 0.1f));
        var surLaCroche = fam.Ranger(new EventSignature(0.7f, 0.3f, 0.1f));

        for (var i = 0; i < 40; i++)
        {
            fam.Ranger(new EventSignature(0.2f, 0.3f, 0.1f));
            accord.Frappe(surLeTemps, i * tempsMs);

            fam.Ranger(new EventSignature(0.7f, 0.3f, 0.1f));
            accord.Frappe(surLaCroche, i * tempsMs / 2);
        }

        accord.Juger(bpm);

        Assert.Equal(2, accord.Votantes);
        Assert.True(accord.Accord > 0.8f, $"accord {accord.Accord:F2}, attendu au-dessus de 0,8");
    }

    /// <summary>
    /// Et des frappes qui ne tombent sur aucune subdivision le contredisent. Sans cela le
    /// mecanisme dirait oui a tout, ce qui ne verifierait rien.
    /// </summary>
    [Fact]
    public void Des_frappes_hors_mesure_ne_confirment_rien()
    {
        var fam = new EventFamilies();
        var accord = new GridAgreement(fam);

        const float bpm = 90f;
        var tempsMs = 60_000f / bpm;

        var f = fam.Ranger(new EventSignature(0.2f, 0.3f, 0.1f));
        // Un intervalle a 1,37 temps : ni le temps, ni la croche, ni la noire pointee.
        for (var i = 0; i < 40; i++)
        {
            fam.Ranger(new EventSignature(0.2f, 0.3f, 0.1f));
            accord.Frappe(f, (long)(i * tempsMs * 1.37f));
        }

        accord.Juger(bpm);

        Assert.Equal(1, accord.Votantes);
        Assert.True(accord.Accord < 0.2f, $"accord {accord.Accord:F2}, attendu sous 0,2");
    }

    /// <summary>
    /// Sans tempo accroche, il n'y a rien a verifier et le mecanisme se tait plutot que de
    /// rendre un chiffre qui n'appuie sur rien.
    /// </summary>
    [Fact]
    public void Sans_tempo_le_mecanisme_se_tait()
    {
        var fam = new EventFamilies();
        var accord = new GridAgreement(fam);

        var f = fam.Ranger(new EventSignature(0.2f, 0.3f, 0.1f));
        for (var i = 0; i < 40; i++) accord.Frappe(f, i * 500);

        accord.Juger(null);

        Assert.Equal(0f, accord.Accord);
        Assert.Equal(0, accord.Votantes);
    }
}
