using System.Net;
using Arrowgene.DJMaxOnline.Updater;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// One small, read-only HTTP server for everything the game and the launcher download -
/// the web equivalent of <see cref="LocalFtpServer"/>. Two routes, one port, one host:
///
/// <code>
///   /song/    - song archives, what DOWNLOADURL points at. FLAT: the client asks for a
///               bare file name.
///   /patch/   - what the launcher installs, mirroring the GAME FOLDER, so crc.pak and the
///               system paks sit at its root and subfolders nest. Also carries the
///               checksum list and the news.
/// </code>
///
/// There is deliberately no third route for client paks. A client pak is a file in the
/// game folder, which is exactly what /patch/ already describes - a separate site for it
/// would be another thing to configure, host and keep in sync for no gain.
///
/// The client fetches through WinINet (InternetOpenUrlA + InternetReadFile) and probes the
/// size with HttpQueryInfo(CONTENT_LENGTH), so plain GET with a correct Content-Length is
/// all it needs. Range is honoured because the downloader also calls
/// InternetSetFilePointer, which WinINet turns into a ranged re-request.
///
/// This exists so the HTTP path can be used with no external hosting. Point
/// ContentBaseUrl at a real site and leave HttpContentEnabled off to use that instead.
/// </summary>
public sealed class LocalContentServer : IDisposable
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(LocalContentServer));

    private readonly Setting _setting;
    private readonly Dictionary<string, string> _routes;
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;

    public LocalContentServer(Setting setting)
    {
        _setting = setting ?? throw new ArgumentNullException(nameof(setting));
        _routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Segment(setting.ContentSongPath)] = setting.PakDirectory
        };
        _patchRoute = Segment(setting.ContentPatchPath);
        if (_patchRoute.Length != 0)
        {
            _routes[_patchRoute] = setting.PatchDirectory;
        }
    }

    /// <summary>
    /// The route that serves NESTED paths. /song/ is flat because the client asks for a
    /// bare file name; a launcher manifest entry is a path relative to the game folder,
    /// so the patch folder mirrors that shape instead.
    /// </summary>
    private readonly string _patchRoute;

    private static string Segment(string path) => path.Trim().Trim('/').ToLowerInvariant();

    public void Start()
    {
        if (!_setting.HttpContentEnabled || _listener != null)
        {
            return;
        }

        foreach (string directory in _routes.Values)
        {
            Directory.CreateDirectory(directory);
        }

        PublishPatchManifest();

        _listener = new HttpListener();
        // A specific host needs a URL ACL; the loopback default does not.
        _listener.Prefixes.Add(
            $"http://{Host(_setting.HttpContentListenIpAddress)}:{_setting.HttpContentPort}/");
        _cancellation = new CancellationTokenSource();

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _listener = null;
            throw new InvalidOperationException(
                $"Could not listen on port {_setting.HttpContentPort}. On Windows a " +
                "non-loopback prefix needs a URL reservation: " +
                $"netsh http add urlacl url=http://+:{_setting.HttpContentPort}/ user=Everyone",
                ex);
        }

        foreach ((string route, string directory) in _routes)
        {
            Logger.Info($"HTTP content: /{route}/ -> {directory}");
        }
        Logger.Info(
            $"HTTP content listening on {Host(_setting.HttpContentListenIpAddress)}:" +
            $"{_setting.HttpContentPort}");

        _ = Task.Run(() => AcceptAsync(_listener, _cancellation.Token));
    }

    private static string Host(IPAddress address) =>
        Equals(address, IPAddress.Any) ? "+" : address.ToString();

    /// <summary>
    /// Rebuilds the patch checksum list from what is actually on disk.
    ///
    /// This is what makes the launcher's verification meaningful: the list is generated
    /// from the bytes being served, in the same process that serves them, so a patch file
    /// replaced on disk can never be described by a stale hash. Drop in a new
    /// system_0001.pak, restart, and every launcher sees and verifies the new one.
    ///
    /// The news file is excluded deliberately - see <see cref="Setting.PatchNewsFileName"/>.
    /// A failure here is logged and does NOT stop the server: an out-of-date list is a
    /// patching problem, not a reason to refuse to run the game.
    /// </summary>
    private void PublishPatchManifest()
    {
        if (!_setting.PatchManifestAutoBuild || _patchRoute.Length == 0)
        {
            return;
        }

        string directory = _setting.PatchDirectory;
        string manifestName = string.IsNullOrWhiteSpace(_setting.PatchManifestFileName)
            ? UpdateManifest.DefaultFileName
            : _setting.PatchManifestFileName;

        try
        {
            // The song folder is served and mirrors the game folder, but is deliberately
            // NOT checksummed: the client downloads song archives itself, in game, when a
            // player picks a chart. Hashing hundreds of paks would hold up every launch to
            // verify a library nobody has downloaded yet.
            UpdateManifest manifest = UpdateManifest.Create(
                directory,
                manifestName,
                [_setting.PatchNewsFileName],
                [Segment(_setting.ContentSongPath)]);
            string path = Path.Combine(directory, manifestName);
            // Written through a temporary file: a launcher polling mid-write must never
            // read a half-finished list and conclude the game is corrupt.
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, manifest.Write());
            File.Move(temporary, path, overwrite: true);

            Logger.Info(manifest.Entries.Count == 0
                ? $"Patch manifest: no files in {Path.GetFullPath(directory)}; " +
                  $"launchers will see nothing to update."
                : $"Patch manifest: hashed {manifest.Entries.Count} file(s) into " +
                  $"/{_patchRoute}/{manifestName}.");
        }
        catch (Exception ex)
        {
            Logger.Error(
                $"Patch manifest could not be rebuilt ({ex.Message}). Serving whatever " +
                $"{manifestName} is already there - it may be out of date.");
        }
    }

    private async Task AcceptAsync(HttpListener listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"HTTP content accept failed: {ex.Message}");
                return;
            }

            _ = Task.Run(() => ServeAsync(context), cancellation);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        try
        {
            if (context.Request.HttpMethod is not ("GET" or "HEAD"))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            string? path = ResolvePath(context.Request.Url?.AbsolutePath ?? "/");
            if (path == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                Logger.Info($"HTTP content 404: {context.Request.Url?.AbsolutePath}");
                return;
            }

            FileInfo file = new(path);
            response.ContentType = "application/octet-stream";
            response.AddHeader("Accept-Ranges", "bytes");

            (long offset, long length) = ResolveRange(context.Request.Headers["Range"], file.Length);
            if (offset > 0 || length != file.Length)
            {
                response.StatusCode = (int)HttpStatusCode.PartialContent;
                response.AddHeader("Content-Range",
                    $"bytes {offset}-{offset + length - 1}/{file.Length}");
            }
            response.ContentLength64 = length;

            if (context.Request.HttpMethod == "HEAD")
            {
                return;
            }

            await using FileStream source = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read);
            source.Seek(offset, SeekOrigin.Begin);
            await CopyAsync(source, response.OutputStream, length);
            Logger.Info($"HTTP content: sent {file.Name} ({length} bytes)");
        }
        catch (HttpListenerException)
        {
            // The client hung up mid-transfer; nothing useful to do.
        }
        catch (Exception ex)
        {
            Logger.Error($"HTTP content error: {ex.Message}");
            try
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            catch (Exception)
            {
                // The response may already be committed.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // Already closed by the error path.
            }
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, long length)
    {
        byte[] buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int taken = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (taken <= 0)
            {
                return;
            }
            await destination.WriteAsync(buffer.AsMemory(0, taken));
            remaining -= taken;
        }
    }

    /// <summary>Only <c>bytes=start-</c> and <c>bytes=start-end</c> are needed here.</summary>
    private static (long Offset, long Length) ResolveRange(string? header, long size)
    {
        const string prefix = "bytes=";
        if (header == null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return (0, size);
        }

        string[] parts = header[prefix.Length..].Split('-', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out long start) ||
            start < 0 || start >= size)
        {
            return (0, size);
        }

        long end = long.TryParse(parts[1], out long parsed) && parsed >= start
            ? Math.Min(parsed, size - 1)
            : size - 1;
        return (start, end - start + 1);
    }

    /// <summary>
    /// Maps a request path onto one of the two served folders. Rejects anything that
    /// escapes its folder, and refuses nested paths - both folders are flat.
    /// </summary>
    private string? ResolvePath(string absolutePath)
    {
        string[] segments = Uri.UnescapeDataString(absolutePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !_routes.TryGetValue(segments[0], out string? directory))
        {
            return null;
        }

        bool patch = segments[0].Equals(_patchRoute, StringComparison.OrdinalIgnoreCase);
        // Only the patch route nests; /song/ stays flat, as the client requires.
        if (!patch && segments.Length != 2)
        {
            return null;
        }

        foreach (string segment in segments[1..])
        {
            if (segment.Length == 0 || segment == "." || segment == ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }
        }

        string root = Path.GetFullPath(directory);
        string candidate = Path.GetFullPath(Path.Combine([root, .. segments[1..]]));
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(candidate)
            ? candidate
            : null;
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"HTTP content stop failed: {ex.Message}");
        }
        finally
        {
            _listener = null;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public void Dispose() => Stop();
}
