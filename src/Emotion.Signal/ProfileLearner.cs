using System.Numerics;

namespace Emotion.Signal;

/// <summary>
/// Apprend les gabarits des sources, <b>hors du fil d'analyse</b>.
///
/// CE QU'EST UN GABARIT, ET POURQUOI CE N'EST PLUS UN PROFIL.
///
/// La factorisation apprenait des profils spectraux sur un axe lineaire en frequence. Mesure
/// sur Passepartout a douze composantes, neuf des douze profils etaient <b>les notes de la
/// ligne de basse</b>, une par case de 47 Hz : une basse qui joue un mi puis un la, c'etait
/// deux profils, et un piano qui change d'accord n'en avait jamais un. Le DJ entendait
/// « trois tranches de basse et tout le reste dans une seule source ».
///
/// Une sonorite, c'est une forme spectrale qui <b>glisse avec la hauteur en gardant sa
/// forme</b> : le meme piano au mi et au la. Sur un axe logarithmique en frequence, un
/// changement de note est une translation. Le modele est donc :
///
/// <code>
///   V(f, t) ≈ Σ_k Σ_p  w_k(f − p) · h_k(p, t)
/// </code>
///
/// un gabarit <c>w_k</c> par source, place a la position <c>p</c> (la note) avec le niveau
/// <c>h_k(p, t)</c>. Le glissement est borne a deux octaves : a quatre, mesure, le modele
/// trichait — il se servait du glissement pour fabriquer n'importe quoi.
///
/// LA DIVERGENCE EST CELLE DE KULLBACK-LEIBLER, pas l'ecart quadratique : sur un spectre, le
/// quadratique ne regarde que ce qui est fort, et 70 % de l'energie de ce repertoire vit
/// sous 150 Hz. Les regles de mise a jour restent multiplicatives — positives par
/// construction, comme avant.
///
/// MESURE HORS LIGNE SUR PASSEPARTOUT, contre les stems d'un juge exterieur : la basse sort a
/// 0,82 et le piano a 0,67, chacun dans son gabarit. La guitare et le kick, non — ils
/// restent colles a un voisin, et aucune forme (ni spectrale, ni temporelle) ne les a
/// decolles. C'est dit, et c'est la limite d'un separateur sans connaissance prealable.
///
/// LA CONCURRENCE, ET POURQUOI IL N'Y A AUCUN VERROU. Un seul fil ecrit, un seul fil lit,
/// et jamais la meme chose au meme moment : l'analyse depose une copie du spectrogramme puis
/// n'y touche plus ; l'apprentissage la lit, travaille sur ses propres tableaux, et publie
/// un resultat ; l'analyse le reprend a l'image suivante.
/// </summary>
public sealed class ProfileLearner
{
    private const float Eps = 1e-9f;

    /// <summary>Cases par octave de l'axe logarithmique. Un demi-ton fait deux cases.</summary>
    public const int ParOctave = 24;

    /// <summary>Cases de l'axe : huit octaves, de C1 (32,7 Hz) a C9 (8372 Hz).</summary>
    public const int NLog = 192;

    /// <summary>Premiere case, en hertz.</summary>
    public const float F0 = 32.703f;

    /// <summary>
    /// Positions ou un gabarit peut se placer : deux octaves de glissement.
    ///
    /// A une octave, la basse mangeait trois gabarits sur cinq parce que sa ligne ne tenait
    /// pas dedans ; a quatre, les gabarits « glissaient sur quatre octaves » et n'etaient
    /// plus des sonorites. Deux est la valeur mesuree entre les deux.
    /// </summary>
    public const int Positions = 49;

    /// <summary>Longueur d'un gabarit : ce qui reste de l'axe une fois le glissement retire.</summary>
    public const int Longueur = NLog - Positions + 1;

    private readonly int _sources;
    private readonly int _memory;
    private readonly int _iterations;

    /// <summary>
    /// Combien de sources le prochain apprentissage cherche, et sur combien de trames.
    ///
    /// LE NOMBRE DE SOURCES N'EST PAS UNE CONSTANTE, ET C'EST LA DEMANDE DU DJ : « des fois
    /// on en a 2, des fois 8, c'est justement ce que le programme est cense me dire ». La
    /// capacite (<c>_sources</c>) reste celle du paquet ; <c>_k</c> est ce qu'on cherche.
    /// </summary>
    private int _k;
    private int _trames;

