namespace Emotion.Signal;

/// <summary>
/// Ce qui se repete, et tous les combien.
///
/// « Souvent on a un coup de piano qui n'est que quelques notes, genre huit notes ; ces
/// huit notes une fois passees repassent apres. C'est comme si on analysait une chanson
/// avec des paroles : on reconnait le refrain qui est en quatrain, des la premiere ecoute
/// on a cet indice, puis quand on l'entend une deuxieme fois on sait que c'est un refrain. »
///
/// ELLE A ETE MESUREE AVANT D'ETRE ECRITE. `outils/motif.py` a passe les dix morceaux du bac
/// et rend un verdict sans appel, obtenu avec deux juges independants :
///
///     z seul : depasser le meme morceau dont on a brasse les mesures
///     relief : depasser les AUTRES decalages, ce qu'aucune derive lente ne peut faire
///
///                                  z seul    z ET relief
///     les douze bandes ensemble     7/10        1/10
///     la meilleure bande seule     10/10        8/10
///
/// <b>Le melange ne porte pas le motif</b> — ses sept sur dix apparents etaient de la
/// derive, et le second juge les efface. <b>Une bande seule le porte</b>, a des decalages de
/// deux, quatre ou huit mesures. C'est exactement l'hypothese du DJ : le piano se repete
/// meme quand le reste change, et c'est pour cela qu'il faut le regarder seul.
///
/// SUR LES BANDES, ET SURTOUT PAS SUR LES SOURCES SEPAREES. Les activations de la separation
/// ne sont pas calees sur le temps — leur concentration est au niveau du hasard, ce qui a
/// tue le classement des roles — et elles sont si piquees qu'une source y est a zero plus
/// d'une image sur deux. Les bandes viennent directement du spectre.
///
/// LE « 1 » N'EST PAS NECESSAIRE, et c'est une propriete precieuse. Une correlation a un
/// decalage ne depend que de la PERIODE de la mesure, jamais de son origine. Le vote du
/// temps fort ne verrouille que six fois sur dix ; le motif s'en passe entierement.
///
/// CHAQUE BANDE CALCULE CHEZ ELLE, PUIS PASSE SA VALEUR AUX VOISINES.
///
/// C'est le schema que le DJ decrit — la propagation de chaleur sur une plaque, ou chaque
/// cellule calcule sa valeur et la transmet a cote pour que la suivante s'en serve. Ici la
/// grandeur qui diffuse est le vote pour un decalage : une bande qui trouve franchement une
/// periode de quatre mesures rend ses voisines plus promptes a la voir.
///
/// <b>La diffusion redistribue de l'information, elle n'en cree pas</b> — c'est pourquoi il
/// fallait d'abord mesurer que les bandes en portent. Et elle est ponderee par la certitude
/// de celui qui parle : une bande qui ne sait rien ne doit pas convaincre sa voisine.
/// </summary>
public sealed class MotifTracker
{
    /// <summary>Bandes suivies. Les douze du spectre.</summary>
    public const int Bandes = VisualFrame.BandCount;

    /// <summary>
    /// Pas par mesure dans la signature. Seize : la double croche.
    ///
    /// C'est l'unite ou un motif se lit. Une premiere version hors ligne moyennait chaque
    /// mesure sur toute sa duree et passait son critere sans rien prouver : neuf morceaux
    /// sur dix « reussis » pour cinq milliemes de marge, et le decalage gagnant toujours a
    /// une mesure. Moyenner detruit ce qui fait un motif — sa forme DANS LE TEMPS.
    /// </summary>
    public const int Pas = 16;

    /// <summary>
    /// Decalages examines, en mesures. De deux a huit.
    ///
    /// DEUX ET NON UN : un motif qui se repete a chaque mesure ne se distingue pas d'une
    /// texture constante, et c'est lui qui gagnait systematiquement tant qu'on le laissait
    /// concourir. Au-dela de huit on decrirait la section, ce que SectionTracker fait deja.
    /// </summary>
    public const int LagMin = 2;
    public const int LagMax = 8;
    public const int Lags = LagMax - LagMin + 1;

    /// <summary>Mesures gardees en memoire. Il en faut plus que le plus grand decalage.</summary>
    public const int Histoire = LagMax + 8;

