using System.Text.RegularExpressions;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Pak;

public sealed record ImportedFile(string FileName, string SourcePak, string ArchivePath, int Bytes);

public sealed record GameDataImportResult(
    IReadOnlyList<ImportedFile> Imported,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Paks)
{
    public bool Any => Imported.Count > 0;
}

/// <summary>
/// Pulls the game-info files the server needs out of the client's <c>system.pak</c> into
/// the DATA folder - the catalogs (DiscStock, ItemStock, IconSet...), the shop lists and
/// the course scripts.
///
/// Patch paks are layered exactly the way the client layers them (sub_4A58C0): the base
/// <c>system.pak</c> first, then <c>system_0001.pak</c>, <c>system_0002.pak</c> and so on
/// in numeric order, each overriding what came before. A file only present in a later
/// patch still lands, and a file replaced by one wins over the base copy.
/// </summary>
public sealed partial class GameDataImporter
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(GameDataImporter));

    /// <summary>
    /// What the server actually reads out of DATA, described by where it lives inside the
    /// pak rather than as a flat list of names - so a client that adds another shop list
    /// or icon set is picked up without touching this.
    /// </summary>
    private static readonly (string Directory, string Pattern)[] Wanted =
    [
        ("song", "discstock.csv"),
        ("system", "fontset.csv"),
        ("system/icon", "iconset*.csv"),
        ("system/shop", "*.lst"),
        ("system/shop", "itemstock.csv"),
        ("system/shop", "itemsetinfo.csv"),
        ("system/courseclub", "*.ini")
    ];

    /// <summary>
    /// Never overwrite these, whatever a pak claims: the player database and our own notes
    /// live in the same folder as the imported catalogs.
    /// </summary>
    private static readonly string[] Protected =
        ["djmax.sqlite3", "djmax.sqlite3-wal", "djmax.sqlite3-shm", "readme.md", "settings.ini"];

    [GeneratedRegex(@"^(?<base>.+?)(?:_(?<patch>\d{4}))?\.pak$", RegexOptions.IgnoreCase)]
    private static partial Regex PakName();

    /// <summary>
    /// Orders <c>system.pak</c>, <c>system_0001.pak</c>, ... so later patches win. Any pak
    /// sharing the base name is included; anything else is ignored.
    /// </summary>
    public static IReadOnlyList<string> ResolvePakChain(string pakPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);

        string full = Path.GetFullPath(pakPath);
        string directory = Path.GetDirectoryName(full) ??
                           throw new InvalidDataException($"{pakPath} has no directory.");
        Match self = PakName().Match(Path.GetFileName(full));
        if (!self.Success)
        {
            throw new InvalidDataException($"{pakPath} is not a .pak.");
        }

        // Given system_0002.pak, still start from system.pak - the chain is what matters.
        string baseName = self.Groups["base"].Value;

        List<(int Patch, string Path)> chain = [];
        foreach (string candidate in Directory.EnumerateFiles(directory, $"{baseName}*.pak"))
        {
            Match match = PakName().Match(Path.GetFileName(candidate));
            if (!match.Success ||
                !string.Equals(match.Groups["base"].Value, baseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string patch = match.Groups["patch"].Value;
            chain.Add((patch.Length == 0 ? 0 : int.Parse(patch), candidate));
        }

        return [.. chain.OrderBy(item => item.Patch).Select(item => item.Path)];
    }

    public GameDataImportResult Import(
        string pakPath, string dataDirectory, XipKeyPair keys, bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(keys);

        IReadOnlyList<string> chain = ResolvePakChain(pakPath);
        if (chain.Count == 0)
        {
            throw new FileNotFoundException($"No pak found for {pakPath}.");
        }

        // Later paks overwrite earlier ones, so just walk the chain in order and keep the
        // last hit per file name.
        Dictionary<string, (XipArchive Archive, XipEntry Entry, string Pak)> chosen =
            new(StringComparer.OrdinalIgnoreCase);
        List<string> skipped = [];

        foreach (string pak in chain)
        {
            XipArchive archive = XipArchive.Open(pak, keys);
            string label = Path.GetFileName(pak);
            foreach (XipEntry entry in archive.Entries)
            {
                if (entry.Deleted || !IsWanted(entry))
                {
                    continue;
                }
                if (Protected.Contains(entry.FileName.ToLowerInvariant()))
                {
                    skipped.Add($"{entry.Name} (protected name)");
                    continue;
                }

                chosen[entry.FileName] = (archive, entry, label);
            }
        }

        List<ImportedFile> imported = [];
        if (!dryRun)
        {
            Directory.CreateDirectory(dataDirectory);
        }

        foreach ((string name, (XipArchive archive, XipEntry entry, string pak)) in
                 chosen.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            byte[] data;
            try
            {
                data = archive.Extract(entry);
            }
            catch (Exception ex)
            {
                skipped.Add($"{entry.Name} ({ex.Message})");
                Logger.Error($"Could not extract {entry.Name} from {pak}: {ex.Message}");
                continue;
            }

            if (!dryRun)
            {
                File.WriteAllBytes(Path.Combine(dataDirectory, name), data);
            }
            imported.Add(new ImportedFile(name, pak, entry.Name, data.Length));
        }

        return new GameDataImportResult(imported, skipped, chain);
    }

    private static bool IsWanted(XipEntry entry)
    {
        string path = entry.NormalisedName.ToLowerInvariant();
        int slash = path.LastIndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        string directory = path[..slash];
        string file = path[(slash + 1)..];
        foreach ((string wantedDirectory, string pattern) in Wanted)
        {
            if (directory == wantedDirectory && Matches(file, pattern))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Only '*' is needed here, and only ever as a suffix or whole-name wildcard.</summary>
    private static bool Matches(string name, string pattern)
    {
        int star = pattern.IndexOf('*');
        if (star < 0)
        {
            return name == pattern;
        }

        string prefix = pattern[..star];
        string suffix = pattern[(star + 1)..];
        return name.Length >= prefix.Length + suffix.Length &&
               name.StartsWith(prefix, StringComparison.Ordinal) &&
               name.EndsWith(suffix, StringComparison.Ordinal);
    }
}
