using Emotion.Signal;

// Sonde hors ligne : passe un enregistrement dans la chaine d'analyse et rend des
// chiffres.
//
// POURQUOI ELLE EXISTE. Regarder l'ecran renseigne sur ce qu'on voit, jamais sur ce qui
// decide. Chaque correction de ce projet vient d'une mesure — quatre attaques par temps,
// ecart median egal a l'ecart minimal, 22 µs par message — et aucune n'aurait ete trouvee
// a l'oeil. Un descripteur qui n'a jamais rencontre de vrai signal n'est pas un
// descripteur, c'est une intention.
//
//   dotnet run --project tools/Emotion.Probe -- <fichier.wav> [debut_s] [duree_s]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: probe <fichier.wav> [debut_s] [duree_s]");
    return 1;
}

var path = args[0];
var startS = args.Length > 1 ? double.Parse(args[1]) : 0;
var lengthS = args.Length > 2 ? double.Parse(args[2]) : 90;

// Export des images analysees, pour rejouer l'analyse sans materiel ni serveur.
// Quatrieme argument : le fichier de sortie.
var exportTo = args.Length > 3 ? args[3] : null;
var exported = new List<string>();

var (mono, rate) = Wav.ReadMono(path, startS, lengthS);
Console.WriteLine($"{Path.GetFileName(path)} — {mono.Length / (float)rate:F1} s a {rate} Hz");

// Cinquieme argument : « sep » force la separation harmonique/percussive, coupee par
// defaut depuis qu'on l'a mesuree. Sert a comparer les deux sur la meme matiere.
var separate = args.Length > 4 && args[4] == "sep";
var analyzer = new SpectrumAnalyzer(rate, separate);

// « inline » en argument : fait tourner l'apprentissage dans le fil d'analyse, comme
// avant. Sert a comparer les deux regimes sur le meme morceau.
if (args.Contains("inline")) analyzer.Separation.ApprentissageEnLigne = true;

// « memoire » en argument : joue l'extrait DEUX FOIS de suite dans le meme processus, la
// seconde reprenant ce que la premiere a etabli. C'est ainsi que la reprise se produit
// vraiment — en memoire vive, sans jamais toucher au disque dur — et cela reproduit ce qui
// arrive quand on repose l'aiguille au debut pendant un calage.
var deuxPassages = args.Contains("memoire");
Console.WriteLine(separate ? "separation active" : "separation COUPEE");
const int hop = SpectrumAnalyzer.Window;

var barStarts = new List<long>();
var phraseStarts = new List<long>();
var drops = new List<long>();
var confidences = new List<float>();
var buildups = new List<float>();
int kicks = 0, claps = 0, hats = 0, novelties = 0, chordChanges = 0;
int vLow = 0, vMid = 0, vHigh = 0;
var beatHisto = new int[5];   // -1 puis 0..3
var changes = new List<float>();
var slopes = new List<float>();
var tempos = new List<float>();

// Ou tombent les ruptures dans la phrase. Si la musique se construit vraiment en 4, 8,
// 16, elles doivent se concentrer sur quelques mesures et non se repartir au hasard —
// et c'est ce qui rendrait leur prochaine occurrence previsible.
var breakBar = new int[Structure.DefaultPhraseBars];
var breakBeat = new int[4];
var lastBreakBar = -1;
var breakGaps = new List<int>();
var barCounter = 0;
var freeBreaks = new List<int>();
var phraseLen = new Dictionary<int, int>();
var sectionConf = new List<float>();
var profileConf = new List<float>();
var agree = 0;
var compared = 0;
var ruptures = 0;
var kickAt = new List<long>();
var syncErr = new List<float>();
var offsets = new List<float>();
var gridMs = new List<float>();
var tempoMs = new List<float>();
var lastReason = "";

