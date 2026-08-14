using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Arrowgene.DJMaxOnline.Server;

public enum SongKeyMode
{
    FiveKey = 5,
    SevenKey = 7
}

public enum SongDifficulty
{
    Easy,
    Normal,
    Hard,
    Maximum,
    Special
}

public sealed record SongChartDefinition(
    SongKeyMode KeyMode,
    SongDifficulty Difficulty,
    int DiscLevel,
    int UserLevel,
    decimal ReferenceSpeed)
{
    public bool IsAvailable => DiscLevel != 99;

    public string DifficultyCode => Difficulty switch
    {
        SongDifficulty.Easy => "EZ",
        SongDifficulty.Normal => "NM",
        SongDifficulty.Hard => "HD",
        SongDifficulty.Maximum => "MX",
        SongDifficulty.Special => "SC",
        _ => throw new ArgumentOutOfRangeException()
    };
}

public sealed record SongDefinition(
    uint Id,
    string Title,
    string Subtitle,
    string Version,
    string Genre,
    decimal Bpm,
    string Composer,
    string Artist,
    int PlayTimeSeconds,
    string Tag,
    /// <summary>
    /// DiscStock column 14. "ORG" is the original cut; "rm1" marks a REMIX that ships
    /// inside the same pak under the same <see cref="Tag"/>. Elastic Star is the only
    /// song that does this - ids 5 (ORG) and 26 (rm1) both tag as "ElasticSTAR" - so the
    /// tag alone does not identify a chart set and the pattern lookup has to split on
    /// this as well.
    /// </summary>
    string VersionTag,
    string Status,
    IReadOnlyList<SongChartDefinition> Charts,
    IReadOnlyList<string> SourceFields)
{
    /// <summary>
    /// The title as a person should read it.
    ///
    /// DiscStock.csv cannot hold a space, so it writes '_' instead and the client swaps it
    /// back when it draws a title. CHAT DOES NOT: text sent in a chat packet is rendered
    /// verbatim, so anything the server composes has to do the swap itself or players see
    /// "Y_Have_To_Follow_Me".
    ///
    /// A lone '_' is DiscStock's empty marker, not a one-space title.
    /// </summary>
    public string DisplayTitle => Display(Title);

    /// <summary>Turns a DiscStock field into readable text. See <see cref="DisplayTitle"/>.</summary>
    public static string Display(string value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text is "" or "_")
        {
            return string.Empty;
        }

        text = text.Replace('_', ' ').Trim();
        // Retail DiscStock wrote every title as "♪_Name". This catalog no longer carries
        // the glyph, but stripping a leading one costs nothing and keeps an older or
        // reimported CSV rendering the same way - it draws as an empty box in the client's
        // chat font, so it should never reach a player either way.
        return text.StartsWith(MusicNote) ? text[MusicNote.Length..].TrimStart() : text;
    }

    /// <summary>Decoration retail prefixed DiscStock titles with; stripped for display.</summary>
    public const string MusicNote = "♪";

    public SongChartDefinition? FindChart(
        SongKeyMode keyMode,
        SongDifficulty difficulty) =>
        Charts.FirstOrDefault(chart =>
            chart.KeyMode == keyMode && chart.Difficulty == difficulty);

    public string ChartFileName(
        SongKeyMode keyMode,
        SongDifficulty difficulty)
    {
        SongChartDefinition chart = FindChart(keyMode, difficulty) ??
                                    throw new ArgumentException(
                                        $"Song {Id} has no {keyMode}/{difficulty} definition.");
        return $"{Tag.ToLowerInvariant()}_{(int)keyMode}k{chart.DifficultyCode}.pt";
    }
}

/// <summary>
/// The merged client-side DiscStock.csv catalog. Column positions are used because
/// the source has duplicate header names for the 5-key and 7-key chart columns.
/// </summary>
public sealed class SongCatalog
{
    // Column layout differs between the merged China DiscStock (29 columns) and the
    // Korean client's DiscStock (25 columns). Columns 0..13 (ID_ .. Tag) are identical;
    // only Status and the per-difficulty chart-level/speed columns move. Detected by the
    // header column count. (Korean has a single Deposit column reused for both speeds.)
    private readonly record struct CatalogLayout(
        int ColumnCount, int Status,
        int FiveDisc, int FiveUser, int FiveSpeed,
        int SevenDisc, int SevenUser, int SevenSpeed);