    /// <summary>Temps par mesure.</summary>
    public const int TempsParMesure = 4;

    /// <summary>
    /// Part du vote d'une bande qui passe a chacune de ses deux voisines.
    ///
    /// Un cinquieme : assez pour qu'une bande sure entraine ses voisines, trop peu pour
    /// qu'une bande seule impose sa periode a toute la plaque. Au-dela, la diffusion cesse
    /// d'aider et se met a effacer les differences qu'on cherche justement a lire.
    /// </summary>
    public const float Diffusion = 0.2f;

    /// <summary>Oubli des votes, par mesure. Huit mesures de demi-vie.</summary>
    private const float Oubli = 0.917f;     // 0,5 ^ (1/8)

    /// <summary>
    /// Ressemblance minimale pour qu'un decalage compte, quelle que soit son avance sur les
    /// autres.
    ///
    /// LE RELIEF SEUL NE SUFFIT PAS EN DIRECT, et un test l'a montre : sur du bruit pur, le
    /// meilleur de sept decalages depasse les six autres de trois ecarts-types assez souvent
    /// pour designer une periode qui n'existe pas. Hors ligne, c'etait le brassage des
    /// mesures qui l'interdisait ; en direct on ne peut pas brasser, alors on exige que la
    /// ressemblance soit reelle et pas seulement superieure.
    ///
    /// Quinze centiemes : deux mesures de bruit se ressemblent a moins de cinq centiemes,
    /// deux mesures qui portent le meme motif a plus de trente.
    /// </summary>
    public const float RessemblanceMin = 0.15f;

    // La signature de la mesure en cours, et celles qui precedent.
    private readonly float[] _courante = new float[Pas * Bandes];
    private readonly float[][] _passees = new float[Histoire][];
    private int _rang;                       // combien de mesures closes
    private readonly int[] _compte = new int[Pas];

    // Le vote de chaque bande pour chaque decalage, avant et apres diffusion.
    private readonly float[,] _votes = new float[Bandes, Lags];
    private readonly float[,] _diffus = new float[Bandes, Lags];

    private double _phase;                   // dans la mesure
    private long _lastMs = -1;
    private float _beatMs = 690f;

    public MotifTracker()
    {
        for (var i = 0; i < Histoire; i++) _passees[i] = new float[Pas * Bandes];
    }

    /// <summary>Mesures closes depuis le debut. En dessous du plus grand decalage, on se tait.</summary>
    public int Mesures => _rang;

    /// <summary>
    /// Le decalage qui ressort le plus pour une bande, en mesures, ou zero.
    ///
    /// Zero et non « la valeur par defaut » : une valeur par defaut qui a l'air juste est
    /// plus dangereuse qu'une erreur franche, et ce projet l'a deja paye — un suivi rendait
    /// « huit mesures, cent pour cent du temps » sans avoir rien decide.
    /// </summary>
    public int Periode(int bande)
    {
        if ((uint)bande >= Bandes || _rang <= LagMax + 2) return 0;
        var (lag, _) = Sommet(bande);
        return lag;
    }

    /// <summary>
    /// A quel point cette bande designe une periode plutot qu'une autre, 0 a 1.
    ///
    /// C'est le RELIEF : de combien le sommet depasse les autres decalages, et non sa
    /// hauteur absolue. Une bande dont tous les decalages se valent n'a rien trouve, meme
    /// si tous se valent tres haut — c'est le piege qui avait fait passer le melange pour
    /// structure alors qu'on mesurait sa derive.
    /// </summary>
    public float Certitude(int bande)
    {
        if ((uint)bande >= Bandes || _rang <= LagMax + 2) return 0f;
        var (_, relief) = Sommet(bande);
        return relief;
    }

    /// <summary>Le vote diffuse d'une bande pour un decalage. Diagnostic.</summary>
    public float Vote(int bande, int lag)
    {
        var l = lag - LagMin;
        return (uint)bande < Bandes && (uint)l < Lags ? _diffus[bande, l] : 0f;
    }

    /// <summary>La bande qui porte le motif le plus net, ou -1.</summary>
    public int Meilleure()
    {
        var meilleur = -1;
        var haut = 0f;
        for (var b = 0; b < Bandes; b++)
        {
            var c = Certitude(b);
            if (c > haut) { haut = c; meilleur = b; }
        }
        return haut > 0f ? meilleur : -1;
    }