// Combien de temps mur coute une image d'analyse. C'est la seule mesure qui dise si la
// chaine tient le direct : une image qui depasse le pas de 21 ms fait prendre du retard,
// et ce retard s'accumule.
var coutImage = new List<double>();
var annonces = new List<(long T, float Bpm)>();
var murAt = new long[Voices.Registers];
Array.Fill(murAt, -1L);
var chronoImage = new System.Diagnostics.Stopwatch();

// Premier passage, quand on demande la reprise : on ecoute une fois, on garde ce qu'on a
// appris, et la boucle qui suit repart dessus.
if (deuxPassages)
{
    for (var i = 0; i + hop <= mono.Length; i += hop)
        analyzer.Analyze(mono.AsSpan(i, hop), (long)(i * 1000L / rate));

    var appris = analyzer.Connaissance(Path.GetFileNameWithoutExtension(path), default);
    Console.WriteLine($"premier passage     {appris.SecondsHeard:F0} s · " +
                      $"{appris.Sources.Sum(x => x.Observations)} observations · " +
                      $"tempo {appris.Bpm:F1}");

    analyzer.NewTrack();
    analyzer.Reprendre(appris);
}

for (var i = 0; i + hop <= mono.Length; i += hop)
{
    var tMs = (long)(i * 1000L / rate);
    chronoImage.Restart();
    var f = analyzer.Analyze(mono.AsSpan(i, hop), tMs);
    coutImage.Add(chronoImage.Elapsed.TotalMilliseconds);

    // A quel instant chaque source devient assez sure d'elle pour qu'un nom tienne. Les
    // six ne s'attendent pas : c'est tout l'interet de les faire murir separement.
    for (var r = 0; r < Voices.Registers; r++)
        if (murAt[r] < 0 && analyzer.Voix.EtatDe(r).Confidence >= 0.6f) murAt[r] = tMs;

    if (f.TempoAnnounce) annonces.Add((tMs, f.AnnouncedBpm));

    if (f.Hits.Kick)
    {
        kicks++; kickAt.Add(tMs);
        syncErr.Add(MathF.Abs(analyzer.SyncError));
        offsets.Add(analyzer.TransientOffsetMs);
    }
    if (f.Hits.Clap) claps++;
    if (f.Hits.Hat) hats++;
    if (f.Voices.LowHit) vLow++;
    if (f.Voices.MidHit) vMid++;
    if (f.Voices.HighHit) vHigh++;
    if (f.NoveltyOnset) novelties++;
    if (f.Harmony.Change > 0.45f) chordChanges++;
    changes.Add(f.Harmony.Change);
    gridMs.Add(analyzer.GridBeatMs);
    if (f.Bpm is { } bq) tempoMs.Add(60_000f / bq);
    if (f.Bpm is { } bp) tempos.Add(bp);

    if (exportTo is not null)
    {
        // Format compact : des nombres, pas des objets. Mille images nommees pesent dix
        // fois ce que pesent mille lignes de valeurs, et c'est une page web qui les lit.
        var b = string.Join(",", f.Bands.Select(v => v.ToString("F3",
            System.Globalization.CultureInfo.InvariantCulture)));
        var flags = (f.Hits.Kick ? 1 : 0) | (f.Hits.Clap ? 2 : 0) | (f.Hits.Hat ? 4 : 0)
                  | (f.Voices.LowHit ? 8 : 0) | (f.Voices.MidHit ? 16 : 0)
                  | (f.Voices.HighHit ? 32 : 0) | (f.NoveltyOnset ? 64 : 0)
                  | (f.Structure.Drop ? 128 : 0) | (f.Gestures.FilterClosed ? 256 : 0)
                  | (f.Gestures.BassCut ? 512 : 0) | (f.Gestures.Dense ? 1024 : 0);
        string N(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        exported.Add($"[{tMs},{N(f.Rms)},[{b}],{flags},{N(f.Voices.Low)},{N(f.Voices.Mid)}," +
                     $"{N(f.Voices.High)},{N(f.Timbre.Openness)},{N(f.Timbre.Centroid)}," +
                     $"{N(f.Timbre.Density)},{(f.Bpm is { } bb ? N(bb) : "0")}," +
                     $"{f.Structure.Beat},{N(f.Structure.Buildup)},{N(f.Novelty)}," +
                     $"{N(f.Structure.Confidence)},{N(f.Harmony.Tonality)}," +
                     $"{N(f.Voices.LowPitch)},{N(f.Voices.MidPitch)},{N(f.Voices.HighPitch)}," +
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => N(f.Voices.LevelAt(i)))) + "]," +
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => N(f.Voices.PitchAt(i)))) + "]," +
                     f.Voices.Hits + "," +
                     // Ce que chaque source sait d'elle-meme : la maturite monte a son
                     // rythme, sans jamais retenir le reste de l'image.
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => N(f.Voices.LaneAt(i).Confidence))) + "]," +
                     "[" + string.Join(",", Enumerable.Range(0, Voices.Registers)
                            .Select(i => (int)f.Voices.LabelAt(i))) + "]," +
                     $"{N(f.AnnouncedBpm)},{(f.TempoAnnounce ? 1 : 0)},{N(f.DriftVisible)}]");
    }

    var s = f.Structure;
    if (s.BarStart) { barStarts.Add(tMs); barCounter++; }
    if (f.NoveltyOnset && s.Confidence > 0.35f)
    {
        breakBar[Math.Clamp(s.Bar, 0, Structure.DefaultPhraseBars - 1)]++;
        if (s.Beat >= 0) breakBeat[s.Beat]++;
        freeBreaks.Add(barCounter);
        if (lastBreakBar >= 0) breakGaps.Add(barCounter - lastBreakBar);
        lastBreakBar = barCounter;
    }
    if (s.PhraseStart) phraseStarts.Add(tMs);
    if (s.Drop) drops.Add(tMs);
    confidences.Add(s.Confidence);
    phraseLen[s.PhraseBars] = phraseLen.GetValueOrDefault(s.PhraseBars) + 1;
    sectionConf.Add(s.SectionConfidence);
    if (analyzer.ContinuityBroken) { ruptures++; lastReason = analyzer.LastBreak; }
    var (pOff, pConf, _, gBeat) = analyzer.Downbeat;
    profileConf.Add(pConf);
    // Le profil designe une position de grille ; la grille, elle, publie le rang du temps.
    // Ils sont d'accord quand le temps que le profil designe est bien le temps fort.
    if (gBeat >= 0 && pConf > 0.2f)
    {
        compared++;
        if (((int)(s.Beat) == 0) == (pOff == (int)(pOff))) { }
    }
    buildups.Add(s.Buildup);
    var (sb, sa, su) = analyzer.Slopes;
    slopes.Add(MathF.Abs(sb) + MathF.Abs(sa) + MathF.Abs(su));
    beatHisto[s.Beat + 1]++;
}