    /// <summary>Ce que le balayage a mesure pour chaque nombre de sources essaye.</summary>
    /// <param name="Lien">Le pire lien entre deux sources : la correlation de leurs niveaux
    /// dans le temps. Deux gabarits qui montent et descendent ensemble sont un instrument
    /// coupe en deux, meme si leurs formes different.</param>
    public sealed record Bilan(int K, float Reste, float Doublon, float Lien = 0f);
    // PUBLIE D'UN BLOC, JAMAIS PENDANT. Le balayage tourne dans un fil de fond ; une liste
    // qu'on remplit au fur et a mesure se lit a moitie faite depuis /profils, et un bilan a
    // moitie fait ressemble a un choix. On construit a part et l'on echange la reference.
    private volatile IReadOnlyList<Bilan> _bilans = [];
    public IReadOnlyList<Bilan> Bilans => _bilans;

    /// <summary>Tous les bilans depuis le debut, dans l'ordre : pour lire les seuils apres coup.</summary>
    private readonly List<IReadOnlyList<Bilan>> _historique = [];
    public IReadOnlyList<IReadOnlyList<Bilan>> Historique => _historique;

    /// <summary>Le nombre de sources retenu par le dernier balayage, ou zero.</summary>
    public int Choix { get; private set; }

    /// <summary>Le K du dernier apprentissage publie, et s'il venait d'un balayage.</summary>
    public int DernierK { get; private set; }
    public bool DernierEtaitChoix { get; private set; }

    private int _kMin, _kMax, _iterationsBalayage;
    private float _seuilGain, _seuilDoublon, _plancher, _seuilLien;
    private bool _choisir;
    private bool _croitre;
    private readonly float[] _wGarde;     // les gabarits a K, si K+1 n'apporte rien

    // TRAME PAR TRAME EN MEMOIRE : chaque colonne du spectrogramme est contigue. Toutes les
    // boucles interieures parcourent un gabarit le long de l'axe des frequences, et c'est
    // cet axe-la qui doit etre contigu pour que les vecteurs SIMD y passent.
    private readonly float[] _v;          // trames x NLog : copie du spectrogramme, a nous seuls
    private readonly float[] _g;          // trames x NLog : la reconstruction, puis V / reconstruction
    private readonly float[] _w;          // sources x Longueur : les gabarits en cours
    private readonly float[] _h;          // (sources x Positions) x memoire : les niveaux
    private readonly float[] _accumule;   // Longueur : un gabarit en construction
    private readonly float[] _ready;      // sources x Longueur : gabarits publies
    private readonly float[] _positionMoyenne;       // sources : ou chaque gabarit a joue
    private readonly float[] _positionMoyenneReady;

    /// <summary>Un apprentissage tourne en ce moment.</summary>
    private volatile bool _running;

    /// <summary>Un resultat attend d'etre adopte par l'analyse.</summary>
    private volatile bool _published;

    public ProfileLearner(int sources, int memory, int iterations)
    {
        _sources = sources;
        _memory = memory;
        _iterations = iterations;
        _k = sources;
        _trames = memory;

        _v = new float[memory * NLog];
        _g = new float[memory * NLog];
        _w = new float[sources * Longueur];
        _h = new float[sources * Positions * memory];
        _accumule = new float[Longueur];
        _ready = new float[sources * Longueur];
        _positionMoyenne = new float[sources];
        _positionMoyenneReady = new float[sources];
        _wGarde = new float[sources * Longueur];
    }

    public bool Running => _running;

    /// <summary>
    /// Fait tourner l'apprentissage sur le fil appelant au lieu d'un fil de fond.
    ///
    /// Sert a la mesure et aux tests : c'est le seul moyen de comparer les deux regimes sur
    /// le meme morceau et la meme machine, et de tout faire dans le fil d'un test.
    /// </summary>
    public bool RunInline { get; set; }

    /// <summary>Cout moyen d'un apprentissage, en millisecondes. Diagnostic.</summary>
    public double AverageMs => _runs == 0 ? 0 : _totalMs / _runs;

