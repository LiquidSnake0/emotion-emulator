namespace Emotion.Signal;

/// <summary>
/// Fabrique un signal plausible a partir d'un tempo, sans aucune carte son.
///
/// Ce n'est pas un bouche-trou : c'est ce qui permet de construire et de regler tout
/// le visuel avant d'avoir la table branchee, et de rejouer deux fois exactement la
/// meme sequence quand on compare deux versions d'un shader. Les valeurs sont
/// deterministes pour une graine donnee.
///
/// Ce qu'elle imite du barber beats : un kick sur chaque temps, des charleys sur les
/// contretemps, des nappes qui bougent lentement dans les mediums.
/// </summary>
public sealed class MockAudioSource : IAudioSource
{
    private readonly float _bpm;
    private readonly int _seed;
    private readonly TimeSpan _tick;

    /// <param name="bpm">Tempo impose. Le crate vit entre 82 et 97.</param>
    /// <param name="fps">Images par seconde. 60 pour coller au rafraichissement du renderer.</param>
    /// <param name="seed">Graine des nappes, pour rejouer la meme sequence.</param>
    public MockAudioSource(float bpm = 87f, int fps = 60, int seed = 1203)
    {
        if (bpm <= 0) throw new ArgumentOutOfRangeException(nameof(bpm));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

        _bpm = bpm;
        _seed = seed;
        _tick = TimeSpan.FromSeconds(1.0 / fps);
    }

    public string Name => $"mock {_bpm:0.#} BPM";

    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        long lastBeatIndex = -1;

