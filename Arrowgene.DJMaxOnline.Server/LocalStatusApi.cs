using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server;

/// <summary>
/// A small read-only JSON API describing live server state, for local tooling such as the
/// Discord bot.
///
/// THIS IS A LOCAL, PRIVATE API. It binds to loopback by default and is not designed to
/// face the internet: there is no TLS, no rate limiting and no user authentication beyond
/// an optional shared token. Put it behind a reverse proxy if it ever has to leave the
/// machine.
///
/// What it will never publish, by construction: account ids, secondary ids, passwords or
/// password hashes, login tickets, session tokens, IP addresses, or anything else that
/// identifies a person rather than a player. See <see cref="ServerStatus"/> - the API can
/// only serialise those records, so widening it takes a deliberate edit there.
///
/// Endpoints (all GET):
///   /status          - counts plus every channel, its rooms and its players
///   /rooms           - just the rooms, flattened across channels
///   /players         - just the players, flattened across channels
///   /scores/recent   - finished runs after ?after=N (the Discord feed reads this)
///   /scores/player   - a player's runs by ?nickname=
///   /scores/song     - best run per player for ?song=N, optional ?keys=
///   /matches/recent  - finished multiplayer matches after ?after=N, with placements
///   /charts/issues   - wrong-difficulty chart serves after ?after=N (missing charts)
///   /health          - liveness, no game data at all
///
/// There is deliberately NO account endpoint. Sign-up, linking and password changes are
/// not exposed here and never will be - that work goes straight to the database, where
/// the credential hashing lives.
/// </summary>
public sealed class LocalStatusApi : IDisposable
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(LocalStatusApi));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly Setting _setting;
    private readonly Func<IReadOnlyList<ChannelStatus>> _channels;
    private readonly ScoreFeedQueries? _scores;
    private readonly MatchHistory? _matches;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;

    public LocalStatusApi(
        Setting setting,
        Func<IReadOnlyList<ChannelStatus>> channels,
        ScoreFeedQueries? scores = null,
        MatchHistory? matches = null)
    {
        _setting = setting ?? throw new ArgumentNullException(nameof(setting));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        // Null when the server runs without a database (legacy single-player mode); the
        // score endpoints then report empty rather than failing.
        _scores = scores;
        // Null leaves /matches/recent answering an empty list rather than 404, so a poller
        // does not have to special-case a server that is not capturing matches.
        _matches = matches;
    }

    public void Start()
    {
        if (!_setting.StatusApiEnabled || _listener != null)
        {
            return;
        }

        string host = Equals(_setting.StatusApiListenIpAddress, IPAddress.Any)
            ? "+"
            : _setting.StatusApiListenIpAddress.ToString();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{host}:{_setting.StatusApiPort}/");
        _cancellation = new CancellationTokenSource();

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _listener = null;
            throw new InvalidOperationException(
                $"Status API could not bind port {_setting.StatusApiPort}. A non-loopback " +
                "prefix needs a URL reservation: netsh http add urlacl " +
                $"url=http://+:{_setting.StatusApiPort}/ user=Everyone",
                ex);
        }

        if (host == "+" || !IPAddress.IsLoopback(_setting.StatusApiListenIpAddress))
        {
            Logger.Error(
                $"Status API is bound to {host} - it is a LOCAL, UNAUTHENTICATED API and " +
                "should not be reachable from the internet. Bind it to 127.0.0.1 unless " +
                "it sits behind a proxy you control.");
        }

        Logger.Info(
            $"Status API listening on http://{host}:{_setting.StatusApiPort}/ " +
            $"(token {(string.IsNullOrEmpty(_setting.StatusApiToken) ? "not set" : "required")}).");

        _ = Task.Run(() => AcceptAsync(_listener, _cancellation.Token));
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
                Logger.Error($"Status API accept failed: {ex.Message}");
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
            if (context.Request.HttpMethod != "GET")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (!IsAuthorised(context.Request))
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await WriteAsync(response, new { error = "A status token is required." });
                return;
            }

            string path = (context.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
            switch (path)
            {
                case "":
                case "/health":
                    await WriteAsync(response, new
                    {
                        online = true,
                        startedUtc = _startedUtc.ToString("O"),
                    });
                    return;
                case "/status":
                    await WriteAsync(response, BuildStatus());
                    return;
                case "/rooms":
                    await WriteAsync(response, new
                    {
                        rooms = _channels()
                            .SelectMany(channel => channel.RoomList
                                .Select(room => new { channel = channel.Name, room }))
                            .ToArray()
                    });
                    return;
                case "/players":
                    await WriteAsync(response, new
                    {
                        players = _channels()
                            .SelectMany(channel => channel.PlayerList
                                .Select(player => new { channel = channel.Name, player }))
                            .ToArray()
                    });
                    return;
                case "/scores/recent":
                    await WriteAsync(response, new
                    {
                        latestScoreId = _scores?.LatestScoreId() ?? 0,
                        scores = _scores?.Recent(
                            Query(context.Request, "after", 0),
                            (int)Query(context.Request, "limit", 25)) ?? []
                    });
                    return;
                case "/scores/player":
                    await WriteAsync(response, new
                    {
                        scores = _scores?.ForPlayer(
                            context.Request.QueryString["nickname"] ?? string.Empty,
                            (int)Query(context.Request, "limit", 10)) ?? []
                    });
                    return;
                case "/scores/song":
                    await WriteAsync(response, new
                    {
                        scores = _scores?.ForSong(
                            (uint)Query(context.Request, "song", 0),
                            (byte)Query(context.Request, "keys", 0),
                            (int)Query(context.Request, "limit", 10)) ?? []
                    });
                    return;
                case "/charts/issues":
                    await WriteAsync(response, new
                    {
                        latestIssueId = ChartServeIssues.LatestIssueId,
                        issues = ChartServeIssues.Recent(
                            Query(context.Request, "after", 0),
                            (int)Query(context.Request, "limit", 25))
                    });
                    return;
                case "/matches/recent":
                    await WriteAsync(response, new
                    {
                        latestMatchId = _matches?.LatestMatchId ?? 0,
                        matches = _matches?.Recent(
                            Query(context.Request, "after", 0),
                            (int)Query(context.Request, "limit", 25)) ?? []
                    });
                    return;
                default:
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteAsync(response, new { error = "Unknown endpoint." });
                    return;
            }
        }
        catch (HttpListenerException)
        {
            // Caller hung up.
        }
        catch (Exception ex)
        {
            Logger.Error($"Status API error: {ex.Message}");
            try
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            catch (Exception)
            {
                // Response already committed.
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
                // Already closed.
            }
        }
    }

    /// <summary>
    /// Optional shared token. Compared in constant time so the endpoint cannot be used as
    /// an oracle, and only checked when the operator configured one.
    /// </summary>
    private bool IsAuthorised(HttpListenerRequest request)
    {
        string expected = _setting.StatusApiToken ?? string.Empty;
        if (expected.Length == 0)
        {
            return true;
        }

        string supplied = request.Headers["X-Status-Token"] ?? string.Empty;
        byte[] a = Encoding.UTF8.GetBytes(expected);
        byte[] b = Encoding.UTF8.GetBytes(supplied);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            a, b.Length == a.Length ? b : new byte[a.Length]);
    }

    /// <summary>Query values are caller-supplied; anything unparseable falls back.</summary>
    private static long Query(HttpListenerRequest request, string name, long fallback) =>
        long.TryParse(request.QueryString[name], out long value) && value >= 0
            ? value
            : fallback;

    private ServerStatus BuildStatus()
    {
        IReadOnlyList<ChannelStatus> channels = _channels();
        return new ServerStatus(
            Online: true,
            StartedUtc: _startedUtc.ToString("O"),
            Players: channels.Sum(channel => channel.Players),
            Playing: channels.Sum(channel => channel.Playing),
            Rooms: channels.Sum(channel => channel.Rooms),
            Channels: channels);
    }

    private static async Task WriteAsync(HttpListenerResponse response, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
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
            Logger.Error($"Status API stop failed: {ex.Message}");
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
