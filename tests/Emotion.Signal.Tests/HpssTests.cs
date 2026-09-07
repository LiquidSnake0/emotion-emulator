using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class HpssTests
{
    private const int Bins = 128;

    /// <summary>Une note tenue : une raie etroite, presente a chaque instant.</summary>
    private static float[] Note(int bin, float amp = 1f)
    {
        var s = new float[Bins];
        s[bin] = amp;
        s[bin - 1] = amp * 0.3f;
        s[bin + 1] = amp * 0.3f;
        return s;
    }

    /// <summary>Une frappe : large en frequence, sur un seul instant.</summary>
    private static float[] Hit(float amp = 1f)
    {
        var s = new float[Bins];
        for (var i = 0; i < Bins; i++) s[i] = amp;
        return s;
    }

    private static float[] Add(float[] a, float[] b)
    {
        var s = new float[Bins];
        for (var i = 0; i < Bins; i++) s[i] = a[i] + b[i];
        return s;
    }

    [Fact]
    public void Rien_ne_sort_tant_que_le_tampon_n_est_pas_plein()
    {
        // Au demarrage les composantes ne veulent rien dire : les livrer ferait
        // declencher tout ce qui ecoute.
        var h = new Hpss(Bins, timeFrames: 7);

        for (var i = 0; i < 6; i++)
            Assert.False(h.Feed(Note(40)));

        Assert.True(h.Feed(Note(40)));
    }

    [Fact]
    public void Une_note_tenue_part_du_cote_harmonique()
    {
        var h = new Hpss(Bins, timeFrames: 7);
        for (var i = 0; i < 9; i++) h.Feed(Note(40));

        var harm = h.Harmonic[40];
        var perc = h.Percussive[40];

        Assert.True(harm > perc * 5f,
            $"une note tenue devrait etre harmonique : h={harm:0.000} p={perc:0.000}");
    }

    [Fact]
    public void Une_frappe_isolee_part_du_cote_percussif()
    {
        var h = new Hpss(Bins, timeFrames: 7);

        // Du silence, une frappe au milieu, du silence : c'est la frappe qui sera jugee.
        h.Feed(new float[Bins]);
        h.Feed(new float[Bins]);
        h.Feed(new float[Bins]);
        h.Feed(Hit());
        h.Feed(new float[Bins]);
        h.Feed(new float[Bins]);
        h.Feed(new float[Bins]);

        var harm = h.Harmonic[64];
        var perc = h.Percussive[64];

        Assert.True(perc > harm * 5f,
            $"une frappe isolee devrait etre percussive : h={harm:0.000} p={perc:0.000}");
    }

    [Fact]
    public void Un_melange_est_reparti_entre_les_deux()
    {
        // Le cas reel : un piano tenu pendant qu'une frappe tombe. C'est exactement la
        // situation d'instamata, ou le piano brouillait la detection des claps.
        var h = new Hpss(Bins, timeFrames: 7);

        h.Feed(Note(40));
        h.Feed(Note(40));
        h.Feed(Note(40));
        h.Feed(Add(Note(40), Hit(0.8f)));   // la fenetre jugee
        h.Feed(Note(40));
        h.Feed(Note(40));
        h.Feed(Note(40));

        // Au bin de la note : l'harmonique domine malgre la frappe superposee.
        Assert.True(h.Harmonic[40] > h.Percussive[40],
            $"au bin de la note, h={h.Harmonic[40]:0.000} p={h.Percussive[40]:0.000}");

        // Loin de la note : il ne reste que la frappe.
        Assert.True(h.Percussive[100] > h.Harmonic[100],
            $"loin de la note, h={h.Harmonic[100]:0.000} p={h.Percussive[100]:0.000}");
    }

    [Fact]
    public void Les_deux_composantes_redonnent_l_original()
    {
        // Propriete des masques de Wiener : leur somme vaut exactement un. Un masque
        // binaire laisserait au contraire des trous nets dans le spectre, qui se voient.
        var h = new Hpss(Bins, timeFrames: 7);

        var mixed = Add(Note(40), Hit(0.5f));
        for (var i = 0; i < 4; i++) h.Feed(Note(40));
        h.Feed(mixed);
        for (var i = 0; i < 3; i++) h.Feed(Note(40));

        // La fenetre jugee est celle du milieu, trois avant la derniere.
        var h2 = new Hpss(Bins, timeFrames: 7);
        h2.Feed(Note(40)); h2.Feed(Note(40)); h2.Feed(Note(40));
        h2.Feed(mixed);
        h2.Feed(Note(40)); h2.Feed(Note(40)); h2.Feed(Note(40));

        for (var k = 1; k < Bins - 1; k++)
            Assert.Equal(mixed[k], h2.Harmonic[k] + h2.Percussive[k], 4);
    }

    [Fact]
    public void La_latence_vaut_la_moitie_de_la_fenetre()
    {
        // Elle est structurelle : pour savoir si un bin durait, il faut avoir vu la
        // suite. Elle doit donc etre annoncee, pas subie.
        Assert.Equal(3, new Hpss(Bins, timeFrames: 7).LatencyFrames);
        Assert.Equal(4, new Hpss(Bins, timeFrames: 9).LatencyFrames);
    }

    [Fact]
    public void Une_longueur_paire_est_refusee()
    {
        // Sans element central, il n'y a pas de fenetre a juger.
        Assert.Throws<ArgumentException>(() => new Hpss(Bins, timeFrames: 8));
        Assert.Throws<ArgumentException>(() => new Hpss(Bins, freqBins: 16));
    }
}