Console.WriteLine($"\nlatence d'analyse   {analyzer.LatencyMs:F0} ms");
Console.WriteLine($"tempo detecte sur   {tempos.Count * 100 / Math.Max(1, changes.Count)} % des fenetres" +
                  (tempos.Count > 0 ? $" · median {Median(tempos):F1} BPM" : ""));
Console.WriteLine($"changement d'accord p50 {Pct(changes, 50):F2} · p90 {Pct(changes, 90):F2} · p99 {Pct(changes, 99):F2}");
Console.WriteLine($"pentes cumulees     p50 {Pct(slopes, 50):F3} · p95 {Pct(slopes, 95):F3} · max {slopes.Max():F3}");
Console.WriteLine($"tempo               {analyzer.Bpm?.ToString("F1") ?? "—"} BPM");
Console.WriteLine($"frappes             {kicks} kicks · {claps} claps · {hats} charleys");
if (kickAt.Count > 4)
{
    var kg = kickAt.Zip(kickAt.Skip(1), (a, b) => (float)(b - a)).ToList();
    var kmed = Median(kg);
    // LA METRIQUE QUI JUGE LA DETECTION. L'ecart median entre kicks doit tomber sur un
    // multiple simple du temps. S'il colle au minimum autorise, le detecteur sature et
    // compte du bruit ; s'il vaut la moitie d'un temps, il entend les croches.
    var beat = analyzer.Bpm is { } b2 ? 60_000f / b2 : float.NaN;
    Console.WriteLine($"ecart median entre kicks {kmed:F0} ms" +
        (float.IsNaN(beat) ? "" : $"  =  {kmed / beat:F2} temps") +
        $"  ·  minimum autorise {OnsetDetector.MinGapMs:F0} ms");
}
if (kickAt.Count > 8 && gridMs.Count > 0)
{
    // La mediane ne dit rien de la dispersion. Ce qui compte est la part des intervalles
    // qui tombent vraiment sur un multiple du temps : c'est elle qui decide si la grille
    // peut se caler sur ces frappes ou si elle poursuit du bruit.
    var beat = Median(gridMs);
    var kg = kickAt.Zip(kickAt.Skip(1), (a, b) => (float)(b - a)).ToList();
    // ATTENTION AU CRITERE. Exiger un multiple entier du temps rejette les croches, et
    // un kick sur un contretemps est parfaitement musical. On compte donc separement les
    // deux grilles, et la seconde est la seule qui ait un sens musical.
    // Tolerance absolue, en fraction de temps, et non relative a l'unite testee : sans
    // quoi la grille la plus fine est jugee le plus severement, ce qui donne des croches
    // moins souvent justes que des temps — un resultat impossible, puisque tout multiple
    // du temps est aussi un multiple de la croche.
    int Near(float r, float unit) => MathF.Abs(r - unit * MathF.Round(r / unit)) < 0.12f ? 1 : 0;
    // La tolerance doit se resserrer avec la grille, sinon la plus fine est
    // trivialement satisfaite : a plus ou moins 0,12 temps sur un pas de 0,25, on couvre
    // 96 % de l'espace et le chiffre ne prouve plus rien.
    int NearT(float r, float unit, float tol) =>
        MathF.Abs(r - unit * MathF.Round(r / unit)) < tol ? 1 : 0;
    var onBeat = kg.Sum(g => NearT(g / beat, 1f, 0.12f));
    var onEighth = kg.Sum(g => NearT(g / beat, 0.5f, 0.06f));
    var onSixteenth = kg.Sum(g => NearT(g / beat, 0.25f, 0.03f));
    Console.WriteLine($"intervalles sur la grille  temps {onBeat * 100 / kg.Count} %" +
                      $"  ·  croches {onEighth * 100 / kg.Count} %" +
                      $"  ·  doubles {onSixteenth * 100 / kg.Count} %  (sur {kg.Count})");

    var close = syncErr.Count(e => e < 0.1f);
    Console.WriteLine($"frappes bien calees        {close * 100 / syncErr.Count} %" +
                      $"  (ecart de phase sous 0,1 temps)");
}

