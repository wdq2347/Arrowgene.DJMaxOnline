using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// Launcher login over HTTP, for players whose launcher is not on this machine.
///
/// One endpoint:
/// <code>
///   POST /login   {"accountId":"...","password":"..."}
///      -> 200 {"success":true,"ticket":"...","expiresInSeconds":60}
///      -> 200 {"success":false,"error":"..."}          (a wrong password is not an
///                                                       HTTP error; it is an answer)
/// </code>
///
/// It does no authentication of its own: the request goes straight to
/// <see cref="LocalLoginServer.Authenticate"/>, the same method the named pipe uses. That
/// keeps the length limits, the per-account rate limit, the dummy verification on a miss
/// and the one-use 60-second ticket identical on both routes - there is no second, weaker
/// way into an account.
///
/// THIS HAS NO TLS. It is built to sit behind something that provides it:
///   * a Cloudflare Tunnel - no inbound port at all, and the origin needs no public IP;
///   * a proxied hostname on Full (strict), so Cloudflare-to-origin is encrypted too;
///   * or your own reverse proxy.
/// Bound to a public interface with nothing in front, it would carry account passwords in
/// the clear, which is why it is off by default and warns when it is not on loopback.
///
/// A password is the one thing here that must never be logged: failures are recorded as a
/// rejection and nothing more.
/// </summary>
public sealed class LoginApi : IDisposable
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(LoginApi));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>Enough for the longest credential; anything larger is not a login.</summary>
    private const int MaximumRequestBytes = 8 * 1024;

    private readonly Setting _setting;
    private readonly LocalLoginServer _logins;
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;

    public LoginApi(Setting setting, LocalLoginServer logins)
    {
        _setting = setting ?? throw new ArgumentNullException(nameof(setting));
        _logins = logins ?? throw new ArgumentNullException(nameof(logins));
    }

    public void Start()
    {
        if (!_setting.LoginApiEnabled || _listener != null)
        {
            return;
        }

        string host = Equals(_setting.LoginApiListenIpAddress, IPAddress.Any)
            ? "+"
            : _setting.LoginApiListenIpAddress.ToString();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{host}:{_setting.LoginApiPort}/");
        _cancellation = new CancellationTokenSource();

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _listener = null;
            throw new InvalidOperationException(
                $"Login API could not bind port {_setting.LoginApiPort}. A non-loopback " +
                "prefix needs a URL reservation: netsh http add urlacl " +
                $"url=http://+:{_setting.LoginApiPort}/ user=Everyone",
                ex);
        }

        Logger.Info($"Launcher login API listening on http://{host}:{_setting.LoginApiPort}/login");
        if (host == "+" || !IPAddress.IsLoopback(_setting.LoginApiListenIpAddress))
        {
            Logger.Error(
                $"The login API is bound to {host} and speaks PLAIN HTTP - it carries " +
                "account passwords. Put a Cloudflare Tunnel or a TLS proxy in front and " +
                "bind this to 127.0.0.1, unless the network it is on is already private.");
        }

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
                Logger.Error($"Login API accept failed: {ex.Message}");
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
            string path = (context.Request.Url?.AbsolutePath ?? "/")
                .TrimEnd('/')
                .ToLowerInvariant();

            if (path is "" or "/health")
            {
                await WriteAsync(response, new { online = true });
                return;
            }

            if (path != "/login")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteAsync(response, new { error = "Unknown endpoint." });
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                // A password must not travel in a query string - it lands in every proxy
                // log along the way - so this endpoint is POST only.
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                await WriteAsync(response, new { error = "POST a JSON body to /login." });
                return;
            }

            LocalLoginRequest? request = await ReadRequestAsync(context.Request);
            if (request == null)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteAsync(response, new { error = "Expected {accountId, password}." });
                return;
            }

            // The same call the named pipe makes: one place decides whether a password is
            // right, and one rate limiter counts the attempt. The transport label is for
            // the log, so a login can be told apart from a local one at a glance.
            LocalLoginResponse result = _logins.Authenticate(request, DescribeCaller(context));
            await WriteAsync(response, result);
        }
        catch (HttpListenerException)
        {
            // Caller hung up.
        }
        catch (Exception ex)
        {
            // Never include the request body in a log line - it holds the password.
            Logger.Error($"Login API error: {ex.GetType().Name}.");
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
    /// Names this request for the log: the transport, and who sent it.
    ///
    /// Behind a tunnel or proxy the socket address is that proxy, not the player - so the
    /// forwarded-for header is reported when present, marked as claimed because it is
    /// caller-supplied and trivially forged. It is a debugging aid, never a decision input.
    /// </summary>
    private static string DescribeCaller(HttpListenerContext context)
    {
        string remote = context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        string? forwarded = context.Request.Headers["CF-Connecting-IP"]
            ?? context.Request.Headers["X-Forwarded-For"];
        return string.IsNullOrWhiteSpace(forwarded)
            ? $"HTTP API from {remote}"
            : $"HTTP API from {remote} (claims {forwarded.Trim()})";
    }

    private static async Task<LocalLoginRequest?> ReadRequestAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaximumRequestBytes)
        {
            return null;
        }

        // Read with a hard cap rather than trusting Content-Length, so a lying or absent
        // header cannot make the server buffer an unbounded body.
        byte[] buffer = new byte[MaximumRequestBytes];
        int read = 0;
        while (read < buffer.Length)
        {
            int taken = await request.InputStream.ReadAsync(buffer.AsMemory(read));
            if (taken <= 0)
            {
                break;
            }
            read += taken;
        }

        try
        {
            LocalLoginRequest? parsed = JsonSerializer.Deserialize<LocalLoginRequest>(
                buffer.AsSpan(0, read), Json);
            return string.IsNullOrEmpty(parsed?.AccountId) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        response.ContentType = "application/json; charset=utf-8";
        // A credential response must not be cached anywhere between here and the launcher.
        response.AddHeader("Cache-Control", "no-store");
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
            Logger.Error($"Login API stop failed: {ex.Message}");
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
