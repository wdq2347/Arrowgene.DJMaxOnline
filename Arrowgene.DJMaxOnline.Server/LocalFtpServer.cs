using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server;

/// <summary>
/// Small, read-only FTP server for the chart downloader embedded in DJMax.
/// It intentionally implements both passive and active transfers because the
/// original WinINet client can use either depending on its Internet settings.
/// </summary>
public sealed class LocalFtpServer : IDisposable
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(LocalFtpServer));

    private readonly Setting _setting;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients;
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _acceptTask;

    public LocalFtpServer(Setting setting)
    {
        _setting = setting;
        _clients = new ConcurrentDictionary<TcpClient, byte>();
    }

    public void Start()
    {
        if (!_setting.FtpEnabled || _listener != null)
        {
            return;
        }

        Directory.CreateDirectory(_setting.FtpRootDirectory);
        _cancellation = new CancellationTokenSource();
        _listener = new TcpListener(_setting.FtpListenIpAddress, _setting.FtpPort);
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_listener, _cancellation.Token);
        Logger.Info(
            $"FTP listening on {_setting.FtpListenIpAddress}:{_setting.FtpPort}; " +
            $"root {_setting.FtpRootDirectory}");
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation = _cancellation;
        TcpListener? listener = _listener;
        _cancellation = null;
        _listener = null;

        cancellation?.Cancel();
        listener?.Stop();
        foreach (TcpClient client in _clients.Keys)
        {
            client.Dispose();
        }

        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.All(inner =>
                inner is OperationCanceledException || inner is SocketException))
        {
        }

        _acceptTask = null;
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task AcceptLoopAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                _clients.TryAdd(client, 0);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Exception(ex);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        TcpListener? passiveListener = null;
        IPEndPoint? activeEndpoint = null;
        string? suppliedUsername = null;
        bool authenticated = false;
        string currentDirectory = "/";
        long restartOffset = 0;

        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new(
                       stream, Encoding.ASCII, false, 1024, leaveOpen: true))
            using (StreamWriter writer = new(
                       stream, Encoding.ASCII, 1024, leaveOpen: true)
                   {
                       NewLine = "\r\n",
                       AutoFlush = true
                   })
            {
                await ReplyAsync(writer, "220 DJMAX local chart FTP ready");
                Logger.Info($"FTP connected: {remote}");

                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                    {
                        break;
                    }

                    int separator = line.IndexOf(' ');
                    string command = (separator < 0 ? line : line[..separator])
                        .Trim()
                        .ToUpperInvariant();
                    string argument = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
                    Logger.Info(
                        $"FTP {remote}: {command}" +
                        (string.IsNullOrEmpty(argument)
                            ? string.Empty
                            : command == "PASS" ? " ********" : $" {argument}"));

                    if (!authenticated && command is not ("USER" or "PASS" or "QUIT" or "AUTH"))
                    {
                        await ReplyAsync(writer, "530 Please login with USER and PASS");
                        continue;
                    }

                    switch (command)
                    {
                        case "USER":
                            suppliedUsername = argument;
                            authenticated = false;
                            await ReplyAsync(writer,
                                string.Equals(
                                    suppliedUsername,
                                    _setting.FtpUsername,
                                    StringComparison.OrdinalIgnoreCase)
                                    ? "331 Password required"
                                    : "530 Invalid username");
                            break;

                        case "PASS":
                            authenticated =
                                string.Equals(
                                    suppliedUsername,
                                    _setting.FtpUsername,
                                    StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(
                                    argument,
                                    _setting.FtpPassword,
                                    StringComparison.Ordinal);
                            await ReplyAsync(writer,
                                authenticated
                                    ? "230 Login successful"
                                    : "530 Login incorrect");
                            break;

                        case "AUTH":
                            await ReplyAsync(writer, "502 TLS is not enabled on this local server");
                            break;

                        case "SYST":
                            await ReplyAsync(writer, "215 UNIX Type: L8");
                            break;

                        case "FEAT":
                            await writer.WriteLineAsync("211-Features");
                            await writer.WriteLineAsync(" EPSV");
                            await writer.WriteLineAsync(" MDTM");
                            await writer.WriteLineAsync(" SIZE");
                            await writer.WriteLineAsync(" UTF8");
                            await writer.WriteLineAsync("211 End");
                            break;

                        case "OPTS":
                            await ReplyAsync(writer, "200 Option accepted");
                            break;

                        case "TYPE":
                        case "MODE":
                        case "STRU":
                            await ReplyAsync(writer, "200 Command accepted");
                            break;

                        case "NOOP":
                            await ReplyAsync(writer, "200 OK");
                            break;

                        case "PWD":
                        case "XPWD":
                            await ReplyAsync(writer, $"257 \"{currentDirectory}\"");
                            break;

                        case "CWD":
                        case "XCWD":
                            currentDirectory = NormalizeFtpPath(currentDirectory, argument);
                            await ReplyAsync(writer, "250 Directory changed");
                            break;

                        case "CDUP":
                        case "XCUP":
                            currentDirectory = NormalizeFtpPath(currentDirectory, "..");
                            await ReplyAsync(writer, "250 Directory changed");
                            break;

                        case "PASV":
                            passiveListener?.Stop();
                            passiveListener = CreatePassiveListener();
                            activeEndpoint = null;
                            IPEndPoint passiveEndpoint =
                                (IPEndPoint)passiveListener.LocalEndpoint;
                            byte[] address = _setting.FtpAdvertisedIpAddress.GetAddressBytes();
                            if (address.Length != 4)
                            {
                                address = IPAddress.Loopback.GetAddressBytes();
                            }

                            await ReplyAsync(
                                writer,
                                $"227 Entering Passive Mode ({string.Join(',', address)},{passiveEndpoint.Port / 256},{passiveEndpoint.Port % 256})");
                            break;

                        case "EPSV":
                            passiveListener?.Stop();
                            passiveListener = CreatePassiveListener();
                            activeEndpoint = null;
                            int passivePort =
                                ((IPEndPoint)passiveListener.LocalEndpoint).Port;
                            await ReplyAsync(
                                writer,
                                $"229 Entering Extended Passive Mode (|||{passivePort}|)");
                            break;

                        case "PORT":
                            if (!TryParsePort(argument, client, out activeEndpoint))
                            {
                                await ReplyAsync(writer, "501 Invalid PORT command");
                                break;
                            }

                            passiveListener?.Stop();
                            passiveListener = null;
                            await ReplyAsync(writer, "200 PORT command successful");
                            break;

                        case "SIZE":
                        {
                            string? file = ResolveRequestedFile(
                                currentDirectory, argument, allowFallback: true);
                            await ReplyAsync(writer,
                                file != null
                                    ? $"213 {new FileInfo(file).Length}"
                                    : "550 File unavailable");
                            break;
                        }

                        case "MDTM":
                        {
                            string? file = ResolveRequestedFile(
                                currentDirectory, argument, allowFallback: true);
                            await ReplyAsync(writer,
                                file != null
                                    ? $"213 {File.GetLastWriteTimeUtc(file):yyyyMMddHHmmss}"
                                    : "550 File unavailable");
                            break;
                        }

                        case "REST":
                            if (long.TryParse(
                                    argument,
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    out long offset) &&
                                offset >= 0)
                            {
                                restartOffset = offset;
                                await ReplyAsync(writer, "350 Restart position accepted");
                            }
                            else
                            {
                                await ReplyAsync(writer, "501 Invalid restart position");
                            }

                            break;

                        case "RETR":
                        {
                            string? file = ResolveRequestedFile(
                                currentDirectory, argument, allowFallback: true);
                            if (file == null)
                            {
                                await ReplyAsync(writer, "550 File unavailable");
                                break;
                            }

                            await ReplyAsync(writer, "150 Opening binary data connection");
                            using TcpClient? dataClient = await OpenDataClientAsync(
                                passiveListener,
                                activeEndpoint,
                                cancellationToken);
                            passiveListener = null;
                            activeEndpoint = null;
                            if (dataClient == null)
                            {
                                await ReplyAsync(writer, "425 Cannot open data connection");
                                break;
                            }

                            await using (FileStream input = File.OpenRead(file))
                            await using (NetworkStream output = dataClient.GetStream())
                            {
                                if (restartOffset > input.Length)
                                {
                                    restartOffset = input.Length;
                                }

                                input.Position = restartOffset;
                                restartOffset = 0;
                                await input.CopyToAsync(output, cancellationToken);
                            }

                            Logger.Info($"FTP {remote}: sent {file}");
                            await ReplyAsync(writer, "226 Transfer complete");
                            break;
                        }

                        case "LIST":
                        case "NLST":
                            await ReplyAsync(writer, "150 Opening ASCII data connection");
                            using (TcpClient? dataClient = await OpenDataClientAsync(
                                       passiveListener,
                                       activeEndpoint,
                                       cancellationToken))
                            {
                                passiveListener = null;
                                activeEndpoint = null;
                                if (dataClient == null)
                                {
                                    await ReplyAsync(writer, "425 Cannot open data connection");
                                    break;
                                }

                                await WriteDirectoryListingAsync(
                                    dataClient,
                                    currentDirectory,
                                    command == "NLST",
                                    cancellationToken);
                            }

                            await ReplyAsync(writer, "226 Transfer complete");
                            break;

                        case "ABOR":
                            passiveListener?.Stop();
                            passiveListener = null;
                            activeEndpoint = null;
                            await ReplyAsync(writer, "226 Abort successful");
                            break;

                        case "QUIT":
                            await ReplyAsync(writer, "221 Goodbye");
                            return;

                        default:
                            await ReplyAsync(writer, "502 Command not implemented");
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            Logger.Error($"FTP {remote}: {ex.Message}");
        }
        catch (SocketException ex)
        {
            Logger.Error($"FTP {remote}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Exception(ex);
        }
        finally
        {
            passiveListener?.Stop();
            _clients.TryRemove(client, out _);
            Logger.Info($"FTP disconnected: {remote}");
        }
    }

    private TcpListener CreatePassiveListener()
    {
        TcpListener listener = new(_setting.FtpListenIpAddress, 0);
        listener.Start(1);
        return listener;
    }

    private static async Task<TcpClient?> OpenDataClientAsync(
        TcpListener? passiveListener,
        IPEndPoint? activeEndpoint,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (passiveListener != null)
            {
                try
                {
                    return await passiveListener.AcceptTcpClientAsync(timeout.Token);
                }
                finally
                {
                    passiveListener.Stop();
                }
            }

            if (activeEndpoint != null)
            {
                TcpClient client = new(activeEndpoint.AddressFamily);
                await client.ConnectAsync(
                    activeEndpoint.Address,
                    activeEndpoint.Port,
                    timeout.Token);
                return client;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }

        return null;
    }

    private static bool TryParsePort(
        string argument,
        TcpClient controlClient,
        out IPEndPoint? endpoint)
    {
        endpoint = null;
        string[] parts = argument.Split(',');
        if (parts.Length != 6 ||
            !byte.TryParse(parts[4], out byte high) ||
            !byte.TryParse(parts[5], out byte low))
        {
            return false;
        }

        int port = high * 256 + low;
        if (port == 0 ||
            controlClient.Client.RemoteEndPoint is not IPEndPoint remote)
        {
            return false;
        }

        // Always use the control peer's address to prevent FTP bounce requests.
        endpoint = new IPEndPoint(remote.Address, port);
        return true;
    }

    private string? ResolveRequestedFile(
        string currentDirectory,
        string argument,
        bool allowFallback)
    {
        string ftpPath = NormalizeFtpPath(currentDirectory, argument);
        string relative = ftpPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string root = Path.GetFullPath(_setting.FtpRootDirectory);

        string? direct = ResolveContainedPath(root, relative);
        if (direct != null && File.Exists(direct))
        {
            return direct;
        }

        // Extracted song archives commonly have this layout:
        //   <root>/<tag>.pak/song/<tag>/SND/file.pt
        string[] segments = ftpPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The client builds package URLs by appending a manifest filename to
        // DownloadURL, so a base URL ending in /song/ requests paths such as
        // /song/baram.pak. Those packed archives live in PakDirectory; expose them
        // through the virtual /song directory while retaining FtpRootDirectory for
        // extracted chart paths. PatternsDirectory is charts only and is NOT searched
        // here - keeping the two apart is what stops a stale duplicate pak sitting next
        // to the charts from being served instead of the real one.
        if (segments.Length == 2 &&
            string.Equals(segments[0], "song", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                Path.GetExtension(segments[1]),
                ".pak",
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_setting.PakDirectory))
        {
            string packageRoot = Path.GetFullPath(_setting.PakDirectory);
            string? packagePath = ResolveContainedPath(packageRoot, segments[1]);
            if (packagePath != null && File.Exists(packagePath))
            {
                Logger.Info($"FTP song package mapping: {ftpPath} -> {packagePath}");
                return packagePath;
            }
        }

        if (segments.Length >= 2 &&
            string.Equals(segments[0], "song", StringComparison.OrdinalIgnoreCase))
        {
            string tag = segments[1];
            string mainArchive = $"{tag}.pak";
            string patchPrefix = $"{tag}_";
            string[] archives = Directory
                .EnumerateDirectories(root)
                .Where(path =>
                {
                    string name = Path.GetFileName(path);
                    return string.Equals(
                               name, mainArchive, StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith(
                               patchPrefix, StringComparison.OrdinalIgnoreCase) &&
                           name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(path =>
                    string.Equals(
                        Path.GetFileName(path),
                        mainArchive,
                        StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : 0)
                .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string songRelative = string.Join(Path.DirectorySeparatorChar, segments);
            foreach (string archive in archives)
            {
                string? archivePath = ResolveContainedPath(
                    root, Path.Combine(archive, songRelative));
                if (archivePath != null && File.Exists(archivePath))
                {
                    Logger.Info($"FTP package mapping: {ftpPath} -> {archivePath}");
                    return archivePath;
                }
            }
        }

        if (allowFallback &&
            string.Equals(Path.GetExtension(ftpPath), ".pt", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_setting.FtpFallbackChartPath))
        {
            string fallback = Path.GetFullPath(_setting.FtpFallbackChartPath);
            if (File.Exists(fallback))
            {
                Logger.Info($"FTP fallback: {ftpPath} -> {fallback}");
                return fallback;
            }
        }

        Logger.Error($"FTP file not found: {ftpPath}");
        return null;
    }

    private async Task WriteDirectoryListingAsync(
        TcpClient dataClient,
        string currentDirectory,
        bool namesOnly,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(_setting.FtpRootDirectory);
        string relative = currentDirectory.TrimStart('/')
            .Replace('/', Path.DirectorySeparatorChar);
        string? directory = ResolveContainedPath(root, relative);

        await using NetworkStream stream = dataClient.GetStream();
        await using StreamWriter writer = new(stream, Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        if (directory == null || !Directory.Exists(directory))
        {
            return;
        }

        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (namesOnly)
            {
                await writer.WriteLineAsync(entry.Name);
                continue;
            }

            bool isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
            long length = entry is FileInfo file ? file.Length : 0;
            await writer.WriteLineAsync(
                $"{(isDirectory ? 'd' : '-') }r--r--r-- 1 DJMAX DJMAX {length,12} " +
                $"{entry.LastWriteTime:MMM dd HH:mm} {entry.Name}");
        }
    }

    private static string? ResolveContainedPath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    private static string NormalizeFtpPath(string currentDirectory, string path)
    {
        path = Uri.UnescapeDataString(path.Replace('\\', '/'));
        string combined = path.StartsWith('/')
            ? path
            : $"{currentDirectory.TrimEnd('/')}/{path}";
        List<string> segments = new();
        foreach (string segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static Task ReplyAsync(StreamWriter writer, string response)
    {
        return writer.WriteLineAsync(response);
    }
}