    /// <summary>
    /// Une fenetre d'analyse : les douze bandes, et ou l'on en est.
    /// </summary>
    public void Feed(long tMs, float? bpm, ReadOnlySpan<float> bandes)
    {
        if (bpm is { } b && b > 20f && b < 400f)
            _beatMs += (60_000f / b - _beatMs) * 0.05f;

        if (_lastMs < 0) { _lastMs = tMs; return; }
        var dt = tMs - _lastMs;
        _lastMs = tMs;
        if (dt <= 0 || dt > 1000) return;

        var avant = _phase;
        _phase += dt / (_beatMs * TempsParMesure);
        if (_phase >= 1.0)
        {
            _phase -= Math.Floor(_phase);
            Clore();
        }
        else if (_phase < avant)
        {
            _phase = avant;                  // le tempo a saute : on ne recule pas
        }

        // Le pas courant recoit ce que les bandes portent maintenant.
        var pas = (int)(_phase * Pas) % Pas;
        for (var i = 0; i < Bandes && i < bandes.Length; i++)
            _courante[pas * Bandes + i] += bandes[i];
        _compte[pas]++;
    }

    /// <summary>Une mesure vient de se fermer : on la range et l'on vote.</summary>
    private void Clore()
    {
        // Chaque pas porte la moyenne de ce qui y est tombe, et non sa somme : sans cela un
        // pas qui aurait recu deux fenetres au lieu d'une paraitrait deux fois plus fort.
        for (var k = 0; k < Pas; k++)
        {
            var n = MathF.Max(1, _compte[k]);
            for (var i = 0; i < Bandes; i++) _courante[k * Bandes + i] /= n;
            _compte[k] = 0;
        }

        var place = _rang % Histoire;
        Array.Copy(_courante, _passees[place], _courante.Length);
        Array.Clear(_courante);
        _rang++;

        if (_rang <= LagMax) return;

        // CHAQUE BANDE VOTE CHEZ ELLE.
        for (var b = 0; b < Bandes; b++)
        {
            for (var l = 0; l < Lags; l++)
            {
                var lag = LagMin + l;
                var autre = (_rang - 1 - lag) % Histoire;
                if (autre < 0) autre += Histoire;
                var r = Cosinus(_passees[place], _passees[autre], b);

                // UNE MOYENNE QUI OUBLIE, ET NON UNE SOMME QUI OUBLIE. Une somme grandit
                // avec le nombre de mesures ecoulees, et son ecart entre decalages avec
                // elle : le relief calcule dessus n'a plus rien a voir avec celui que la
                // mesure hors ligne a valide, qui portait sur des cosinus. La premiere
                // version accumulait une somme et se trompait dans les deux sens — elle
                // manquait un motif franc et en inventait un sur du bruit.
                _votes[b, l] = _votes[b, l] * Oubli + (1f - Oubli) * r;
            }
        }

        // PUIS ELLE PASSE SA VALEUR AUX VOISINES.
        //
        // La plaque de Laplace : chaque cellule garde son calcul et recoit une part de ceux
        // d'a cote. Une bande sure entraine ses voisines ; une bande qui ne sait rien ne
        // convainc personne, puisque ce qu'elle transmet est proportionnel a ce qu'elle a
        // trouve. C'est une diffusion, pas un vote majoritaire.
        for (var b = 0; b < Bandes; b++)
        {
            for (var l = 0; l < Lags; l++)
            {
                var somme = _votes[b, l];
                var poids = 1f;
                if (b > 0) { somme += Diffusion * _votes[b - 1, l]; poids += Diffusion; }
                if (b < Bandes - 1) { somme += Diffusion * _votes[b + 1, l]; poids += Diffusion; }
                _diffus[b, l] = somme / poids;
            }
        }
    }

