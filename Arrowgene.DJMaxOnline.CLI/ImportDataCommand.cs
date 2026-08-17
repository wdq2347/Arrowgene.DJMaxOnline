using Arrowgene.DJMaxOnline.Server.Korea400.Pak;

namespace Arrowgene.DJMaxOnline;

/// <summary>
/// <c>--import-data &lt;system.pak&gt; [--data DIR] [--keys DIR] [--dry-run]</c>
///
/// Extracts the catalogs the server reads out of the client's system pak into DATA. Patch
/// paks (<c>system_0001.pak</c>, <c>system_0002.pak</c>, ...) are layered over the base in
/// numeric order exactly as the client layers them, so the newest copy of a file wins.
/// </summary>
public partial class Program
{
    public void RunImportData(IReadOnlyList<string> args)
    {
        string pakPath = Path.GetFullPath(args[1]);
        if (!File.Exists(pakPath))
        {
            Console.Error.WriteLine($"{pakPath} does not exist.");
            return;
        }

        string dataDirectory = Path.GetFullPath(
            ResolveOption(args, "--data") ?? ResolveDataDirectory());
        string? keyDirectory = ResolveOption(args, "--keys") ?? ResolveKeyDirectory(dataDirectory);
        bool dryRun = args.Any(argument =>
            string.Equals(argument, "--dry-run", StringComparison.OrdinalIgnoreCase));

        if (keyDirectory == null || !XipKeyPair.Available(keyDirectory))
        {
            // The key tables are client data and are not shipped with the server, so an
            // import can only run once you have supplied your own copies.
            Console.Error.WriteLine(
                $"Pak keys not found. Put {XipKeyPair.ModulusFileName}, " +
                $"{XipKeyPair.ExponentFileName} and {XipKeyPair.XorFileName} in " +
                "DATA/xipkeys (or pass --keys DIR pointing at the folder holding them).");
            return;
        }

        GameDataImportResult result;
        try
        {
            result = new GameDataImporter().Import(
                pakPath, dataDirectory, XipKeyPair.Load(keyDirectory), dryRun);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Import failed: {ex.Message}");
            return;
        }

        // Straight to stdout, not through LogProvider: the logger drains on a background
        // thread and a one-shot command exits before the queue is flushed, so a report
        // written through it arrives truncated.
        Console.WriteLine(
            $"Pak chain ({result.Paks.Count}): " +
            string.Join(" -> ", result.Paks.Select(Path.GetFileName)));

        foreach (ImportedFile file in result.Imported)
        {
            Console.WriteLine($"  {file.FileName,-34} {file.Bytes,8} B  <- {file.SourcePak}");
        }

        foreach (string skipped in result.Skipped)
        {
            Console.WriteLine($"  skipped: {skipped}");
        }

        // Anything overridden by a patch pak is worth calling out - it is the usual reason
        // a catalog stops matching the client after an update.
        string baseName = Path.GetFileName(result.Paks[0]);
        foreach (IGrouping<string, ImportedFile> group in result.Imported
                     .GroupBy(file => file.SourcePak)
                     .Where(group => group.Key != baseName)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"From patch {group.Key}: " +
                string.Join(", ", group.Select(file => file.FileName)));
        }

        Console.WriteLine(
            $"{(dryRun ? "Would import" : "Imported")} {result.Imported.Count} file(s) " +
            $"into {dataDirectory}" +
            (result.Skipped.Count > 0 ? $"; {result.Skipped.Count} skipped." : "."));
    }

    private static string ResolveDataDirectory()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory != null;
                 directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "DATA");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "DATA");
    }

    /// <summary>
    /// The chosen DATA folder first, then DATA/xipkeys anywhere above the working
    /// directory. The walk is anchored to the working directory rather than to DATA,
    /// because --data may point anywhere.
    /// </summary>
    private static string? ResolveKeyDirectory(string dataDirectory)
    {
        string chosen = Path.Combine(dataDirectory, "xipkeys");
        if (XipKeyPair.Available(chosen))
        {
            return chosen;
        }

        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory != null;
                 directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "DATA", "xipkeys");
                if (XipKeyPair.Available(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