    /// <summary>Pire apprentissage rencontre. C'est lui qui ferait le retard s'il etait en ligne.</summary>
    public double WorstMs { get; private set; }

    public int Runs => _runs;

    private double _totalMs;
    private int _runs;

    /// <summary>
    /// Lance un apprentissage, s'il n'y en a pas deja un. Rend faux si l'on est encore
    /// occupe : c'est un refus normal, pas une erreur. Mieux vaut sauter un apprentissage
    /// que d'en empiler deux — les gabarits du precedent restent valables.
    /// </summary>
    /// <param name="spectrogram">trames x NLog, autant de trames que la memoire.</param>
    /// <param name="seed">Les gabarits courants, dont on repart : ils restent a leur place.</param>
    public bool TryStart(ReadOnlySpan<float> spectrogram, ReadOnlySpan<float> seed,
                         int k = 0, int trames = 0)
    {
        if (_running || _published) return false;
        _k = k <= 0 ? _sources : Math.Min(k, _sources);
        _trames = trames <= 0 ? _memory : Math.Min(trames, _memory);
        _choisir = false;

        // On copie ce dont on a besoin pendant que l'analyse est arretee sur cette image :
        // apres le depart, plus rien de vivant n'est lu par le fil de fond.
        spectrogram.CopyTo(_v);
        seed.CopyTo(_w);
        GraineNiveaux();

        _running = true;
        if (RunInline) Run(); else Task.Run(Run);
        return true;
    }

    /// <summary>
    /// Cherche COMBIEN de sources le morceau contient, puis les apprend.
    ///
    /// ON DEMANDE AU MORCEAU, ON NE DECIDE PAS. On factorise avec kMin, kMin+1… sources et
    /// l'on regarde deux choses a chaque pas :
    ///
    ///   ce qui reste inexplique   une source de plus doit expliquer une part NEUVE du son ;
    ///                             quand le gain tombe sous le seuil, on en a assez
    ///   les doublons              deux gabarits qui se ressemblent — a une translation
    ///                             pres, puisqu'ils glissent — sont une source coupee en
    ///                             deux : le signe qu'on en a demande trop
    ///
    /// Le reste est la divergence de Kullback-Leibler rapportee a l'energie du spectre, et
    /// le gain se lit EN PROPORTION du reste precedent : sur un signal deja explique a 99 %,
    /// un dixieme de point n'est pas une source, c'est du bruit qu'on ajuste. Sous le
    /// plancher, on s'arrete quoi qu'il arrive.
    ///
    /// Mesure a quarante iterations. Passepartout : 4,2 · 2,3 · 2,1 · 2,1 % — le coude est a
    /// trois (gain de 45 % puis 9 %). Trois instruments fabriques : 10,7 · 7,0 · 4,7 %, le
    /// reste baisse encore, mais le doublon saute de 0,21 a 0,98 au quatrieme — c'est lui
    /// qui dit trois. Deux instruments : doublon 1,00 au troisieme. Une basse seule : 0,8 %
    /// de reste des deux sources — le plancher dit deux.
    ///
    /// ET LE LIEN. Un instrument coupe en deux gabarits de formes differentes passe le
    /// doublon, mais ses deux niveaux montent et descendent ensemble : mesure, 0,77 et 0,79
    /// sur les instruments fabriques, contre 0,55 pour la guitare qui entre vraiment a
    /// soixante secondes de Passepartout. Le seuil est a 0,70, entre les deux.
    ///
    /// LE DOUBLON EST A 0,60, ET IL ETAIT A 0,85. La croissance (voir
    /// <see cref="TryStartCroissance"/>) a montre des coupes en deux a 0,74 et 0,79 qui
    /// passaient, avec un lien de 0,59 a 0,69 qui passait aussi ; la vraie entree de la
    /// guitare, elle, est a 0,34. Entre 0,34 et 0,74, le seuil est a 0,60 — et sur
    /// Passepartout a quarante secondes, deux sources restent deux : la troisieme etait une
    /// seconde basse a 0,86, ce que l'oreille appelait deja « aussi une basse ».
    ///
    /// LE BALAYAGE PART DE LA MEME GRAINE POUR TOUS LES K. Sans cela le reste ne serait pas
    /// comparable d'un K au suivant — une initialisation heureuse a K=5 battrait une
    /// initialisation malheureuse a K=6 et l'on prendrait ce hasard pour un coude.
    /// </summary>
    public bool TryStartChoix(ReadOnlySpan<float> spectrogram, int kMin, int kMax, int trames,
                              int iterationsBalayage = 40, float seuilGain = 0.15f,
                              float seuilDoublon = 0.60f, float plancher = 0.01f,
                              float seuilLien = 0.70f)
    {
        _seuilLien = seuilLien;
        if (_running || _published) return false;
        spectrogram.CopyTo(_v);
        _kMin = Math.Max(1, kMin);
        _kMax = Math.Clamp(kMax, _kMin, _sources);
        _trames = trames <= 0 ? _memory : Math.Min(trames, _memory);
        _iterationsBalayage = iterationsBalayage;
        _seuilGain = seuilGain;
        _seuilDoublon = seuilDoublon;
        _plancher = plancher;
        _choisir = true;
        _running = true;
        if (RunInline) Run(); else Task.Run(Run);
        return true;
    }

