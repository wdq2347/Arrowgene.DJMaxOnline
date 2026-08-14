using System.Security.Cryptography;

namespace Arrowgene.DJMaxOnline.Updater;

/// <summary>Where the update files are fetched from: a web site, or a folder for testing.</summary>
public interface IUpdateTransport
{
    /// <summary>A human-readable description of the source, for the status line.</summary>
    string Describe();

    Task<string> ReadTextAsync(string relativePath, CancellationToken cancellation);

    /// <summary>Opens the file for reading. Length is -1 when the source does not say.</summary>
    Task<(Stream Stream, long Length)> OpenReadAsync(
        string relativePath, CancellationToken cancellation);
}

/// <summary>Reads updates out of a local folder - the testing path, no web server needed.</summary>
public sealed class FolderUpdateTransport : IUpdateTransport
{
    private readonly string _root;

    public FolderUpdateTransport(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string Describe() => _root;

    public async Task<string> ReadTextAsync(string relativePath, CancellationToken cancellation) =>
        await File.ReadAllTextAsync(Resolve(relativePath), cancellation);

    public Task<(Stream Stream, long Length)> OpenReadAsync(
        string relativePath, CancellationToken cancellation)
    {
        FileInfo file = new(Resolve(relativePath));
        Stream stream = file.OpenRead();
        return Task.FromResult((stream, file.Length));
    }

    private string Resolve(string relativePath)
    {
        if (!UpdateManifest.IsSafeRelativePath(UpdateManifest.NormalisePath(relativePath)))
        {
            throw new InvalidDataException($"Unsafe update path: {relativePath}");
        }
        return Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>Reads updates from the configured web site.</summary>
public sealed class WebUpdateTransport : IUpdateTransport
{
    private readonly HttpClient _client;
    private readonly Uri _root;

    public WebUpdateTransport(Uri root, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(client);
        // A base Uri only composes correctly when it ends in a slash.
        _root = root.AbsoluteUri.EndsWith('/') ? root : new Uri(root.AbsoluteUri + "/");
        _client = client;
    }

    public string Describe() => _root.AbsoluteUri;

    public async Task<string> ReadTextAsync(string relativePath, CancellationToken cancellation)
    {
        using HttpResponseMessage response =
            await _client.GetAsync(Resolve(relativePath), cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellation);
    }

    public async Task<(Stream Stream, long Length)> OpenReadAsync(
        string relativePath, CancellationToken cancellation)
    {
        HttpResponseMessage response = await _client.GetAsync(
            Resolve(relativePath), HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStreamAsync(cancellation),
            response.Content.Headers.ContentLength ?? -1);
    }

    private Uri Resolve(string relativePath)
    {
        string normalised = UpdateManifest.NormalisePath(relativePath);
        if (!UpdateManifest.IsSafeRelativePath(normalised))
        {
            throw new InvalidDataException($"Unsafe update path: {relativePath}");
        }

        string escaped = string.Join('/',
            normalised.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_root, escaped);
    }
}

/// <summary>Why a file is in the plan - purely so the UI can say something useful.</summary>
public enum UpdateReason
{
    Missing,
    Changed
}

public sealed record UpdateAction(ManifestEntry Entry, UpdateReason Reason);

/// <summary>The outcome of a check: which files differ from what the site publishes.</summary>
public sealed record UpdatePlan(IReadOnlyList<UpdateAction> Actions, int Examined)
{
    public bool UpToDate => Actions.Count == 0;
}

/// <summary>Progress for the two bars: overall across the plan, and the current file.</summary>
public sealed record UpdateProgress(
    int FileIndex,
    int FileCount,
    string Path,
    long BytesRead,
    long BytesTotal)
{
    public float FileFraction =>
        BytesTotal > 0 ? Math.Clamp((float)BytesRead / BytesTotal, 0F, 1F) : 0F;

    public float OverallFraction =>
        FileCount <= 0 ? 0F : Math.Clamp((FileIndex + FileFraction) / FileCount, 0F, 1F);
}

/// <summary>
/// Compares the game folder against the manifest published by the update site, and
/// downloads whatever differs. Every download is written to a temporary file, verified
/// against the manifest's MD5, and only then moved into place - so an interrupted or
/// corrupted transfer can never leave a half-written game file behind.
/// </summary>
public sealed class UpdateService
{
    private readonly IUpdateTransport _transport;
    private readonly string _manifestFileName;

    public UpdateService(
        IUpdateTransport transport, string manifestFileName = UpdateManifest.DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFileName);
        _transport = transport;
        _manifestFileName = manifestFileName;
    }

    public string Source => _transport.Describe();

    /// <summary>
    /// Builds a transport from a setting that is either a URL or a local folder path, so
    /// the same field can point at the live site or at a test folder.
    /// </summary>
    public static IUpdateTransport CreateTransport(string source, HttpClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new WebUpdateTransport(uri, client)
            : new FolderUpdateTransport(source);
    }

    public async Task<UpdateManifest> FetchManifestAsync(CancellationToken cancellation) =>
        UpdateManifest.Parse(await _transport.ReadTextAsync(_manifestFileName, cancellation));

    /// <summary>
    /// Hashing every listed file is the slow part of a check, so this is expected to run
    /// off the UI thread. Files present locally but absent from the manifest are left
    /// alone - the updater only ever adds and replaces, it never deletes.
    /// </summary>
    public static UpdatePlan Plan(UpdateManifest manifest, string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        string root = Path.GetFullPath(gameDirectory);
        List<UpdateAction> actions = [];
        foreach (ManifestEntry entry in manifest.Entries)
        {
            string local = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(local))
            {
                actions.Add(new UpdateAction(entry, UpdateReason.Missing));
            }
            else if (!string.Equals(UpdateManifest.HashFile(local), entry.Md5,
                         StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(new UpdateAction(entry, UpdateReason.Changed));
            }
        }

        return new UpdatePlan(actions, manifest.Entries.Count);
    }

    public async Task ApplyAsync(
        UpdatePlan plan,
        string gameDirectory,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        string root = Path.GetFullPath(gameDirectory);
        for (int index = 0; index < plan.Actions.Count; index++)
        {
            ManifestEntry entry = plan.Actions[index].Entry;
            cancellation.ThrowIfCancellationRequested();
            await DownloadAsync(entry, root, index, plan.Actions.Count, progress, cancellation);
        }
    }

    private async Task DownloadAsync(
        ManifestEntry entry,
        string root,
        int index,
        int count,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellation)
    {
        string target = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        string full = Path.GetFullPath(target);
        // Belt and braces: the path was validated when the manifest was parsed, but the
        // resolved path must also still sit under the game folder.
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Update path escapes the game folder: {entry.Path}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".part";

        (Stream source, long length) = await _transport.OpenReadAsync(entry.Path, cancellation);
        try
        {
            await using (source)
            await using (FileStream destination = new(temporary, FileMode.Create, FileAccess.Write,
                             FileShare.None))
            using (MD5 md5 = MD5.Create())
            {
                byte[] buffer = new byte[81920];
                long read = 0;
                int taken;
                progress?.Report(new UpdateProgress(index, count, entry.Path, 0, length));
                while ((taken = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    md5.TransformBlock(buffer, 0, taken, null, 0);
                    await destination.WriteAsync(buffer.AsMemory(0, taken), cancellation);
                    read += taken;
                    progress?.Report(new UpdateProgress(index, count, entry.Path, read, length));
                }

                md5.TransformFinalBlock([], 0, 0);
                string actual = Convert.ToHexString(md5.Hash!).ToLowerInvariant();
                if (actual != entry.Md5)
                {
                    throw new InvalidDataException(
                        $"{entry.Path} failed its checksum (expected {entry.Md5}, got {actual}).");
                }
            }

            File.Move(temporary, full, overwrite: true);
            progress?.Report(new UpdateProgress(index + 1, count, entry.Path, 1, 1));
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A partial file left behind is harmless - the next run overwrites it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
