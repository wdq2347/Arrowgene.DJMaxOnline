using System.Text.RegularExpressions;
using Arrowgene.DJMaxOnline.Server.China260.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.China260;

/// <summary>Supplies an OnGameInfoInf chart payload for a requested disc.</summary>
public interface IGameInfoProvider
{
    /// <param name="roomDescriptor">
    /// The signed int16 the client folds into its chart-descramble key
    /// (<c>*(__int16*)(base+794255)</c>) — it is the room index the server sent in
    /// OnCreateRoomAck/OnJoinRoomAck. The scramble must use the same value or the
    /// client's LZO stream is garbage and it crashes in the decompressor.
    /// </param>
    /// <param name="difficulty">
    /// The chart difficulty the host selected, taken from the first byte of the
    /// ChangeDiscReq settings field: 0=EZ, 1=NM, 2=HD, 3=MX, 4=SC.
    /// </param>
    /// <param name="windows">
    /// Judgment windows to ship with the chart, or null for the retail values. The client
    /// has no online judgment table of its own, so this is what decides timing strictness.
    /// Providers that replay captured payloads cannot honour it.
    /// </param>
    bool TryLoad(
        uint discId,
        byte difficulty,
        short roomDescriptor,
        out GameInfoPayload? payload,
        out bool usedFallback,
        out string error,
        JudgmentWindows? windows = null);
}