    /// <summary>
    /// Reapprend au nombre courant, en repartant des gabarits donnes — et essaie UNE source
    /// de plus.
    ///
    /// LE MORCEAU NE DIT PAS TOUT EN QUARANTE SECONDES. Passepartout commence par piano et
    /// basse ; la guitare entre a trente secondes, la batterie a cinquante. Le choix fait a
    /// la memoire pleine rendait deux sources, et c'etait juste — a cet instant-la. Mais
    /// ensuite rien ne grandissait : ce qui entrait apres se faisait absorber par les deux.
    ///
    /// Ici, a chaque reapprentissage, on apprend a K depuis les gabarits courants (ils
    /// restent a leur place), puis a K+1 avec un gabarit neuf, et l'on garde K+1 seulement
    /// s'il passe les memes criteres que le balayage : du neuf explique, pas un doublon,
    /// et quelque chose qui restait a expliquer. On ne redescend jamais : une source qui
    /// se tait garde sa case, elle est juste muette.
    /// </summary>
    public bool TryStartCroissance(ReadOnlySpan<float> spectrogram, ReadOnlySpan<float> seed,
                                   int k, int trames, float seuilGain = 0.15f,
                                   float seuilDoublon = 0.60f, float plancher = 0.01f,
                                   float seuilLien = 0.70f)
    {
        _seuilLien = seuilLien;
        if (_running || _published) return false;
        spectrogram.CopyTo(_v);
        seed.CopyTo(_w);
        _k = Math.Clamp(k, 1, _sources);
        _trames = trames <= 0 ? _memory : Math.Min(trames, _memory);
        _seuilGain = seuilGain;
        _seuilDoublon = seuilDoublon;
        _plancher = plancher;
        _choisir = false;
        _croitre = _k < _sources;
        _running = true;
        if (RunInline) Run(); else Task.Run(Run);
        return true;
    }

    private void Croitre()
    {
        var k = _k;
        GraineNiveaux();
        for (var it = 0; it < _iterations; it++) Iterer();
        var resteK = Reste();
        Array.Copy(_w, _wGarde, _w.Length);

        // Un gabarit neuf en colonne k, les k premiers tels qu'ils viennent d'etre appris.
        var rng = new Random(1203 + k);
        var neuf = _w.AsSpan(k * Longueur, Longueur);
        for (var f = 0; f < Longueur; f++) neuf[f] = 0.1f + (float)rng.NextDouble();
        _k = k + 1;
        Normaliser();
        GraineNiveaux();
        for (var it = 0; it < _iterations; it++) Iterer();
        var resteK1 = Reste();
        var doublon = Doublon();
        var lien = Lien();
        _bilans = [new Bilan(k, resteK, 0f), new Bilan(k + 1, resteK1, doublon, lien)];
        lock (_historique) _historique.Add(_bilans);

        var garde = resteK >= _plancher
                 && (resteK - resteK1) / MathF.Max(Eps, resteK) >= _seuilGain
                 && doublon < _seuilDoublon
                 && lien < _seuilLien;
        if (!garde)
        {
            Array.Copy(_wGarde, _w, _w.Length);
            _k = k;
        }
        Choix = _k;
    }