    private static readonly CatalogLayout ChinaLayout =
        new(29, 18, 20, 21, 27, 22, 23, 28);
    private static readonly CatalogLayout KoreanLayout =
        new(25, 17, 19, 20, 23, 21, 22, 23);

    private static readonly SongDifficulty[] Difficulties =
    [
        SongDifficulty.Easy,
        SongDifficulty.Normal,
        SongDifficulty.Hard,
        SongDifficulty.Maximum,
        SongDifficulty.Special
    ];

    private readonly IReadOnlyDictionary<uint, SongDefinition> _byId;
    private readonly IReadOnlyDictionary<string, SongDefinition> _byTag;

    private SongCatalog(
        string sourcePath,
        IDictionary<uint, SongDefinition> byId,
        IDictionary<string, SongDefinition> byTag)
    {
        SourcePath = sourcePath;
        _byId = new ReadOnlyDictionary<uint, SongDefinition>(byId);
        _byTag = new ReadOnlyDictionary<string, SongDefinition>(byTag);
    }

    public string SourcePath { get; }
    public int Count => _byId.Count;
    public IEnumerable<SongDefinition> Songs => _byId.Values.OrderBy(song => song.Id);

    /// <summary>
    /// The disc index the client sends for its RANDOM slot: one past the last real disc in
    /// its own list. A disc index is <c>catalog id - 1</c>, so the last real index is
    /// <c>maxId - 1</c> and the random slot sits at <c>maxId</c> - a 198-song catalog makes
    /// it 198. Derived rather than pinned, so it follows the song list instead of going
    /// stale the moment a chart is added.
    /// </summary>
    public uint RandomDiscIndex => _byId.Count == 0 ? 0 : _byId.Keys.Max();

    public static SongCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Song catalog was not found.", path);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // CP949 (Korean), like every other client file the server reads. This was 936
        // (Simplified Chinese) left over from the China client, which decoded the Korean
        // catalog into plausible-looking Chinese rather than failing: "♪_바람에게_부탁해"
        // came out as "④_官恩俊霸_何殴秦", and that is what the chart announcement showed
        // in chat. Nothing errors when this is wrong - the titles are just quietly wrong.
        string[] lines = File.ReadAllLines(path, Encoding.GetEncoding(949));
        if (lines.Length < 2)
        {
            throw new InvalidDataException($"Song catalog {path} contains no records.");
        }

        string[] header = ParseCsvLine(lines[0]);
        CatalogLayout layout;
        if (header.Length == KoreanLayout.ColumnCount)
        {
            layout = KoreanLayout;
        }
        else if (header.Length == ChinaLayout.ColumnCount)
        {
            layout = ChinaLayout;
        }
        else
        {
            throw new InvalidDataException(
                $"Song catalog {path} has an unexpected {header.Length}-column header " +
                $"(expected {ChinaLayout.ColumnCount} or {KoreanLayout.ColumnCount}).");
        }
        if (!string.Equals(header[0], "ID_", StringComparison.Ordinal) ||
            !string.Equals(header[13], "Tag", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Song catalog {path} header is missing the ID_/Tag columns.");
        }

        Dictionary<uint, SongDefinition> byId = [];
        Dictionary<string, SongDefinition> byTag =
            new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            string[] fields = ParseCsvLine(lines[index]);
            if (fields.Length != layout.ColumnCount)
            {
                throw new InvalidDataException(
                    $"Song catalog row {index + 1} has {fields.Length} fields; " +
                    $"expected {layout.ColumnCount}.");
            }

            SongDefinition song = ParseSong(fields, index + 1, layout);
            if (!byId.TryAdd(song.Id, song))
            {
                throw new InvalidDataException(
                    $"Song catalog contains duplicate id {song.Id}.");
            }
            // Disc id is the authoritative key used to serve charts; the by-tag index is a
            // convenience only. The Korean catalog legitimately repeats a tag across song
            // variants (e.g. ElasticSTAR ORG + remix), so keep the first and don't fail.
            byTag.TryAdd(song.Tag, song);
        }