if (syncErr.Count > 4)
{
    var m = syncErr.Average();
    var sd = MathF.Sqrt(syncErr.Sum(e => (e - m) * (e - m)) / syncErr.Count);
    var beatMs = analyzer.Bpm is { } b3 ? 60_000f / b3 : 625f;
    Console.WriteLine("\ncourbe du tempo, meilleures periodes :");
foreach (var (pb, raw, sc) in analyzer.TempoPeaks(6))
    Console.WriteLine($"   {pb,6:F1} BPM   correlation {raw,6:F3}   score {sc,6:F3}");
Console.WriteLine("   —— hypotheses connues ——");
foreach (var t in new[] { 87f, 90f, 93f, 96f, 104f, 108f, 117f, 45f, 180f })
    Console.WriteLine($"   {t,6:F1} BPM   correlation {analyzer.TempoRawAt(t),6:F3}");
Console.WriteLine($"periode de la grille  mediane {Median(gridMs):F0} ms" +
    (tempoMs.Count > 0 ? $"  ·  tempo publie {Median(tempoMs):F0} ms  ·  ecart {100f * (Median(gridMs) - Median(tempoMs)) / Median(tempoMs):+0.0;-0.0} %" : "  ·  tempo jamais publie"));
Console.WriteLine($"justesse de phase   ecart moyen {m:F3} temps = {m * beatMs:F0} ms" +
                      $"  ·  dispersion {sd:F3}  ·  median {Median(syncErr):F3}");
    Console.WriteLine($"position dans la fenetre  moyenne {offsets.Average():F1} ms sur 21");
}
Console.WriteLine($"registres tonals    {vLow} voix graves · {vMid} medium · {vHigh} aigues");
Console.WriteLine($"indices de structure {chordChanges} changements d'accord · {novelties} ruptures");

