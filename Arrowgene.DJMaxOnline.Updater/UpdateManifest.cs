using System.Security.Cryptography;
using System.Text;

namespace Arrowgene.DJMaxOnline.Updater;

/// <summary>One published file: its path relative to the game folder and its MD5.</summary>
/// <remarks>
/// MD5 is an integrity check here, not a security control - it catches truncated and
/// stale downloads, but it is not collision resistant, so a manifest served over plain
/// HTTP can be tampered with. Serve the manifest over HTTPS if that matters.
/// </remarks>
public sealed record ManifestEntry(string Path, string Md5)
{
    public string Path { get; } = Path;

    /// <summary>Always lower-case hex, so comparisons never have to care about casing.</summary>
    public string Md5 { get; } = Md5.ToLowerInvariant();
}

/// <summary>
/// The <c>md5list.txt</c> that sits next to the downloadable files on the update site.
/// The format is deliberately the same shape as <c>md5sum</c> output:
/// <code>
/// # comment
/// d41d8cd98f00b204e9800998ecf8427e  Song/example.pak
/// d41d8cd98f00b204e9800998ecf8427e *System/example.pak
/// </code>
/// The hash is the first token; everything after the whitespace is the path, so paths
/// may contain spaces. A leading <c>*</c> (md5sum's binary marker) is ignored.
/// </summary>
public sealed class UpdateManifest
{
    public const string DefaultFileName = "md5list.txt";

    /// <summary>
    /// Files that live in the update folder for the operator's benefit and must never be
    /// listed - because everything a manifest names gets written into the player's game
    /// folder. The README documenting the folder, and the junk Windows and macOS leave
    /// lying around, are not game files.
    /// </summary>
    private static readonly HashSet<string> SiteOnlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "readme.md", "readme.txt", "thumbs.db", "desktop.ini", ".ds_store"
    };

    private UpdateManifest(IReadOnlyList<ManifestEntry> entries) => Entries = entries;

    public IReadOnlyList<ManifestEntry> Entries { get; }

    public static UpdateManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<ManifestEntry> entries = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int number = 0;
        foreach (string raw in text.Split('\n'))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            int split = line.IndexOfAny([' ', '\t']);
            if (split <= 0)
            {
                throw new InvalidDataException($"Line {number}: expected '<md5> <path>'.");
            }

            string md5 = line[..split];
            if (!IsMd5(md5))
            {
                throw new InvalidDataException($"Line {number}: '{md5}' is not an MD5 hash.");
            }

            string path = line[(split + 1)..].TrimStart();
            if (path.StartsWith('*'))
            {
                path = path[1..];   // md5sum's binary marker
            }

            path = NormalisePath(path);
            if (!IsSafeRelativePath(path))
            {
                throw new InvalidDataException(
                    $"Line {number}: '{path}' is not a safe relative path.");
            }
            if (!seen.Add(path))
            {
                throw new InvalidDataException($"Line {number}: '{path}' is listed twice.");
            }

            entries.Add(new ManifestEntry(path, md5));
        }

        return new UpdateManifest(entries);
    }

    public static UpdateManifest FromEntries(IEnumerable<ManifestEntry> entries) =>
        new([.. entries]);

    /// <summary>Renders the manifest back out, ready to upload beside the files.</summary>
    public string Write()
    {
        StringBuilder builder = new();
        builder.AppendLine("# DJMAX Online update manifest");
        builder.AppendLine("# <md5>  <path relative to the game folder>");
        foreach (ManifestEntry entry in Entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            builder.Append(entry.Md5).Append("  ").AppendLine(entry.Path);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Hashes every file under <paramref name="directory"/> to build a manifest for it.
    /// The manifest file itself is skipped so it never lists its own stale hash.
    /// </summary>
    /// <param name="exclude">
    /// Extra paths to leave out, relative to <paramref name="directory"/>. Anything listed
    /// in a manifest gets DOWNLOADED INTO THE GAME FOLDER, so site-only files that merely
    /// live beside the patches - the news file above all - must be excluded here or every
    /// player ends up with a copy of them.
    /// </param>
    /// <param name="excludeFolders">
    /// Folders to leave out entirely, matched against the first path segment. The song
    /// folder goes here: song archives live in the game folder like everything else, but
    /// the CLIENT downloads them itself, on demand, when a player picks a chart. Listing
    /// them would make the launcher hash and re-download the whole music library before
    /// anyone could log in.
    /// </param>
    public static UpdateManifest Create(
        string directory,
        string manifestFileName = DefaultFileName,
        IEnumerable<string>? exclude = null,
        IEnumerable<string>? excludeFolders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        HashSet<string> skip = new(StringComparer.OrdinalIgnoreCase) { manifestFileName };
        foreach (string name in exclude ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                skip.Add(NormalisePath(name));
            }
        }

        HashSet<string> skipFolders = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in excludeFolders ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                skipFolders.Add(NormalisePath(name).Trim('/'));
            }
        }

        string root = System.IO.Path.GetFullPath(directory);
        List<ManifestEntry> entries = [];
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = NormalisePath(System.IO.Path.GetRelativePath(root, file));
            if (skip.Contains(relative) || IsSiteOnly(relative) ||
                IsInExcludedFolder(relative, skipFolders))
            {
                continue;
            }

            entries.Add(new ManifestEntry(relative, HashFile(file)));
        }

        return new UpdateManifest([.. entries.OrderBy(e => e.Path, StringComparer.Ordinal)]);
    }

    private static bool IsInExcludedFolder(string relativePath, HashSet<string> folders)
    {
        if (folders.Count == 0)
        {
            return false;
        }

        int separator = relativePath.IndexOf('/');
        return separator > 0 && folders.Contains(relativePath[..separator]);
    }

    /// <summary>
    /// Site bookkeeping rather than a game file: a README, an OS metadata file, a dotfile,
    /// or a half-finished download. None of these belong in a player's install.
    /// </summary>
    private static bool IsSiteOnly(string relativePath)
    {
        string name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return SiteOnlyNames.Contains(name) ||
               name.StartsWith('.') ||
               name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
    }

    public static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Manifests always use forward slashes, whatever platform wrote them. Leading
    /// separators are deliberately left alone: stripping them would quietly turn
    /// "/etc/passwd" and "\\server\share\x" into paths that pass
    /// <see cref="IsSafeRelativePath"/>, which is exactly the case it exists to catch.
    /// </summary>
    public static string NormalisePath(string path) => path.Replace('\\', '/').Trim();

    /// <summary>
    /// Guards against a manifest writing outside the game folder - the one thing a
    /// downloader must never allow, since the manifest comes off the network.
    /// </summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains(':') ||
            path.StartsWith('/') ||
            path.StartsWith('\\') ||
            System.IO.Path.IsPathRooted(path))
        {
            return false;
        }

        if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        string[] segments = path.Split('/', StringSplitOptions.None);
        return segments.Length != 0 &&
               segments.All(segment =>
                   segment.Length != 0 && segment != "." && segment != "..");
    }

    private static bool IsMd5(string value) =>
        value.Length == 32 && value.All(Uri.IsHexDigit);
}