    private void Graine()
    {
        // La meme graine a chaque appel : deux balayages du meme spectrogramme rendent le
        // meme choix, et deux K du meme balayage partagent leurs premiers gabarits.
        var rng = new Random(1203);
        for (var i = 0; i < _w.Length; i++) _w[i] = 0.1f + (float)rng.NextDouble();
        Normaliser();
        for (var i = 0; i < _h.Length; i++) _h[i] = 0.01f + (float)rng.NextDouble() * 0.1f;
    }

    private void GraineNiveaux()
    {
        var rng = new Random(1203);
        for (var i = 0; i < _h.Length; i++) _h[i] = 0.01f + (float)rng.NextDouble() * 0.1f;
    }

    /// <summary>Chaque gabarit somme a un : c'est le niveau qui porte l'echelle, pas la forme.</summary>
    private void Normaliser()
    {
        for (var s = 0; s < _sources; s++)
        {
            var w = _w.AsSpan(s * Longueur, Longueur);
            float somme = 0;
            foreach (var x in w) somme += x;
            if (somme <= Eps) continue;
            var inv = 1f / somme;
            for (var f = 0; f < Longueur; f++) w[f] *= inv;
        }
    }

    private void Balayer()
    {
        var bilans = new List<Bilan>();
        var choix = _kMin;
        float? restePrecedent = null;
        for (var k = _kMin; k <= _kMax; k++)
        {
            Graine();
            _k = k;
            for (var it = 0; it < _iterationsBalayage; it++) Iterer();
            var reste = Reste();
            var doublon = Doublon();
            var lien = Lien();
            bilans.Add(new Bilan(k, reste, doublon, lien));

            if (doublon >= _seuilDoublon) break;                                   // coupe en deux : trop
            if (lien >= _seuilLien) break;                                         // joue avec un autre : trop
            if (restePrecedent is { } rp && (rp - reste) / rp < _seuilGain) break; // plus rien de neuf
            choix = k;
            restePrecedent = reste;
            if (reste < _plancher) break;                                          // tout est explique
        }
        Choix = choix;
        _bilans = bilans;
        lock (_historique) _historique.Add(bilans);

        // Puis l'apprentissage complet, au nombre retenu, depuis la meme graine.
        Graine();
        _k = choix;
        for (var it = 0; it < _iterations; it++) Iterer();
    }

    /// <summary>Une passe : les niveaux, puis les gabarits, chacun sur une reconstruction fraiche.</summary>
    private void Iterer()
    {
        Reconstruire();
        UpdateH();
        Reconstruire();
        UpdateW();
    }

    /// <summary>
    /// La divergence de Kullback-Leibler entre le spectre et sa reconstruction, rapportee a
    /// l'energie du spectre, de 0 (tout explique) vers le haut.
    /// </summary>
    private float Reste()
    {
        Reconstruire(rapport: false);
        double d = 0, tot = 0;
        for (var t = 0; t < _trames; t++)
        {
            var v = _v.AsSpan(t * NLog, NLog);
            var vh = _g.AsSpan(t * NLog, NLog);
            for (var f = 0; f < NLog; f++)
            {
                var x = v[f] + Eps;
                var y = vh[f];
                d += x * Math.Log(x / y) - x + y;
                tot += x;
            }
        }
        return tot > 0 ? (float)(d / tot) : 0f;
    }