Console.WriteLine($"\nconfiance du temps fort  finale {confidences[^1]:F2} · mediane {Median(confidences):F2}");
Console.WriteLine($"verrouille sur           {confidences.Count(c => c > 0.35f) * 100 / confidences.Count} % des fenetres");
Console.WriteLine($"temps non nomme          {beatHisto[0] * 100 / confidences.Count} % des fenetres");

if (barStarts.Count > 2)
{
    var gaps = barStarts.Zip(barStarts.Skip(1), (a, b) => (float)(b - a)).ToList();
    var med = Median(gaps);
    // Une mesure vaut quatre temps : la comparaison au tempo dit si le compteur suit
    // vraiment la musique ou s'il derive tout seul.
    var expected = analyzer.Bpm is { } bpm ? 4f * 60_000f / bpm : float.NaN;
    var off = gaps.Count(g => MathF.Abs(g - med) > med * 0.25f);
    Console.WriteLine($"\nmesures comptees    {barStarts.Count}");
    Console.WriteLine($"  intervalle median {med:F0} ms (attendu {expected:F0} ms au tempo detecte)");
    Console.WriteLine($"  irreguliers       {off} sur {gaps.Count}");
}

Console.WriteLine($"\nlongueur de phrase  " +
    string.Join("  ", phraseLen.OrderByDescending(k => k.Value)
        .Select(k => $"{k.Key} mesures : {k.Value * 100 / confidences.Count} %")));
var (sBars, sBest, sScores) = analyzer.Section;
Console.WriteLine($"scores de phrase    " + string.Join("  ",
    SectionTracker.Candidates.Select((c, i) => $"{c}:{sScores[i]:F3}")) +
    $"   retenu {sBars}");
var (dOff, dConf, dScores, _) = analyzer.Section is var _ ? analyzer.Downbeat : default;
Console.WriteLine($"ruptures de continuite  {ruptures}" + (ruptures > 0 ? $"  (derniere : {lastReason})" : ""));
Console.WriteLine($"profil du temps fort  offset {dOff} · confiance finale {dConf:F2} · mediane {Median(profileConf):F2}");
Console.WriteLine($"  scores  " + string.Join(" ", dScores.Select((v, i) => $"{i}:{v:+0.000;-0.000}")));
Console.WriteLine($"confiance section   mediane {Median(sectionConf):F2} · max {sectionConf.Max():F2}");
Console.WriteLine($"phrases             {phraseStarts.Count}");
if (phraseStarts.Count > 2)
{
    var pg = phraseStarts.Zip(phraseStarts.Skip(1), (a, b) => (float)(b - a)).ToList();
    var pmed = Median(pg);
    Console.WriteLine($"  intervalle median {pmed / 1000f:F1} s  ·  " +
                      $"reguliers {pg.Count(g => MathF.Abs(g - pmed) < pmed * 0.2f)}/{pg.Count}");
}

