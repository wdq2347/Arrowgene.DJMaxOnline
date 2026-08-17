using System.IO.Pipes;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// Launcher authentication endpoint. Passwords never enter the game protocol; a
/// successful request receives a 60-second, one-use ticket instead.
///
/// Two transports, same request handling, same rate limiter:
///
///   NAMED PIPE - always on, and the only one a local launcher needs. It is
///   CurrentUserOnly and opened against ".", so it cannot be reached from another
///   machine or even another Windows account. Nothing crosses a network.
///
///   HTTP - see <see cref="LoginApi"/>, which handles a remote launcher and calls
///   straight into <see cref="Authenticate"/> here, so both transports share one set of
///   checks, one rate limiter and one ticket issuer.
/// </summary>
public sealed class LocalLoginServer
{
    /// <summary>How the named-pipe route names itself in the log.</summary>
    public const string PipeTransport = "named pipe";

    private const int MaximumInstances = 8;
    private const int MaximumAttemptsPerMinute = 5;
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(LocalLoginServer));

    private readonly object _lock = new();
    private readonly IPlayerRepository _players;
    private readonly LoginTicketService _tickets;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;
    private Task? _acceptTask;

    public LocalLoginServer(
        IPlayerRepository players,
        LoginTicketService tickets,
        string pipeName = LocalLoginProtocol.DefaultPipeName)
    {
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeName = pipeName;
    }

    public string PipeName { get; }

    public void Start()
    {
        lock (_lock)
        {
            if (_acceptTask != null)
            {
                return;
            }
            CancellationTokenSource cancellation = new();
            _cancellation = cancellation;
            _acceptTask = Task.Run(() => AcceptLoopAsync(cancellation.Token));
        }
        Logger.Info($"Secure launcher login pipe ready: {PipeName} (current user only).");
    }

    public void Stop()
    {
        Task? task;
        CancellationTokenSource? cancellation;
        lock (_lock)
        {
            cancellation = _cancellation;
            task = _acceptTask;
            _acceptTask = null;
            _cancellation = null;
        }
        cancellation?.Cancel();
        try
        {
            task?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    MaximumInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 4096,
                    outBufferSize: 4096);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(pipe, cancellationToken);
                pipe = null; // ownership transferred to HandleClientAsync
            }
            catch (OperationCanceledException)
            {
                if (pipe != null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
                break;
            }
            catch (Exception ex)
            {
                if (pipe != null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
                Logger.Exception(ex);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Serves one request. Takes a Stream so the pipe and the socket share it - the
    /// authentication, the ticket issue and the per-account rate limit are then the same
    /// code for a local and a remote launcher, and cannot drift apart.
    /// </summary>
    private async Task HandleClientAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                LocalLoginRequest request = await LocalLoginProtocol
                    .ReadAsync<LocalLoginRequest>(pipe, cancellationToken)
                    .ConfigureAwait(false);
                LocalLoginResponse response = Authenticate(request, PipeTransport);
                await LocalLoginProtocol.WriteAsync(pipe, response, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
                if (pipe.CanWrite)
                {
                    try
                    {
                        await LocalLoginProtocol.WriteAsync(
                            pipe, LocalLoginResponse.Rejected(), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    /// <summary>
    /// Verifies one login and issues a ticket. Public so <see cref="LoginApi"/> can serve a
    /// remote launcher through the SAME path a local one takes - the length limits, the
    /// per-account rate limit, the dummy verification on a miss and the ticket lifetime are
    /// then impossible to get wrong on one transport and right on the other.
    /// </summary>
    /// <param name="transport">
    /// Which route the request arrived on, for the log only - "named pipe" for a local
    /// launcher, or the HTTP API with its caller. Both go through this same method, so the
    /// log line is the only way to tell them apart when something misbehaves.
    /// </param>
    public LocalLoginResponse Authenticate(
        LocalLoginRequest request,
        string transport = PipeTransport)
    {
        string accountId = request.AccountId?.Trim() ?? string.Empty;
        string password = request.Password ?? string.Empty;
        // The account name is logged; the password never is, on any path.
        string who = accountId.Length == 0 ? "<no account>" : accountId;

        if (accountId.Length == 0 || accountId.Length > 64 ||
            password.Length == 0 || password.Length > PasswordSecurity.MaximumLength)
        {
            PasswordSecurity.PerformDummyVerification(password);
            Logger.Info($"Login REJECTED via {transport} for '{who}': malformed request.");
            return LocalLoginResponse.Rejected();
        }
        if (!PermitAttempt(accountId))
        {
            PasswordSecurity.PerformDummyVerification(password);
            Logger.Info(
                $"Login REJECTED via {transport} for '{who}': too many attempts " +
                $"({MaximumAttemptsPerMinute} a minute). This limit is shared by both " +
                "transports, so the other route is refused too.");
            return LocalLoginResponse.Rejected();
        }

        if (!_players.TryGetPasswordCredential(accountId, out PlayerPasswordCredential? credential) ||
            credential == null)
        {
            PasswordSecurity.PerformDummyVerification(password);
            Logger.Info($"Login REJECTED via {transport} for '{who}': no such account.");
            return LocalLoginResponse.Rejected();
        }
        if (!PasswordSecurity.Verify(password, credential))
        {
            Logger.Info($"Login REJECTED via {transport} for '{who}': wrong password.");
            return LocalLoginResponse.Rejected();
        }

        // THE BAN IS ENFORCED HERE, and no ticket is issued.
        //
        // This is the right place for it: the game client cannot be told why it was
        // refused. Once the launcher hands over a ticket the client goes straight to
        // channel select, whose director (sub_4499DA) chooses its own dialog text from a
        // hardcoded set and has no account-lock case at all - the locked-account dialogs
        // belong to the login screen the launcher flow skips. Refusing the ticket means
        // the launcher reports the ban itself, in plain text, before the game starts.
        //
        // Checked AFTER the password so a lock cannot be probed without credentials.
        if (_players.TryLoad(credential.UserId, out Packets.LocalPlayerProfile? profile) &&
            profile != null &&
            profile.LockState != AccountLockState.None)
        {
            // The account is named, the reason is not. Operators read it from the log.
            Logger.Info(
                $"Login REFUSED via {transport} for '{who}' (SQLite user " +
                $"{credential.UserId}): account is " +
                $"{AccountLockReasons.Describe(profile.LockState)}" +
                (profile.LockReason.Length == 0 ? "." : $": {profile.LockReason}"));
            return LocalLoginResponse.Locked(profile.LockState);
        }

        string token = _tickets.Issue(credential.UserId);
        Logger.Info(
            $"Login OK via {transport}: '{who}' (SQLite user {credential.UserId}); " +
            "issued a short-lived one-time game ticket.");
        return new LocalLoginResponse(
            true,
            token,
            checked((int)LoginTicketService.DefaultLifetime.TotalSeconds),
            null);
    }

    private bool PermitAttempt(string accountId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_attempts)
        {
            if (!_attempts.TryGetValue(accountId, out Queue<DateTimeOffset>? attempts))
            {
                attempts = new Queue<DateTimeOffset>();
                _attempts.Add(accountId, attempts);
            }
            while (attempts.TryPeek(out DateTimeOffset oldest) &&
                   oldest <= now - TimeSpan.FromMinutes(1))
            {
                attempts.Dequeue();
            }
            if (attempts.Count >= MaximumAttemptsPerMinute)
            {
                return false;
            }
            attempts.Enqueue(now);
            return true;
        }
    }
}