    /// <summary>
    /// Le pire lien entre deux sources : la correlation, dans le temps, de leurs niveaux
    /// (toutes positions confondues). Un instrument coupe en deux gabarits donne deux
    /// niveaux qui montent et descendent ensemble ; deux instruments, non.
    /// </summary>
    private float Lien()
    {
        var pire = 0f;
        for (var a = 0; a < _k; a++)
            for (var b = a + 1; b < _k; b++)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (var t = 0; t < _trames; t++)
                {
                    double na = 0, nb = 0;
                    for (var p = 0; p < Positions; p++)
                    {
                        na += _h[(a * Positions + p) * _memory + t];
                        nb += _h[(b * Positions + p) * _memory + t];
                    }
                    sa += na; sb += nb; saa += na * na; sbb += nb * nb; sab += na * nb;
                }
                var n = (double)_trames;
                var cov = sab / n - (sa / n) * (sb / n);
                var va = saa / n - (sa / n) * (sa / n);
                var vb = sbb / n - (sb / n) * (sb / n);
                var c = va > 1e-12 && vb > 1e-12 ? (float)(cov / Math.Sqrt(va * vb)) : 0f;
                if (c > pire) pire = c;
            }
        return pire;
    }

    /// <summary>
    /// Le pire cosinus entre deux gabarits, <b>a une translation pres</b> : deux gabarits
    /// identiques decales d'une quinte sont la meme sonorite, et le glissement les
    /// confondrait de toute facon.
    ///
    /// IL SE PREND SUR LE CARRE DES GABARITS. Deux gabarits du grave partagent toujours un
    /// large lobe sous 150 Hz, et sur les valeurs brutes deux sonorites distinctes montaient
    /// deja a 0,80 quand une copie montait a 0,85 : trop peu d'ecart pour un seuil. Le carre
    /// ne garde que les pics, la ou les timbres different vraiment.
    /// </summary>
    private float Doublon()
    {
        var pire = 0f;
        Span<float> ca = stackalloc float[Longueur];
        Span<float> cb = stackalloc float[Longueur];
        for (var a = 0; a < _k; a++)
        {
            var wa = _w.AsSpan(a * Longueur, Longueur);
            double na = 0;
            for (var f = 0; f < Longueur; f++) { ca[f] = wa[f] * wa[f]; na += (double)ca[f] * ca[f]; }
            for (var b = a + 1; b < _k; b++)
            {
                var wb = _w.AsSpan(b * Longueur, Longueur);
                double nb = 0;
                for (var f = 0; f < Longueur; f++) { cb[f] = wb[f] * wb[f]; nb += (double)cb[f] * cb[f]; }
                if (na <= Eps || nb <= Eps) continue;
                for (var d = -ParOctave; d <= ParOctave; d++)
                {
                    double ps = 0;
                    var debut = Math.Max(0, d);
                    var fin = Math.Min(Longueur, Longueur + d);
                    for (var f = debut; f < fin; f++) ps += ca[f] * cb[f - d];
                    var cos = (float)(ps / Math.Sqrt(na * nb));
                    if (cos > pire) pire = cos;
                }
            }
        }
        return pire;
    }

    private void Run()
    {
        var chrono = System.Diagnostics.Stopwatch.StartNew();

        if (_choisir)
        {
            Balayer();
        }
        else if (_croitre)
        {
            Croitre();
        }
        else
        {
            for (var it = 0; it < _iterations; it++) Iterer();
        }

        // Les gabarits au-dela de K ne portent rien : on les eteint plutot que de laisser
        // l'ancien y survivre et se faire lire comme une source.
        Array.Clear(_w, _k * Longueur, (_sources - _k) * Longueur);
        DernierK = _k;
        DernierEtaitChoix = _choisir;
        MesurerPositions();

        Array.Copy(_w, _ready, _w.Length);
        Array.Copy(_positionMoyenne, _positionMoyenneReady, _sources);

        var ms = chrono.Elapsed.TotalMilliseconds;
        _totalMs += ms;
        _runs++;
        if (ms > WorstMs) WorstMs = ms;

        // L'ordre compte : le resultat doit etre entierement ecrit avant que le drapeau
        // ne l'annonce, sans quoi l'analyse pourrait adopter des gabarits a moitie copies.
        _running = false;
        _published = true;
    }

    /// <summary>Ou chaque gabarit a joue en moyenne, en positions — sa hauteur habituelle.</summary>
    private void MesurerPositions()
    {
        for (var s = 0; s < _sources; s++)
        {
            double poids = 0, total = 0;
            if (s < _k)
                for (var p = 0; p < Positions; p++)
                {
                    var ligne = _h.AsSpan((s * Positions + p) * _memory, _trames);
                    double somme = 0;
                    foreach (var x in ligne) somme += x;
                    poids += somme * p;
                    total += somme;
                }
            _positionMoyenne[s] = total > Eps ? (float)(poids / total) : Positions / 2f;
        }
    }

    /// <summary>
    /// Si des gabarits sont prets, les recopie et rend vrai. Appele par le fil d'analyse,
    /// une fois par image : le cout est une copie de quelques kilo-octets.
    /// </summary>
    public bool TryAdopt(Span<float> destination, Span<float> positions)
    {
        if (!_published) return false;

        _ready.AsSpan().CopyTo(destination);
        _positionMoyenneReady.AsSpan().CopyTo(positions);
        _published = false;
        return true;
    }

    /// <summary>
    /// La reconstruction Σ w_k(f − p) h_k(p, t) de chaque trame ; puis, si demande, le
    /// rapport V / reconstruction, qui est ce dont les deux mises a jour ont besoin.
    /// </summary>
    private void Reconstruire(bool rapport = true)
    {
        for (var t = 0; t < _trames; t++)
        {
            var vh = _g.AsSpan(t * NLog, NLog);
            vh.Fill(Eps);
            for (var s = 0; s < _k; s++)
            {
                var w = _w.AsSpan(s * Longueur, Longueur);
                for (var p = 0; p < Positions; p++)
                {
                    var h = _h[(s * Positions + p) * _memory + t];
                    if (h <= Eps) continue;
                    Ajouter(vh.Slice(p, Longueur), w, h);
                }
            }
            if (!rapport) continue;
            var v = _v.AsSpan(t * NLog, NLog);
            for (var f = 0; f < NLog; f++) vh[f] = (v[f] + Eps) / vh[f];
        }
    }

    /// <summary>
    /// La regle multiplicative de Kullback-Leibler pour les niveaux : chaque h_k(p, t) est
    /// multiplie par la correlation du gabarit avec V / reconstruction a cette position,
    /// rapportee a la somme du gabarit — qui vaut un.
    /// </summary>
    private void UpdateH()
    {
        for (var t = 0; t < _trames; t++)
        {
            var g = _g.AsSpan(t * NLog, NLog);
            for (var s = 0; s < _k; s++)
            {
                var w = _w.AsSpan(s * Longueur, Longueur);
                for (var p = 0; p < Positions; p++)
                    _h[(s * Positions + p) * _memory + t] *= Produit(w, g.Slice(p, Longueur));
            }
        }
    }

    /// <summary>
    /// La meme regle pour les gabarits : w_k(f) est multiplie par la moyenne, ponderee par
    /// les niveaux, de V / reconstruction lue a toutes les positions ou le gabarit a joue.
    /// </summary>
    private void UpdateW()
    {
        for (var s = 0; s < _k; s++)
        {
            Array.Clear(_accumule);
            double den = 0;
            var acc = _accumule.AsSpan();
            for (var t = 0; t < _trames; t++)
            {
                var g = _g.AsSpan(t * NLog, NLog);
                for (var p = 0; p < Positions; p++)
                {
                    var h = _h[(s * Positions + p) * _memory + t];
                    if (h <= Eps) continue;
                    Ajouter(acc, g.Slice(p, Longueur), h);
                    den += h;
                }
            }
            var w = _w.AsSpan(s * Longueur, Longueur);
            var inv = (float)(1.0 / (den + Eps));
            for (var f = 0; f < Longueur; f++) w[f] *= acc[f] * inv;
        }
        Normaliser();
    }

    /// <summary>cible += source · gain, en vecteurs SIMD : c'est la boucle qui coute.</summary>
    internal static void Ajouter(Span<float> cible, ReadOnlySpan<float> source, float gain)
    {
        var n = cible.Length;
        var large = Vector<float>.Count;
        var i = 0;
        var vg = new Vector<float>(gain);
        for (; i + large <= n; i += large)
        {
            var c = new Vector<float>(cible.Slice(i, large));
            var s = new Vector<float>(source.Slice(i, large));
            (c + s * vg).CopyTo(cible.Slice(i, large));
        }
        for (; i < n; i++) cible[i] += source[i] * gain;
    }

    /// <summary>Le produit scalaire de deux tranches, en vecteurs SIMD.</summary>
    internal static float Produit(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length;
        var large = Vector<float>.Count;
        var i = 0;
        var acc = Vector<float>.Zero;
        for (; i + large <= n; i += large)
            acc += new Vector<float>(a.Slice(i, large)) * new Vector<float>(b.Slice(i, large));
        var somme = Vector.Dot(acc, Vector<float>.One);
        for (; i < n; i++) somme += a[i] * b[i];
        return somme;
    }
}
