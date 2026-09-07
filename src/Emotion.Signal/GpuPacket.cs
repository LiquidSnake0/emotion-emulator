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
    /// <summary>Taille exacte du message, en octets. Le lecteur CUDA s'aligne dessus.</summary>
    public const int Size = 112;

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

        p.Centroid = (byte)Math.Clamp(f.Timbre.Centroid * 255f, 0f, 255f);
        p.Openness = (byte)Math.Clamp(f.Timbre.Openness * 255f, 0f, 255f);
        p.Density = (byte)Math.Clamp(f.Timbre.Density * 255f, 0f, 255f);

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