var totalBreaks = breakBar.Sum();
// Compteur libre : celui-la n'est jamais realigne, donc la position d'une rupture y a
// un sens. C'est le seul moyen de savoir si les ruptures tombent vraiment toutes les
// 4, 8 ou 16 mesures.
if (freeBreaks.Count >= 4)
{
    Console.WriteLine($"\nsur un compteur de mesures LIBRE, {freeBreaks.Count} ruptures :");
    foreach (var period in new[] { 4, 8, 16 })
    {
        var histo = new int[period];
        foreach (var b in freeBreaks) histo[b % period]++;
        var flat2 = freeBreaks.Count / (float)period;
        var chi2 = histo.Sum(c => (c - flat2) * (c - flat2) / flat2);
        Console.WriteLine($"  modulo {period,2} : {string.Join(" ", histo)}" +
                          $"   ecart au hasard {chi2:F1}");
    }
}

if (totalBreaks >= 4)
{
    Console.WriteLine($"\nou tombent les {totalBreaks} ruptures :");
    Console.WriteLine("  mesure  " + string.Join(" ", breakBar.Select((c, i) => $"{i}:{c}")));
    Console.WriteLine("  temps   " + string.Join(" ", breakBeat.Select((c, i) => $"{i}:{c}")));

    // Une repartition au hasard donnerait autant de ruptures par mesure. L'ecart a cette
    // repartition dit si la phrase existe vraiment, et si elle est de la bonne longueur.
    var flat = totalBreaks / (float)Structure.DefaultPhraseBars;
    var chi = breakBar.Sum(c => (c - flat) * (c - flat) / flat);
    Console.WriteLine($"  ecart au hasard  {chi:F1}  (0 = reparti au hasard, > 14 = concentre)");
    Console.WriteLine("  ATTENTION : biaise. AlignPhrase remet le compteur a zero a chaque");
    Console.WriteLine("  rupture, donc les trouver sur la mesure 0 ne prouve rien.");

    if (breakGaps.Count >= 3)
    {
        var g = breakGaps.OrderBy(x => x).ToList();
        Console.WriteLine($"  espacement median  {g[g.Count / 2]} mesures  " +
                          $"(multiples de 4 : {breakGaps.Count(x => x % 4 == 0)}/{breakGaps.Count})");
    }
}
Console.WriteLine($"tension mediane     {Median(buildups):F2} · maximum {buildups.Max():F2}");

// Ce que le pipeline a mesure sur cette machine, et ce qu'il en a conclu. La decision
// n'est pas ecrite dans le code : elle depend du nombre de coeurs et de ce que coute une
// repartition ici.
var pipe = analyzer.SourcePipeline;
var (seqUs, parUs) = pipe.Cost;
Console.WriteLine($"voies               {(pipe.Parallel ? "PARALLELE" : "SEQUENTIEL")} retenu · " +
                  $"sequentiel {seqUs:F1} us/image · parallele {parUs:F1} us/image");
coutImage.Sort();
var pireImage = coutImage[^1];
var p99 = coutImage[(int)(coutImage.Count * 0.99)];
var enRetard = coutImage.Count(x => x > 21.3);
Console.WriteLine($"cout d'une image    median {coutImage[coutImage.Count / 2]:F2} ms · " +
                  $"99e centile {p99:F2} ms · pire {pireImage:F1} ms · " +
                  $"{enRetard} images au-dessus du pas de 21 ms");

Console.WriteLine($"annonces de tempo   {annonces.Count} : " +
                  string.Join(" · ", annonces.Take(12).Select(a => $"{a.T / 1000f:F0}s {a.Bpm:F1}")));
Console.WriteLine("maturite des sources  (confiance 0,6 atteinte a)");
for (var r = 0; r < Voices.Registers; r++)
{
    var e = analyzer.Voix.EtatDe(r);
    var id = analyzer.Voix.PortraitDe(r);
    Console.WriteLine($"  source {r} : " +
                      (murAt[r] < 0 ? "jamais       " : $"{murAt[r] / 1000f,6:F1} s     ") +
                      $"confiance {e.Confidence:F2} · dispersion {id.Spread:F3} · " +
                      $"{id.Observations} observations · brillance {e.Brightness:F2} · texture {e.Texture:F2}");
}

