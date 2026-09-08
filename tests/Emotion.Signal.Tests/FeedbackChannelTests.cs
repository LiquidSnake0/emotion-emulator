using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class FeedbackChannelTests
{
    private static string Neuf() =>
        Path.Combine(Path.GetTempPath(), "emotion-fb-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Une_ligne_de_cache_exactement()
    {
        // Le lecteur CUDA s'aligne sur cette taille : elle ne doit pas deriver du cote .NET
        // sans qu'un test le voie.
        Assert.Equal(RenderFeedback.Size,
                     System.Runtime.InteropServices.Marshal.SizeOf<RenderFeedback>());
        Assert.Equal(64, RenderFeedback.Size);
    }

    [Fact]
    public void Rien_n_a_ete_publie_ne_rend_rien()
    {
        var chemin = Neuf();
        try
        {
            using var c = new FeedbackChannel(chemin);
            Assert.False(c.TryRead(out _));
        }
        finally { File.Delete(chemin); }
    }

    [Fact]
    public void Ce_qui_est_publie_se_relit_a_l_identique()
    {
        var chemin = Neuf();
        try
        {
            using var ecrivain = new FeedbackChannel(chemin);
            using var lecteur = new FeedbackChannel(chemin);

            ecrivain.Publish(RenderFeedback.For(
                sequence: 4242, packetTimeMs: 123456, renderMs: 6.5f, fps: 59.7f, dropped: 2));

            Assert.True(lecteur.TryRead(out var f));
            Assert.Equal(4242u, f.Sequence);
            Assert.Equal(123456, f.PacketTimeMs);
            Assert.Equal(6.5f, f.RenderMs, 3);
            Assert.Equal(59.7f, f.Fps, 3);
            Assert.Equal(2u, f.Dropped);
        }
        finally { File.Delete(chemin); }
    }

    /// <summary>
    /// LE LECTEUR NE DOIT JAMAIS VOIR UNE STRUCTURE A MOITIE ECRITE.
    ///
    /// C'est le seul risque du canal : l'unite de rendu ecrit pendant que l'analyse lit, et
    /// une lecture qui tomberait au milieu donnerait un retard fantaisiste. Le compteur de
    /// version l'interdit — ce test le verifie en faisant tourner les deux en meme temps
    /// pendant des milliers d'echanges, et en exigeant que chaque lecture soit coherente
    /// avec elle-meme.
    /// </summary>
    [Fact]
    public void Rien_n_est_lu_a_moitie_ecrit()
    {
        var chemin = Neuf();
        try
        {
            using var ecrivain = new FeedbackChannel(chemin);
            using var lecteur = new FeedbackChannel(chemin);

            var stop = false;
            var incoherences = 0;

            var ecriture = Task.Run(() =>
            {
                for (uint i = 1; i <= 20_000 && !stop; i++)
                    // Tous les champs sont derives de i : une lecture panachee se verrait.
                    ecrivain.Publish(RenderFeedback.For(i, i * 10, i * 0.5f, i, i));
            });

            var lecture = Task.Run(() =>
            {
                for (var n = 0; n < 20_000; n++)
                {
                    if (!lecteur.TryRead(out var f)) continue;

                    var i = f.Sequence;
                    if (f.PacketTimeMs != i * 10 || Math.Abs(f.RenderMs - i * 0.5f) > 0.001f
                        || Math.Abs(f.Fps - i) > 0.001f || f.Dropped != i)
                        Interlocked.Increment(ref incoherences);
                }
            });

            Task.WaitAll(ecriture, lecture);
            stop = true;

            Assert.Equal(0, incoherences);
        }
        finally { File.Delete(chemin); }
    }
}