        return new SongCatalog(path, byId, byTag);
    }

    public bool TryGet(uint id, out SongDefinition song) =>
        _byId.TryGetValue(id, out song!);

    public bool TryGet(string tag, out SongDefinition song) =>
        _byTag.TryGetValue(tag, out song!);

    public SongDefinition Get(uint id) =>
        TryGet(id, out SongDefinition? song)
            ? song
            : throw new KeyNotFoundException($"Unknown DJMax song id {id}.");

    /// <summary>
    /// Chooses a real song that has the requested playable chart. The client represents
    /// RANDOM as a synthetic disc after the final catalog entry, so resolving that entry
    /// must never return the synthetic id itself. When possible, the current song is
    /// excluded so repeatedly pressing RANDOM visibly produces a new selection.
    /// </summary>
    public bool TryPickRandomPlayable(
        SongKeyMode keyMode,
        SongDifficulty difficulty,
        uint? excludedCatalogId,
        Random random,
        out SongDefinition song)
    {
        ArgumentNullException.ThrowIfNull(random);

        SongDefinition[] candidates = _byId.Values
            .Where(candidate =>
                candidate.Id > 0 &&
                candidate.FindChart(keyMode, difficulty) is { IsAvailable: true })
            .OrderBy(candidate => candidate.Id)
            .ToArray();
        if (candidates.Length == 0)
        {
            song = null!;
            return false;
        }

        if (candidates.Length > 1 && excludedCatalogId is uint excluded)
        {
            SongDefinition[] alternatives = candidates
                .Where(candidate => candidate.Id != excluded)
                .ToArray();
            if (alternatives.Length != 0)
            {
                candidates = alternatives;
            }
        }

        song = candidates[random.Next(candidates.Length)];
        return true;
    }

    private static SongDefinition ParseSong(string[] fields, int row, CatalogLayout layout)
    {
        uint id = ParseUInt32(fields[0], row, "ID_");
        string tag = fields[13].Trim();
        if (tag.Length == 0 || tag.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new InvalidDataException($"Song catalog row {row} has invalid tag '{tag}'.");
        }

        List<SongChartDefinition> charts = [];
        AddCharts(
            charts,
            SongKeyMode.FiveKey,
            ParseIntVector(fields[layout.FiveDisc], row, "5-key DiscLevel"),
            ParseIntVector(fields[layout.FiveUser], row, "5-key UserLevel"),
            ParseDecimalVector(fields[layout.FiveSpeed], row, "5-key RefSpeed"));
        AddCharts(
            charts,
            SongKeyMode.SevenKey,
            ParseIntVector(fields[layout.SevenDisc], row, "7-key DiscLevel"),
            ParseIntVector(fields[layout.SevenUser], row, "7-key UserLevel"),
            ParseDecimalVector(fields[layout.SevenSpeed], row, "7-key RefSpeed"));

        return new SongDefinition(
            id,
            fields[1],
            fields[2],
            fields[3],
            fields[4],
            ParseDecimal(fields[6], row, "BPM"),
            fields[7],
            fields[10],
            ParseInt32(fields[12], row, "PlayTime"),
            tag,
            fields[14],
            fields[layout.Status],
            charts.AsReadOnly(),
            Array.AsReadOnly(fields.ToArray()));
    }

    private static void AddCharts(
        ICollection<SongChartDefinition> destination,
        SongKeyMode keyMode,
        IReadOnlyList<int> discLevels,
        IReadOnlyList<int> userLevels,
        IReadOnlyList<decimal> speeds)
    {
        for (int index = 0; index < Difficulties.Length; index++)
        {
            destination.Add(new SongChartDefinition(
                keyMode,
                Difficulties[index],
                discLevels[index],
                userLevels[index],
                speeds[index]));
        }
    }

    private static int[] ParseIntVector(string value, int row, string name)
    {
        string[] parts = ParseVector(value, row, name);
        return parts.Select(part => ParseInt32(part, row, name)).ToArray();
    }

    private static decimal[] ParseDecimalVector(string value, int row, string name)
    {
        string[] parts = ParseVector(value, row, name);
        return parts.Select(part => ParseDecimal(part, row, name)).ToArray();
    }

    private static string[] ParseVector(string value, int row, string name)
    {
        string[] parts = value.Split('_');
        if (parts.Length != Difficulties.Length)
        {
            throw new InvalidDataException(
                $"Song catalog row {row} {name} has {parts.Length} values; expected 5.");
        }
        return parts;
    }

    private static uint ParseUInt32(string value, int row, string name) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Song catalog row {row} has invalid {name} value '{value}'.");

    private static int ParseInt32(string value, int row, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Song catalog row {row} has invalid {name} value '{value}'.");

    private static decimal ParseDecimal(string value, int row, string name) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Song catalog row {row} has invalid {name} value '{value}'.");

    private static string[] ParseCsvLine(string line)
    {
        List<string> values = [];
        StringBuilder value = new();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("Song catalog contains an unterminated CSV quote.");
        }
        values.Add(value.ToString());
        return values.ToArray();
    }
}