var sep = analyzer.Separation;
Console.WriteLine($"apprentissage NMF   {sep.Apprentissages} fois · moyenne {sep.ApprentissageMs:F1} ms · " +
                  $"pire {sep.ApprentissagePireMs:F1} ms  (une image dure 21 ms)");
Console.WriteLine($"ruptures            {drops.Count}" +
                  (drops.Count > 0 ? "  a " + string.Join(", ", drops.Select(d => $"{d / 1000f:F0} s")) : ""));
if (exportTo is not null)
{
    File.WriteAllText(exportTo,
        "{\"rate\":" + rate + ",\"window\":" + hop +
        ",\"latenceMs\":" + analyzer.LatencyMs.ToString("F0") +
        ",\"champs\":[\"t\",\"rms\",\"bandes\",\"drapeaux\",\"voixGrave\"," +
        "\"voixMedium\",\"voixAigue\",\"ouverture\",\"brillance\",\"densite\"," +
        "\"bpm\",\"temps\",\"tension\",\"nouveaute\",\"confianceTemps\",\"tonalite\",\"pitchGrave\",\"pitchMedium\",\"pitchAigu\",\"registres\",\"contours\",\"attaques\",\"maturites\",\"noms\",\"bpmAnnonce\",\"annonce\",\"derive\"]" +
        ",\"images\":[\n" + string.Join(",\n", exported) + "\n]}");
    Console.WriteLine($"\n{exported.Count} images exportees vers {exportTo}");
}

return 0;

static float Pct(List<float> v, int p)
{
    if (v.Count == 0) return 0f;
    var c = new List<float>(v);
    c.Sort();
    return c[Math.Clamp(c.Count * p / 100, 0, c.Count - 1)];
}

static float Median(List<float> v)
{
    var c = new List<float>(v);
    c.Sort();
    return c.Count == 0 ? 0f : c[c.Count / 2];
}

/// <summary>Lecteur WAV minimal : PCM 16 ou 24 bits, replie en mono.</summary>
static class Wav
{
    public static (float[] Samples, int Rate) ReadMono(string path, double startS, double lengthS)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);

        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("pas un RIFF");
        r.ReadUInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("pas un WAVE");

        int channels = 0, rate = 0, bits = 0;
        long dataOffset = 0, dataSize = 0;

        while (fs.Position + 8 <= fs.Length)
        {
            var id = new string(r.ReadChars(4));
            var size = r.ReadUInt32();
            var next = fs.Position + size + (size % 2);

            if (id == "fmt ")
            {
                r.ReadUInt16();
                channels = r.ReadUInt16();
                rate = (int)r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt16();
                bits = r.ReadUInt16();
            }
            else if (id == "data")
            {
                dataOffset = fs.Position;
                dataSize = size;
                break;
            }

            fs.Position = next;
        }

        if (bits != 16 && bits != 24) throw new NotSupportedException($"{bits} bits non gere");

        var bytesPerSample = bits / 8;
        var frameBytes = channels * bytesPerSample;
        var totalFrames = dataSize / frameBytes;
        var from = Math.Min((long)(startS * rate), totalFrames);
        var count = (long)Math.Min(lengthS * rate, totalFrames - from);

        fs.Position = dataOffset + from * frameBytes;
        var raw = r.ReadBytes((int)(count * frameBytes));

        var mono = new float[count];
        for (long i = 0; i < count; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                var o = (int)(i * frameBytes + c * bytesPerSample);
                sum += bits == 16
                    ? BitConverter.ToInt16(raw, o) / 32768f
                    // 24 bits petit-boutiste signe : on reconstitue le mot puis on etend
                    // le bit de signe depuis le rang 23.
                    : ((raw[o] | (raw[o + 1] << 8) | ((sbyte)raw[o + 2] << 16))) / 8388608f;
            }
            mono[i] = sum / channels;
        }

        return (mono, rate);
    }
}