/// <summary>
/// Generates game-info payloads on the fly from plaintext PTFF charts in a
/// Patterns directory, reproducing the retail server's session-keyed encoding
/// (<see cref="GameInfoBlobBuilder"/>). The client addresses discs by a 0-based
/// index into its catalog, so the merged (1-based) DiscStock id is index + 1.
/// </summary>
public sealed class PatternGameInfoProvider : IGameInfoProvider
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(PatternGameInfoProvider));

    private readonly SongCatalog _songs;
    private readonly string _patternsDirectory;
    private readonly SongKeyMode _keyMode;
    private readonly string _sessionLabel;

    public PatternGameInfoProvider(
        SongCatalog songs,
        string patternsDirectory,
        SongKeyMode keyMode,
        string sessionLabel)
    {
        _songs = songs ?? throw new ArgumentNullException(nameof(songs));
        ArgumentException.ThrowIfNullOrWhiteSpace(patternsDirectory);
        _patternsDirectory = Path.GetFullPath(patternsDirectory);
        _keyMode = keyMode;
        _sessionLabel = sessionLabel ?? throw new ArgumentNullException(nameof(sessionLabel));
    }

    public bool TryLoad(
        uint discId,
        byte difficulty,
        short roomDescriptor,
        out GameInfoPayload? payload,
        out bool usedFallback,
        out string error,
        JudgmentWindows? windows = null)
    {
        payload = null;
        usedFallback = false;
        error = string.Empty;

        // The client addresses discs by a 0-based index into its catalog, so the
        // merged (1-based) DiscStock id is index + 1. The served chart's keysound
        // samples must match the song the client loaded locally, so this MUST agree
        // with the client's selection — serving the neighbour silences all audio.
        uint catalogId = discId + 1;
        if (!_songs.TryGet(catalogId, out SongDefinition? song))
        {
            error = $"No catalog entry for disc index {discId} (catalog id {catalogId}).";
            return false;
        }

        // Order candidates so the host's selected difficulty comes first, then the
        // neighbouring difficulties, so an exact match wins but a missing chart still
        // falls back to a playable one.
        List<string> candidates = FindPatterns(song.Tag, song.VersionTag, difficulty);
        if (candidates.Count == 0)
        {
            error =
                $"No {(int)_keyMode}-key pattern for '{song.Tag}' in {_patternsDirectory}.";
            return false;
        }

        // Serve the first candidate whose PTFF structure the client can actually
        // parse. Corrupt/truncated dumps are skipped so the client never receives a
        // chart that freezes it or runs it out of memory; a valid difficulty of the
        // same song is used instead when one exists.
        string? rejected = null;
        foreach (string path in candidates)
        {
            byte[] ptff;
            try
            {
                ptff = File.ReadAllBytes(path);
            }
            catch (IOException ex)
            {
                rejected ??= $"{Path.GetFileName(path)}: {ex.Message}";
                continue;
            }

            if (!PtffValidator.IsValid(ptff, out string invalidReason))
            {
                Logger.Info(
                    $"Skipping {Path.GetFileName(path)} for disc {discId} " +
                    $"({song.Tag}): {invalidReason}.");
                rejected ??= $"{Path.GetFileName(path)}: {invalidReason}";
                continue;
            }

            try
            {
                byte[] blob = GameInfoBlobBuilder.Build(
                    discId, difficulty, chartType: 0, ptff, _sessionLabel,
                    descriptorValue: roomDescriptor, windows);
                payload = GameInfoPayload.Parse(blob);

                // Log the exact key inputs so a client-side de-scramble crash can be
                // diagnosed by comparing them with the client's live state.
                uint v44 = GameInfoScrambler.ComputeV44(
                    blob.AsSpan(GameplayProtocol.GameInfoDiscIdOffset, 6),
                    _sessionLabel, roomDescriptor);
                // WHAT WAS ASKED FOR vs WHAT WENT OUT. A chart whose name carries no
                // difficulty of its own, or one for a different difficulty, is still
                // served - TryLoad falls back rather than failing - so without this the
                // player is the first to notice they got the wrong chart.
                string servedName = Path.GetFileName(path);
                string served = ServedDifficulty(servedName);
                bool exact = served == DifficultyName(difficulty);
                usedFallback = !exact;
                Logger.Info(
                    $"Chart request: disc {discId} ({song.Tag}) " +
                    $"{(int)_keyMode}-key {DifficultyName(difficulty)} " +
                    $"-> served {served} from {servedName}" +
                    (exact ? " (exact match)." : " (FALLBACK - not the requested difficulty)."));
                if (!exact)
                {
                    // Publish it too, so the wrong-chart case can be watched from
                    // outside instead of grepping the log after a player complains.
                    ChartServeIssues.Record(
                        discId, song.Tag, (int)_keyMode,
                        DifficultyName(difficulty), served, servedName);
                }
                Logger.Info(
                    $"Generated game-info for disc {discId} ({song.Tag}) " +
                    $"difficulty {DifficultyName(difficulty)} from " +
                    $"{servedName} ({ptff.Length}-byte PTFF); " +
                    $"key inputs: session=\"{_sessionLabel}\" diff={difficulty} " +
                    $"int16={roomDescriptor} v44={v44:X8}.");
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or InvalidDataException or ArgumentException)
            {
                rejected ??= $"{Path.GetFileName(path)}: {ex.Message}";
            }
        }

        error =
            $"No usable {(int)_keyMode}-key chart for '{song.Tag}'; " +
            $"{candidates.Count} candidate(s) were all invalid" +
            (rejected == null ? "." : $" (e.g. {rejected}).");
        return false;
    }

    // Difficulty index (from ChangeDiscReq settings byte 0) → chart codes/aliases as
    // they appear in the many filename schemes (jbg_7kez3, jbg_7kMX, jbg_sc_7k,
    // trumpet_normal_7k_7, TAG_ORG_NM_7KEY, ...).
    private static readonly string[][] DifficultyTokens =
    [
        ["ez", "easy"],       // 0 EZ
        ["nm", "normal"],     // 1 NM
        ["hd", "hard"],       // 2 HD
        ["mx", "maximum"],    // 3 MX
        ["sc", "special"],    // 4 SC
    ];

    /// <summary>
    /// The difficulty a chart FILE actually is, read from its name. Returns "unnamed"
    /// when the name carries no difficulty token - those files are resolved by falling
    /// back, so the request and the reply can silently disagree.
    /// </summary>
    private string ServedDifficulty(string fileName)
    {
        string stem = fileName;
        while (stem.EndsWith(".pt", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^3];
        }
        stem = stem.ToLowerInvariant();
        for (byte d = 0; d < DifficultyNames.Length; d++)
        {
            if (RestMatchesDifficulty(stem, d))
            {
                return DifficultyNames[d];
            }
        }
        return "unnamed";
    }

    private static readonly string[] DifficultyNames = ["EZ", "NM", "HD", "MX", "SC"];

    private static string DifficultyName(byte difficulty) =>
        difficulty < DifficultyNames.Length ? DifficultyNames[difficulty] : $"?{difficulty}";

    private static bool RestMatchesDifficulty(string rest, byte difficulty)
    {
        if (difficulty >= DifficultyTokens.Length)
        {
            return false;
        }

        foreach (string token in DifficultyTokens[difficulty])
        {
            // The code appears bounded by an underscore, the key marker (e.g. "7k"),
            // start, end, or a level digit — never mid-word.
            if (Regex.IsMatch(rest, $@"(^|_|[0-9]k){token}([0-9]|_|$)"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds a chart for <paramref name="tag"/> in the configured key mode,
    /// accepting every observed naming scheme:
    ///   &lt;tag&gt;_5k.pt / _7k.pt          (base)
    ///   &lt;tag&gt;_7kNM8.pt / _7kMX.pt      (key + difficulty + level)
    ///   &lt;tag&gt;_normal_7k_4.pt           (difficulty word + level)
    ///   &lt;TAG&gt;_ORG_NM_7KEY.pt           (ORG variant)
    /// Returns every match ordered best-first: the requested difficulty, then the
    /// plain base name, then the shortest names (fewest qualifiers). <see cref="TryLoad"/>
    /// walks the list and serves the first chart that passes <see cref="PtffValidator"/>,
    /// so a missing/corrupt difficulty still falls back to a playable chart.
    /// </summary>
    /// <summary>
    /// A REMIX shares its pak and its tag with the original, and the two chart sets sit
    /// side by side in the patterns folder distinguished only by "_remix_" in the file
    /// name. Elastic Star is the only song that does it, but matching on the tag alone
    /// pools both sets, so the original could be served a remix chart and vice versa.
    /// </summary>
    /// <summary>
    /// The file name with EVERY trailing .pt removed, lowercased.
    ///
    /// Some dumps carry a doubled extension - "luvflow_5k_MX.pt.PT" - and stripping only
    /// the last one leaves ".pt" glued to the difficulty token. The difficulty matcher
    /// needs the token bounded by an underscore, a digit or the end of the string, so
    /// "5k_mx.pt" failed to register as MX and the chart lost to the plain base name.
    /// Asking for MX then quietly served HD.
    /// </summary>
    private static string PatternStem(string file)
    {
        string stem = Path.GetFileName(file);
        while (stem.EndsWith(".pt", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^3];
        }
        return stem.ToLowerInvariant();
    }

    private static bool IsRemixVersion(string versionTag) =>
        versionTag.TrimStart().StartsWith("rm", StringComparison.OrdinalIgnoreCase);

    private static bool IsRemixPattern(string stem) =>
        stem.Contains("_remix", StringComparison.Ordinal);

    private List<string> FindPatterns(string tag, string versionTag, byte difficulty)
    {
        if (!Directory.Exists(_patternsDirectory))
        {
            return [];
        }

        string t = tag.ToLowerInvariant();
        int k = (int)_keyMode;
        string baseName = $"{t}_{k}k.pt";
        List<(string File, string Suffix)> matches = [];

        foreach (string file in Directory.EnumerateFiles(_patternsDirectory))
        {
            if (!file.EndsWith(".pt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string stem = PatternStem(file);
            if (!stem.StartsWith(t + "_", StringComparison.Ordinal))
            {
                continue;
            }

            // Keep only the cut this disc actually is.
            if (IsRemixPattern(stem) != IsRemixVersion(versionTag))
            {
                continue;
            }

            string rest = stem[(t.Length + 1)..];
            if (Regex.IsMatch(rest, $@"(^|_){k}k") || Regex.IsMatch(rest, $@"_{k}key$"))
            {
                matches.Add((file, rest));
            }
        }

        return matches
            .OrderByDescending(m => RestMatchesDifficulty(m.Suffix, difficulty))
            .ThenByDescending(m =>
                Path.GetFileName(m.File).Equals(baseName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => Path.GetFileName(m.File).Length)
            .Select(m => m.File)
            .ToList();
    }
}