        while (!ct.IsCancellationRequested)
        {
            var t = (long)(DateTime.UtcNow - start).TotalMilliseconds;
            var frame = At(t, ref lastBeatIndex);
            yield return frame;

            try { await Task.Delay(_tick, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    /// <summary>
    /// Le signal a un instant donne. Extrait de la boucle pour que les tests
    /// interrogent n'importe quel instant sans attendre l'horloge.
    /// </summary>
    /// <param name="lastBeatIndex">
    /// Dernier temps deja annonce. <see cref="VisualFrame.Beat"/> est une impulsion :
    /// il ne doit etre vrai que sur la premiere image apres l'attaque, sinon le
    /// renderer declencherait son effet a chaque image de la duree du temps.
    /// </param>
    public VisualFrame At(long t, ref long lastBeatIndex)
    {
        var beatMs = 60_000.0 / _bpm;
        var beats = t / beatMs;              // temps ecoules, partie decimale = position dans le temps
        var beatIndex = (long)Math.Floor(beats);
        var inBeat = (float)(beats - beatIndex);

        var beat = beatIndex > lastBeatIndex;
        if (beat) lastBeatIndex = beatIndex;

        // Kick : attaque nette, retombee rapide. Porte les trois bandes graves.
        var kick = Decay(inBeat, 8f);

        // Charley sur les contretemps, plus court et plus discret.
        var offbeat = (inBeat + 0.5f) % 1f;
        var hat = Decay(offbeat, 26f) * 0.55f;

        var bands = new float[VisualFrame.BandCount];
        for (var i = 0; i < bands.Length; i++)
        {
            var x = i / (float)(bands.Length - 1);          // 0 grave, 1 aigu
            var pad = Pad(t, i);                            // nappe lente, propre a la bande
            var low = kick * Falloff(x, 0f, 0.30f);         // le kick n'existe que dans le bas
            var high = hat * Falloff(x, 1f, 0.35f);         // le charley que dans le haut
            var mid = pad * Falloff(x, 0.45f, 0.55f) * 0.7f;

            bands[i] = Clamp01(low + high + mid);
        }

        // Le niveau global suit surtout le kick : c'est ce qu'on entend.
        var rms = Clamp01(0.25f + 0.55f * kick + 0.20f * Average(bands));

        // Phase sur quatre temps : de quoi animer une figure a l'echelle de la mesure.
        var beatInBar = (int)(((beatIndex % 4) + 4) % 4);
        var phase = (float)(((beats % 4) + 4) % 4 / 4.0);

        // Les trois registres, sur le motif le plus courant du repertoire : kick sur
        // chaque temps, clap sur le deux et le quatre, charley sur les contretemps.
        //
        // Sans eux le mock ne declencherait plus rien a l'ecran, ce qui lui oterait sa
        // raison d'etre : il existe pour regler le visuel sans materiel. Un signal
        // fabrique doit rester coherent avec le contrat que remplit un vrai signal,
        // sinon il ne simule plus rien.
        var hits = new Hits(
            Kick: beat,
            Clap: beat && (beatInBar == 1 || beatInBar == 3),
            Hat:  CrossedOffbeat(beats));

        // Les instruments tonals, le timbre et la structure. Le mock les avait deja
        // perdus une fois, et plus aucun effet ne partait en mode simule : il existe pour
        // regler le visuel sans materiel, et un signal fabrique qui ne remplit pas le
        // meme contrat qu'un vrai ne simule plus rien. La regle vaut a chaque fois que le
        // contrat s'etend.
        // LES SIX REGISTRES, CHACUN AVEC SON RYTHME PROPRE.
        //
        // Sans eux, les six cases du renderer restent vides en mode simule et le visuel ne
        // peut plus etre regle sans materiel — ce qui est precisement la raison d'etre de
        // ce fichier. Chaque registre recoit donc un cycle de niveau, un contour et une
        // subdivision d'attaque qui lui sont propres : six formes identiques ne
        // permettraient pas de verifier qu'on les distingue.
        var niveaux = new float[Voices.Registers];
        var contours = new float[Voices.Registers];
        var voies = new LaneState[Voices.Registers];
        var attaques = 0;

        for (var r = 0; r < Voices.Registers; r++)
        {
            // Des periodes premieres entre elles : les six ne retombent jamais en phase,
            // et l'on voit donc six mouvements et non un seul repete six fois.
            var periode = new[] { 8f, 6f, 5f, 3f, 2f, 1.5f }[r];
            var niveau = Clamp01(0.18f + 0.55f * MathF.Abs(
                MathF.Sin((float)(beats * MathF.PI / periode))));

            niveaux[r] = niveau;
            contours[r] = 0.5f + 0.42f * MathF.Sin((float)(beats * MathF.PI / (periode * 0.7f)));

            // Une attaque toutes les n croches, n changeant d'un registre a l'autre.
            if (beat && ((int)beats % (r + 2)) == 0) attaques |= 1 << r;

            // LA MATURITE MONTE A DES VITESSES DIFFERENTES, ET C'EST LE POINT.
            //
            // Sur un vrai morceau, une source se tient en quelques secondes et une autre
            // met une minute ; deux registres graves partages entre le kick et la basse ne
            // se tiennent jamais. Le mock reproduit cet etagement — sinon la progression
            // que le renderer affiche ne pourrait pas etre reglee sans materiel.
            // Assez ecoutee : une question de duree, et elle se resout en quelques
            // secondes pour toutes. Nettete : une propriete de la bande, qui ne bouge pas
            // avec le temps. Les deux etaient confondues, et la barre trompait.
            var ecoute = Clamp01((float)(t / 1000.0) / (4f + r * 0.8f));
            var nette = new[] { 0.05f, 0.30f, 0.45f, 0.90f, 0.60f, 0.80f }[r];
            // L'ENVELOPPE AUSSI, ET LE MOCK LUI DONNE SIX VALEURS DIFFERENTES.
            //
            // Il a deja perdu le contrat deux fois en s'etendant, et plus aucun effet ne
            // partait en mode simule. Un test verifie desormais qu'aucun champ ne reste a
            // sa valeur par defaut — mais un mock qui remplirait les six sources de la
            // meme valeur passerait ce test tout en rendant l'enveloppe intestable a l'oeil.
            // On etale donc : du souffle pur au bas, de la corde pincee en haut.
            var pique = r / 5f;
            var tenue = 1f - r / 6f;
            voies[r] = new LaneState(niveau, contours[r], (attaques >> r & 1) != 0,
                                     Heard: ecoute, Sharpness: nette,
                                     Brightness: r / 5f,
                                     Texture: 0.3f + r * 0.1f,
                                     Pique: pique, Tenue: tenue,
                                     // Une source sur six se retire, pour que l'affichage
                                     // des absentes se regle sans materiel.
                                     Retrait: r == 4 ? 3f : 0f);
        }

        // Un nom se pose sur une source mure, et sur elle seule. Zero partout ailleurs :
        // le renderer doit montrer une case anonyme tant qu'elle l'est.
        var noms = new int[Voices.Registers];
        for (var r = 0; r < Voices.Registers; r++)
            if (voies[r].Confidence > 0.8f) noms[r] = r + 1;

        var voices = new Voices(
            Low: Clamp01(0.25f + kick * 0.5f),
            Mid: Clamp01(0.30f + Pad(t, 5) * 0.6f),
            High: Clamp01(0.15f + hat * 0.7f),
            LowHit: beat,
            MidHit: beat && beatInBar == 2,
            HighHit: CrossedOffbeat(beats),
            // Un contour melodique fabrique : le medium monte et redescend sur quatre
            // mesures, l'aigu sur deux. Sans lui, rien ne bougerait verticalement en mode
            // simule et le geste resterait introuvable a regler sans materiel.
            LowPitch: 0.5f + 0.25f * MathF.Sin((float)(beats * MathF.PI / 8)),
            MidPitch: 0.5f + 0.35f * MathF.Sin((float)(beats * MathF.PI / 8)),
            HighPitch: 0.5f + 0.30f * MathF.Sin((float)(beats * MathF.PI / 4)),
            Levels: niveaux, Pitches: contours, Hits: attaques,
            Lanes: voies, Labels: noms);

        // Le filtre s'ouvre et se ferme lentement, sur seize mesures : c'est le geste que
        // le DJ fera le plus souvent a la table, et il doit pouvoir le regler sans table.
        var cycle = (float)((t / 1000.0 / (beatMs * 64 / 1000.0)) % 1.0);
        var openness = 0.45f + 0.55f * (0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau));
        var timbre = new Timbre(
            Centroid: Clamp01(0.35f + openness * 0.35f),
            Rolloff: openness,
            Openness: openness,
            Density: Clamp01(0.35f + hat * 0.3f));

        // La structure est fabriquee, pas mesuree : le mock connait sa propre grille, il
        // n'a donc rien a estimer. La tension suit le meme cycle que le filtre, ce qui
        // donne une montee toutes les seize mesures et une rupture a son sommet.
        var bar = (int)((beatIndex / 4) % Structure.DefaultPhraseBars);
        var barStart = beat && beatInBar == 0;
        var buildup = Clamp01((cycle - 0.55f) / 0.35f);
        var structure = new Structure(
            Beat: beatInBar,
            Bar: bar,
            PhrasePos: (float)((beatIndex % (Structure.DefaultPhraseBars * 4) + inBeat) / (Structure.DefaultPhraseBars * 4)),
            Confidence: 1f,
            Buildup: buildup,
            Drop: barStart && buildup > 0.9f,
            BarStart: barStart,
            PhraseStart: barStart && bar == 0,
            // Le mock a deja perdu le contrat deux fois en s'etendant, et plus aucun effet
            // ne partait en mode simule. La phase du temps y est donc remplie des sa
            // naissance : ici on la connait exactement, puisque c'est nous qui la posons.
            BeatPhase: inBeat);

        // Les gestes, deduits du meme cycle : quand le filtre se ferme, l'etat suit. Le
        // mock doit remplir le contrat entier, sous peine de laisser l'ecran eteint en
        // mode simule — cela s'est deja produit deux fois.
        var gestures = new Gestures(
            FilterClosed: openness < 0.55f,
            BassCut: bands[0] < 0.12f,
            Dense: timbre.Density > 0.55f);

        return new VisualFrame(t, rms, bands, beat, phase, _bpm,
            Hits: hits, Voices: voices, Timbre: timbre, Structure: structure,
            Gestures: gestures,
            // Le mock connait son propre morceau : il est pret d'emblee, et le dit. Sans
            // cela, le feu vert ne partirait jamais en mode simule et la bascule visuelle
            // resterait introuvable a regler sans materiel.
            Readiness: new Readiness(1f, true, "mock", 0f),
            // Le mock joue une grille exacte : ses familles de frappes tombent par
            // construction sur des rapports francs du temps, donc l'accord est plein. Le
            // dire compte — un champ laisse a sa valeur par defaut a deja deux fois vide
            // l'ecran en mode simule, et un test verifie desormais qu'aucun ne le reste.
            GridAgreement: 1f);
    }

    private double _lastOffbeat = -1;

    /// <summary>
    /// Vrai sur la seule image qui franchit un contretemps. Meme regle que pour le
    /// temps : une impulsion, jamais un etat.
    /// </summary>
    private bool CrossedOffbeat(double beats)
    {
        var half = Math.Floor(beats * 2);
        if (half <= _lastOffbeat) return false;
        _lastOffbeat = half;
        return half % 2 == 1;          // les demi-temps impairs sont les contretemps
    }

    /// <summary>Enveloppe percussive : 1 a l'attaque, decroissance exponentielle.</summary>
    private static float Decay(float x, float rate) => MathF.Exp(-rate * x);

    /// <summary>Cloche centree sur <paramref name="center"/>, pour repartir une source sur les bandes.</summary>
    private static float Falloff(float x, float center, float width)
    {
        var d = (x - center) / width;
        return MathF.Exp(-d * d);
    }

    /// <summary>
    /// Nappe : somme de trois sinus incommensurables, donc jamais tout a fait la meme
    /// forme deux fois, mais entierement determinee par t et par la graine.
    /// </summary>
    private float Pad(long t, int band)
    {
        var s = (_seed + band * 37) * 0.001f;
        var sec = t / 1000f;
        var v = MathF.Sin(sec * 0.31f + s)
              + MathF.Sin(sec * 0.53f + s * 2.1f)
              + MathF.Sin(sec * 0.11f + s * 4.7f);
        return (v / 3f + 1f) / 2f;   // ramene de [-1,1] a [0,1]
    }

    private static float Average(float[] xs)
    {
        var sum = 0f;
        foreach (var x in xs) sum += x;
        return xs.Length == 0 ? 0f : sum / xs.Length;
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