    /// <summary>
    /// Le decalage retenu pour une bande, et la certitude qu'on y met.
    ///
    /// LE MAXIMUM ET SON RELIEF, PARCE QUE C'EST CE QUE LA MESURE HORS LIGNE A VALIDE.
    ///
    /// Une variante a ete essayee et retiree : prendre le PLUS PETIT decalage qui depasse un
    /// seuil absolu, pour eviter qu'un motif de quatre mesures soit annonce a huit — son
    /// harmonique. L'intention etait juste, le resultat non : mesure sur les dix morceaux du
    /// bac, elle annoncait <b>deux mesures partout, avec une certitude pleine</b>. Deux
    /// mesures consecutives se ressemblent par simple continuite de texture, et le plus
    /// petit decalage attrapait cette continuite au lieu d'une repetition.
    ///
    /// La regle validee hors ligne est le maximum sur les decalages, plus son relief sur les
    /// autres. Elle rend deux, quatre ou huit selon le morceau, et passe huit morceaux sur
    /// dix — et l'ecart au suivant y joue bien son role, contrairement a ce qu'un signal
    /// synthetique laisse croire : dans la vraie musique, une figure n'est jamais rejouee a
    /// l'identique, et son harmonique se correle donc moins bien qu'elle.
    /// </summary>
    private (int Lag, float Certitude) Sommet(int bande)
    {
        var haut = float.MinValue;
        var lag = 0;
        for (var l = 0; l < Lags; l++)
            if (_diffus[bande, l] > haut) { haut = _diffus[bande, l]; lag = LagMin + l; }

        // LES HARMONIQUES NE COMPTENT PAS PARMI « LES AUTRES », ET C'EST DECISIF.
        //
        // Une figure de quatre mesures se repete aussi a huit : c'est une consequence de sa
        // periode, pas une periode concurrente. La mettre parmi les rivales revient a punir
        // la bonne reponse d'etre correcte — et d'autant plus qu'elle l'est franchement.
        //
        // Mesure, sur une figure fabriquee a quatre mesures : les votes valaient 4:0,69 et
        // 8:0,68, tout le reste a moins un dixieme. En comptant huit parmi les rivales, le
        // relief tombait a 2,3 et le suivi ne designait rien. En l'ecartant, il vaut plus de
        // vingt.
        //
        // Ce projet connait ce piege — `SectionTracker` s'y est fait prendre entre huit et
        // seize mesures : « la bonne reponse et son double se tiennent, ce qui ecrase l'ecart
        // au suivant precisement quand tout va bien. » La conclusion y avait ete de renoncer
        // a l'ecart au suivant ; ici on le garde en excluant ce qui n'est pas un rival.
        float somme = 0, carres = 0;
        var n = 0;
        for (var l = 0; l < Lags; l++)
        {
            var autre = LagMin + l;
            if (autre == lag) continue;
            // Multiple ou diviseur du candidat : c'est la meme periode, vue autrement.
            if (autre % lag == 0 || lag % autre == 0) continue;
            somme += _diffus[bande, l];
            carres += _diffus[bande, l] * _diffus[bande, l];
            n++;
        }
        if (n < 2) return (0, 0f);

        var moyenne = somme / n;
        var ecart = MathF.Sqrt(MathF.Max(0f, carres / n - moyenne * moyenne)) + 1e-6f;
        var relief = (haut - moyenne) / ecart;

        return relief >= 3f && haut >= RessemblanceMin
            ? (lag, Math.Clamp((relief - 3f) / 6f, 0f, 1f))
            : (0, 0f);
    }

    /// <summary>Cosinus entre deux mesures, sur une bande et ses seize pas.</summary>
    private static float Cosinus(float[] a, float[] b, int bande)
    {
        float ma = 0, mb = 0;
        for (var k = 0; k < Pas; k++) { ma += a[k * Bandes + bande]; mb += b[k * Bandes + bande]; }
        ma /= Pas; mb /= Pas;

        float ps = 0, na = 0, nb = 0;
        for (var k = 0; k < Pas; k++)
        {
            var x = a[k * Bandes + bande] - ma;
            var y = b[k * Bandes + bande] - mb;
            ps += x * y; na += x * x; nb += y * y;
        }
        return na > 1e-12f && nb > 1e-12f ? ps / MathF.Sqrt(na * nb) : 0f;
    }

    /// <summary>Oublie tout : changement de disque.</summary>
    public void Reset()
    {
        Array.Clear(_courante);
        foreach (var p in _passees) Array.Clear(p);
        Array.Clear(_compte);
        Array.Clear(_votes);
        Array.Clear(_diffus);
        _rang = 0;
        _phase = 0;
        _lastMs = -1;
    }
}
