using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Emotion.Signal;

/// <summary>
/// Le message envoye a l'unite de rendu, un par fenetre d'analyse.
///
/// <b>Disposition fixe, explicite, et sans reference.</b> Chaque choix ici sert la
/// latence, et aucun n'est gratuit :
///
/// <list type="bullet">
/// <item><see cref="StructLayout"/> explicite : l'ordre des champs en memoire est celui
/// ecrit ici, et non celui que le compilateur trouverait commode. Un lecteur ecrit en
/// CUDA peut donc mapper la meme structure sans negocier.</item>
/// <item>Champs de taille fixe, tableaux inclus : la structure entiere s'ecrit par un
/// seul <c>MemoryMarshal.Write</c>, sans parcourir quoi que ce soit et sans allouer.</item>
/// <item>Aucun type reference : elle peut vivre dans de la memoire partagee entre deux
/// processus, ce qu'un <c>float[]</c> interdirait — un tableau gere est une reference
/// dans le tas d'un processus, invisible depuis l'autre.</item>
/// <item>Petits entiers plutot que des booleens separes : les attaques tiennent dans un
/// seul octet de drapeaux.</item>
/// </list>
///
/// Le tout fait 96 octets. A 47 messages par seconde, cela represente 4,5 Ko/s : la
/// bande passante n'est pas le sujet, la <b>latence</b> l'est, et une structure plate se
/// lit d'un bloc de l'autre cote.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct GpuPacket
{
    /// <summary>
    /// Taille exacte du message, en octets. Le lecteur CUDA s'aligne dessus.
    ///
    /// <b>Quatre lignes de cache exactement.</b> Le paquet est passe de 128 a 256 octets
    /// pour que chaque source dispose de son propre mot (voir <see cref="SourceOffset"/>)
    /// au lieu de partager des octets avec ses voisines.
    /// </summary>
    public const int Size = 256;

    /// <summary>Nombre magique, pour qu'un lecteur detecte tout de suite un flux mal cadre.</summary>
    public const uint MagicValue = 0x454D5531;   // "EMU1"

    [FieldOffset(0)] public uint Magic;

    /// <summary>Numero de sequence. Un saut signale des messages perdus, ce qui est permis.</summary>
    [FieldOffset(4)] public uint Sequence;

    /// <summary>Millisecondes depuis le demarrage de la source.</summary>
    [FieldOffset(8)] public long TimeMs;

    /// <summary>Niveau global percu, 0 a 1.</summary>
    [FieldOffset(16)] public float Level;

    /// <summary>Tempo estime, ou zero tant qu'il n'est pas accroche.</summary>
    [FieldOffset(20)] public float Bpm;

    /// <summary>Position dans la mesure, 0 a 1, ou zero si le tempo n'est pas accroche.</summary>
    [FieldOffset(24)] public float Phase;

    /// <summary>Part du morceau prepare deja passee dans le master, 0 a 1.</summary>
    [FieldOffset(28)] public float Blend;

    /// <summary>Caractere tonal, 0 bruite, 1 franchement tonal.</summary>
    [FieldOffset(32)] public float Tonality;

    /// <summary>Changement d'accord, impulsion 0 a 1.</summary>
    [FieldOffset(36)] public float ChordChange;

    /// <summary>Bit 0 kick, bit 1 clap, bit 2 hat, <b>bit 3 nouveaute</b>.</summary>
    [FieldOffset(40)] public byte Hits;

    /// <summary>Classe de hauteur dominante, 0 a 11, ou 255 si aucune.</summary>
    [FieldOffset(41)] public byte Pitch;

    /// <summary>Phenomene courant, valeur de <see cref="Kind"/>.</summary>
    [FieldOffset(42)] public byte Scene;

    /// <summary>Intensite du phenomene, 0 a 255 pour 0 a 1.</summary>
    [FieldOffset(43)] public byte Intensity;

    /// <summary>Ecart a la texture des dernieres secondes, 0 a 255 pour 0 a 1.</summary>
    [FieldOffset(47)] public byte Novelty;

    /// <summary>Couleur de famille, un octet par canal.</summary>
    [FieldOffset(44)] public byte R;
    [FieldOffset(45)] public byte G;
    [FieldOffset(46)] public byte B;


    /// <summary>Les douze bandes, grave a aigu, chacune 0 a 1. 48 octets.</summary>
    [FieldOffset(48)] public Bands12 Bands;

    /// <summary>
    /// Niveaux tonals par registre : grave, medium, aigu. 0 a 255 pour 0 a 1.
    /// Ce sont les instruments qui ne frappent pas — piano, xylophone, voix.
    /// </summary>
    [FieldOffset(96)] public byte VoiceLow;
    [FieldOffset(97)] public byte VoiceMid;
    [FieldOffset(98)] public byte VoiceHigh;

    /// <summary>Bit 0 grave, bit 1 medium, bit 2 aigu : une note vient d'etre jouee.</summary>
    [FieldOffset(99)] public byte VoiceHits;

    /// <summary>Brillance percue, 0 sourd, 255 clair.</summary>
    [FieldOffset(100)] public byte Centroid;

    /// <summary>
    /// Ouverture du filtre, 0 ferme, 255 grand ouvert. C'est le geste du DJ le plus
    /// visible et le plus frequent.
    /// </summary>
    [FieldOffset(101)] public byte Openness;

    /// <summary>Densite d'evenements, 0 vide, 255 dense.</summary>
    [FieldOffset(102)] public byte Density;

    /// <summary>
    /// Rang du temps dans la mesure, 0 a 3, ou 255 tant que le temps fort est incertain.
    /// Zero est le temps fort. Le renderer doit traiter 255 comme « je ne sais pas » et
    /// non comme une valeur : caler une structure sur un temps invente se voit
    /// immediatement.
    /// </summary>
    [FieldOffset(103)] public byte Beat;

    /// <summary>Rang de la mesure dans la phrase, 0 a 7.</summary>
    [FieldOffset(104)] public byte Bar;

    /// <summary>
    /// Position continue dans la phrase. C'est la seule grandeur du paquet qui permette
    /// d'anticiper : a 240, la phrase se termine, quoi qu'il arrive dans le son.
    /// </summary>
    [FieldOffset(105)] public byte PhrasePos;

    /// <summary>Tension qui monte, 0 a 255.</summary>
    [FieldOffset(106)] public byte Buildup;

    /// <summary>
    /// Drapeaux de structure et de geste, en un octet.
    ///
    /// Structure : 1 rupture, 2 debut de mesure, 4 debut de phrase.
    /// Gestes : 8 filtre ferme, 16 basse coupee, 32 passage dense.
    ///
    /// Les gestes sont des etats a hysteresis, pas des seuils bruts : entre les deux
    /// bornes, rien ne bascule. Un bit qui changerait plusieurs fois par seconde ferait
    /// clignoter le motif qu'il commande.
    /// </summary>
    [FieldOffset(107)] public byte StructureBits;

    /// <summary>Longueur de phrase mesuree, en mesures.</summary>
    [FieldOffset(108)] public byte PhraseBars;

    /// <summary>
    /// Mesures restantes avant la prochaine frontiere de phrase. La seule grandeur du
    /// paquet qui regarde devant — mais elle ne vaut que si <see cref="SectionSure"/> est
    /// franc, sans quoi le renderer anticiperait une frontiere inventee.
    /// </summary>
    [FieldOffset(109)] public byte BarsToBoundary;

    /// <summary>Fiabilite de la structure longue, 0 a 255.</summary>
    [FieldOffset(110)] public byte SectionSure;

    /// <summary>
    /// A quel point le renderer peut se fier a la structure, 0 a 255. Un disque qui saute,
    /// un calage en cours, un blanc entre deux disques la font tomber.
    ///
    /// <b>Elle ne coupe rien.</b> L'analyse continue et le flux ne s'interrompt pas : le
    /// renderer decide seul de ce qu'il ignore. Lui transmettre un octet coute le prix
    /// d'un octet ; lui faire redecouvrir la meme chose couterait le retard de toute la
    /// chaine, ce qu'on passe le projet a supprimer.
    /// </summary>
    [FieldOffset(111)] public byte Trust;

    /// <summary>
    /// Cotes du polygone de la voix, tires du Camelot par la fiche. Le renderer les
    /// <b>lit</b> et ne les recalcule pas : la regle vivait en double et les deux
    /// implementations avaient deja diverge.
    /// </summary>
    [FieldOffset(112)] public byte Sides;

    /// <summary>Mode mineur, tire de la lettre Camelot. Non nul si mineur.</summary>
    [FieldOffset(113)] public byte Minor;

    /// <summary>
    /// Ou joue chaque registre a l'interieur du sien, 0 en bas, 255 en haut.
    ///
    /// C'est la seule grandeur du paquet qui decrive un <b>mouvement</b> plutot qu'un
    /// etat. Trois amplitudes ne disent pas qu'une melodie monte : elles disent que le
    /// medium baisse et que l'aigu monte, deux faits independants dont aucun ne porte le
    /// geste. Une position, elle, se deplace — et une forme peut la suivre.
    /// </summary>
    [FieldOffset(114)] public byte LowPitch;
    [FieldOffset(115)] public byte MidPitch;
    [FieldOffset(116)] public byte HighPitch;

    /// <summary>
    /// Ou commence la zone des sources, et combien d'octets chacune occupe.
    ///
    /// POURQUOI QUATRE OCTETS ALIGNES ET NON DES CHAMPS COTE A COTE.
    ///
    /// La disposition precedente donnait a chaque source un octet de niveau, puis rangeait
    /// les six attaques dans <i>un seul octet commun</i>. Tant qu'un seul fil ecrivait, cela
    /// tenait. Des que les six ecrivent en meme temps, cet octet commun devient le seul
    /// point de la structure ou deux sources se rencontrent — et poser un bit dans un octet
    /// se fait en le lisant, en le modifiant, en le reecrivant : deux sources qui le font
    /// ensemble s'effacent l'une l'autre, et une attaque disparait sans que rien ne le
    /// signale.
    ///
    /// Chaque source a donc maintenant <b>son mot de quatre octets, aligne</b> : son niveau,
    /// son contour, ses drapeaux, une reserve. Aucune ne partage un octet avec une autre, ce
    /// qui rend l'ecriture concurrente sure <i>par la forme des donnees</i> et non par un
    /// verrou. C'est la meme idee qu'ailleurs dans le projet : separer plutot qu'arbitrer.
    ///
    /// Huit emplacements sont reserves pour six sources. Le format n'aura pas a bouger le
    /// jour ou la separation en distinguera davantage.
    /// </summary>
    public const int SourceOffset = 128;

    /// <summary>
    /// Huit octets par source. Ce que la source sait d'elle-meme voyage avec ce qu'elle
    /// fait a l'instant : niveau, contour, drapeaux, puis empreinte, confiance et
    /// etiquette.
    ///
    /// LES QUATRE DERNIERS OCTETS N'ARRIVENT PAS EN MEME TEMPS QUE LES TROIS PREMIERS, ET
    /// C'EST VOULU.
    ///
    /// Le niveau est juste des la premiere image. L'empreinte, elle, se forme sur quelques
    /// secondes de jeu effectif — et une source qui n'entre qu'au refrain mettra une
    /// minute. Faire attendre le paquet que la plus lente soit prete gelerait tout
    /// l'affichage pour une seule forme.
    ///
    /// Le paquet part donc a cadence fixe, et chaque source y ecrit <b>ce qu'elle est en
    /// mesure d'affirmer a cet instant</b>. La confiance dit au renderer combien de credit
    /// accorder au reste : a zero, c'est une forme anonyme qui bouge ; a plein, c'est une
    /// source reconnue, et l'etiquette peut porter un nom venu de la fiche.
    /// </summary>
    public const int SourceStride = 8;
    public const int SourceSlots = 8;

    /// <summary>Rangs des octets a l'interieur du mot d'une source.</summary>
    public const int SourceLevel = 0;
    public const int SourcePitch = 1;
    public const int SourceFlags = 2;
    public const int SourceLabel = 3;
    public const int SourceConfidence = 4;
    public const int SourceBrightness = 5;
    public const int SourceTexture = 6;

    /// <summary>Combien de sources la separation publie aujourd'hui.</summary>
    public const int SourceCount = 6;

    /// <summary>Drapeau d'attaque, dans l'octet de drapeaux d'une source.</summary>
    public const byte SourceHitBit = 1;

    /// <summary>La zone des sources, vue comme des octets bruts.</summary>
    [FieldOffset(SourceOffset)] public SourceBlock Sources;

    /// <summary>
    /// Le tempo apporte par la fiche du cue, ou zero si le disque n'a pas ete prepare.
    ///
    /// Il n'est pas la pour remplacer <see cref="Bpm"/> mais pour lui etre confronte : le
    /// renderer voit d'un coup ce qu'on croyait savoir et ce que le disque fait vraiment.
    /// </summary>
    [FieldOffset(192)] public float BpmExpected;

    /// <summary>
    /// Decalage accumule entre la grille attendue et la grille reelle, en fractions de
    /// temps. Signe : negatif quand le disque traine sur la fiche.
    ///
    /// C'est la grandeur qui rend une derive perceptible. Un ecart de 1 BPM sur 87 parait
    /// negligeable ; le meme ecart deplace la grille d'un cinquieme de temps en seize
    /// temps, et d'un temps entier en une minute.
    /// </summary>
    [FieldOffset(196)] public float TempoDrift;

    /// <summary>A quel point la derive se voit, 0 a 255. Le renderer lit ceci, pas le calcul.</summary>
    [FieldOffset(200)] public byte DriftVisible;

    /// <summary>
    /// Le tempo tel qu'il vient d'etre annonce — « on est a 87,9 », puis « 88,1 ».
    ///
    /// C'est une valeur qui tient entre deux annonces, la ou <see cref="Bpm"/> tremble
    /// d'une fenetre a l'autre. Un renderer qui affiche un chiffre lit celle-ci.
    /// </summary>
    [FieldOffset(204)] public float BpmAnnounced;

    /// <summary>
    /// Une annonce vient d'etre faite sur cette image. Ne dure qu'une image : c'est un
    /// instant, pas un etat.
    /// </summary>
    [FieldOffset(208)] public byte TempoAnnounce;

    public const byte KickBit = 1;
    public const byte ClapBit = 2;
    public const byte HatBit = 4;

    /// <summary>Un element est entre : voix, sample, nappe. Aucun des trois autres ne le voit.</summary>
    public const byte NoveltyBit = 8;

    /// <summary>Aucune hauteur dominante.</summary>
    public const byte NoPitch = 255;

    /// <summary>
    /// Douze flottants places en ligne dans la structure, sans indirection. Un
    /// <c>float[]</c> aurait ete une reference : invisible depuis un autre processus et
    /// alloue a chaque message.
    /// </summary>
    [InlineArray(12)]
    public struct Bands12
    {
        private float _first;
    }

    /// <summary>
    /// Compose un message a partir d'une image du signal et du morceau projete.
    /// Aucune allocation : tout est recopie dans la structure.
    /// </summary>
    /// <summary>Valeur de <see cref="Beat"/> quand le temps fort n'est pas etabli.</summary>
    public const byte NoBeat = 255;

    public const byte DropBit = 1;
    public const byte BarBit = 2;
    public const byte PhraseBit = 4;

    public const byte FilterClosedBit = 8;
    public const byte BassCutBit = 16;
    public const byte DenseBit = 32;

    private static byte Byte255(float v) => (byte)Math.Clamp(v * 255f, 0f, 255f);

    /// <summary>
    /// Ce qu'une source dit d'elle-meme, dans son mot a elle.
    /// </summary>
    /// <param name="Level">activation, 0 a 255.</param>
    /// <param name="Pitch">ou elle joue dans son registre, 0 en bas, 255 en haut.</param>
    /// <param name="Hit">une attaque vient d'etre constatee.</param>
    /// <param name="Label">nom attribue de l'exterieur, 0 tant que la source est anonyme.</param>
    /// <param name="Confidence">a quel point l'empreinte est fiable.</param>
    /// <param name="Brightness">brillance moyenne de la source.</param>
    /// <param name="Texture">raie franche a souffle.</param>
    public readonly record struct SourceState(
        byte Level, byte Pitch, bool Hit,
        byte Label = 0, byte Confidence = 0, byte Brightness = 128, byte Texture = 128);

    /// <summary>
    /// Ecrit ce qu'une source a a dire, dans les quatre octets qui n'appartiennent qu'a
    /// elle. Deux sources differentes peuvent appeler cette methode en meme temps sans
    /// precaution : leurs mots ne se touchent pas.
    /// </summary>
    public void WriteSource(int rank, in LaneState state, byte label = 0)
    {
        if ((uint)rank >= SourceSlots) return;

        var slot = SourceByte(rank);
        slot[SourceLevel] = Byte255(state.Level);
        slot[SourcePitch] = Byte255(state.Position);
        slot[SourceFlags] = state.Hit ? SourceHitBit : (byte)0;
        slot[SourceLabel] = label;
        slot[SourceConfidence] = Byte255(state.Confidence);
        slot[SourceBrightness] = Byte255(state.Brightness);
        slot[SourceTexture] = Byte255(state.Texture);
        slot[7] = 0;
    }

    /// <summary>Relit ce qu'une source a ecrit.</summary>
    public SourceState ReadSource(int rank)
    {
        if ((uint)rank >= SourceSlots) return default;

        var slot = SourceByte(rank);
        return new SourceState(
            slot[SourceLevel], slot[SourcePitch], (slot[SourceFlags] & SourceHitBit) != 0,
            slot[SourceLabel], slot[SourceConfidence],
            slot[SourceBrightness], slot[SourceTexture]);
    }

    private Span<byte> SourceByte(int rank)
    {
        var all = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.CompilerServices.Unsafe.As<SourceBlock, byte>(ref Sources),
            SourceSlots * SourceStride);
        return all.Slice(rank * SourceStride, SourceStride);
    }

    public static GpuPacket From(in VisualFrame f, TrackContext track, uint sequence)
    {
        var p = new GpuPacket
        {
            Magic = MagicValue,
            Sequence = sequence,
            TimeMs = f.T,
            Level = f.Rms,
            Bpm = f.Bpm ?? 0f,
            Phase = f.Phase ?? 0f,
            Blend = f.Blend,
            Tonality = f.Harmony.Tonality,
            ChordChange = f.Harmony.Change,
            Pitch = (byte)(f.Harmony.Pitch ?? NoPitch),
            Scene = (byte)track.Scene.Kind,
            Intensity = (byte)Math.Clamp(track.Scene.Intensity * 255f, 0f, 255f),
        };

        if (f.Hits.Kick) p.Hits |= KickBit;
        if (f.Hits.Clap) p.Hits |= ClapBit;
        if (f.Hits.Hat) p.Hits |= HatBit;
        if (f.NoveltyOnset) p.Hits |= NoveltyBit;
        p.Novelty = (byte)Math.Clamp(f.Novelty * 255f, 0f, 255f);

        p.VoiceLow = (byte)Math.Clamp(f.Voices.Low * 255f, 0f, 255f);
        p.VoiceMid = (byte)Math.Clamp(f.Voices.Mid * 255f, 0f, 255f);
        p.VoiceHigh = (byte)Math.Clamp(f.Voices.High * 255f, 0f, 255f);
        if (f.Voices.LowHit) p.VoiceHits |= 1;
        if (f.Voices.MidHit) p.VoiceHits |= 2;
        if (f.Voices.HighHit) p.VoiceHits |= 4;

        p.BpmExpected = f.ExpectedBpm ?? 0f;
        p.TempoDrift = f.TempoDrift;
        p.DriftVisible = (byte)Math.Clamp(f.DriftVisible * 255f, 0f, 255f);
        p.BpmAnnounced = f.AnnouncedBpm;
        p.TempoAnnounce = f.TempoAnnounce ? (byte)1 : (byte)0;

        p.Centroid = (byte)Math.Clamp(f.Timbre.Centroid * 255f, 0f, 255f);
        p.Openness = (byte)Math.Clamp(f.Timbre.Openness * 255f, 0f, 255f);
        p.Density = (byte)Math.Clamp(f.Timbre.Density * 255f, 0f, 255f);

        // 255 signifie « temps fort inconnu », et non un rang. Le renderer doit s'abstenir
        // plutot que caler sa structure sur un temps invente.
        p.Beat = f.Structure.Beat < 0 ? NoBeat : (byte)f.Structure.Beat;
        p.Bar = (byte)f.Structure.Bar;
        p.PhrasePos = (byte)Math.Clamp(f.Structure.PhrasePos * 255f, 0f, 255f);
        p.Buildup = (byte)Math.Clamp(f.Structure.Buildup * 255f, 0f, 255f);
        if (f.Structure.Drop) p.StructureBits |= DropBit;
        if (f.Structure.BarStart) p.StructureBits |= BarBit;
        if (f.Structure.PhraseStart) p.StructureBits |= PhraseBit;
        if (f.Gestures.FilterClosed) p.StructureBits |= FilterClosedBit;
        if (f.Gestures.BassCut) p.StructureBits |= BassCutBit;
        if (f.Gestures.Dense) p.StructureBits |= DenseBit;
        p.PhraseBars = (byte)f.Structure.PhraseBars;
        p.BarsToBoundary = (byte)Math.Clamp(f.Structure.BarsToBoundary, 0, 255);
        p.SectionSure = (byte)Math.Clamp(f.Structure.SectionConfidence * 255f, 0f, 255f);
        p.Trust = (byte)Math.Clamp(f.Structure.Trust * 255f, 0f, 255f);
        p.Sides = (byte)track.Sides;
        p.Minor = track.Minor ? (byte)1 : (byte)0;
        p.LowPitch = (byte)Math.Clamp(f.Voices.LowPitch * 255f, 0f, 255f);
        p.MidPitch = (byte)Math.Clamp(f.Voices.MidPitch * 255f, 0f, 255f);
        p.HighPitch = (byte)Math.Clamp(f.Voices.HighPitch * 255f, 0f, 255f);

        // Chaque source ecrit sa propre zone. Le contour part maintenant pour les six et
        // non plus pour les quatre premieres : les deux dernieres avaient un niveau qui
        // bougeait et un contour toujours nul, donc des formes qui pulsaient sur place.
        for (var i = 0; i < SourceCount; i++)
            p.WriteSource(i, f.Voices.LaneAt(i), f.Voices.LabelAt(i));

        // Couleur deja decomposee par TrackContext : rien a analyser ici.
        var (r, g, b) = track.Rgb;
        p.R = r; p.G = g; p.B = b;

        var bands = f.Bands;
        if (bands is not null)
            for (var i = 0; i < 12 && i < bands.Length; i++)
                p.Bands[i] = bands[i];

        return p;
    }
}

/// <summary>
/// Les huit mots de source, cote a cote. Un bloc plat pour que le lecteur CUDA le lise
/// d'un seul acces au lieu de suivre huit champs.
/// </summary>
[System.Runtime.CompilerServices.InlineArray(GpuPacket.SourceSlots * GpuPacket.SourceStride)]
public struct SourceBlock
{
    private byte _first;
}
