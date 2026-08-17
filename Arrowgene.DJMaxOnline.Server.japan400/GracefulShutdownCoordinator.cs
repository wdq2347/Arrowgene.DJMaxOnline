using System.Text;
using Arrowgene.DJMaxOnline.Server.Japan400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

public sealed record GracefulShutdownResult(
    string Reason,
    int InitiallyConnected,
    int InitiallyActive,
    int DisconnectedDuringDrain,
    int PendingProcessTermination);

/// <summary>Wire-safe text shared by console and future remote administration adapters.</summary>
public static class ShutdownAnnouncementPolicy
{
    public const string DefaultReason = "Scheduled";
    public const int MaximumReasonLength = 120;

    public static string NormalizeReason(string? value)
    {
        string reason = string.IsNullOrWhiteSpace(value)
            ? DefaultReason
            : value.Trim();
        if (reason.Length > MaximumReasonLength)
        {
            throw new ArgumentException(
                $"Shutdown reason cannot exceed {MaximumReasonLength} characters.",
                nameof(value));
        }
        if (reason.Any(character => character > 0x7F || char.IsControl(character)))
        {
            throw new ArgumentException(
                "Shutdown reasons must contain printable ASCII text only.", nameof(value));
        }
        return reason;
    }

    public static string Initial(string reason) => EnsureWireLength(
        $"Server shutdown requested: {reason}. Active songs, courses and ranked games " +
        "may finish; all users will disconnect when shutdown completes.");

    public static string Waiting(int activePlayers, string reason) => EnsureWireLength(
        $"Shutdown pending ({reason}). Waiting for {activePlayers} active " +
        $"player{(activePlayers == 1 ? string.Empty : "s")} to finish.");

    public static string Countdown(int seconds, string reason)
    {
        if (seconds is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }
        return EnsureWireLength(
            $"Server shutdown in {seconds} second{(seconds == 1 ? string.Empty : "s")}. " +
            $"Reason: {reason}");
    }

    public static string Now(string reason) =>
        EnsureWireLength($"Server shutting down now. Reason: {reason}");

    private static string EnsureWireLength(string value)
    {
        if (Encoding.ASCII.GetByteCount(value) > ChatInfPacket.MaximumTextLength)
        {
            throw new InvalidOperationException(
                "The generated shutdown announcement exceeds the client packet limit.");
        }
        return value;
    }
}

/// <summary>
/// Coordinates a production-safe server drain without placing console or Discord concerns
/// in the game lifecycle. New adapters can call the same operation later.
/// </summary>
public sealed class GracefulShutdownCoordinator
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(GracefulShutdownCoordinator));
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FinalAnnouncementSettle = TimeSpan.FromMilliseconds(250);

    private readonly global::DjMaxServer _server;
    private readonly ServerAdministrationService _administration;
    private readonly TimeProvider _timeProvider;

    public GracefulShutdownCoordinator(
        global::DjMaxServer server,
        ServerAdministrationService administration,
        TimeProvider? timeProvider = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _administration = administration ??
            throw new ArgumentNullException(nameof(administration));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public GracefulShutdownResult Shutdown(string? reason) =>
        ShutdownAsync(reason).GetAwaiter().GetResult();

    public async Task<GracefulShutdownResult> ShutdownAsync(
        string? reason,
        CancellationToken cancellationToken = default)
    {
        string normalizedReason = ShutdownAnnouncementPolicy.NormalizeReason(reason);
        if (!_server.BeginShutdownDrain())
        {
            throw new InvalidOperationException("Server shutdown draining has already started.");
        }

        int initiallyConnected = _server.ClientLookup.GetAll().Count;
        _administration.BroadcastChat(
            ShutdownAnnouncementPolicy.Initial(normalizedReason),
            ChatMessageType.Alert);

        int initiallyActive = _server.GetShutdownProtectedClients().Count;
        Logger.Info(
            $"Graceful shutdown ({normalizedReason}): {initiallyConnected} connection(s); " +
            $"{initiallyActive} active player(s) protected. Connections remain owned by " +
            "the process until final termination.");

        if (initiallyActive > 0)
        {
            _administration.BroadcastChat(
                ShutdownAnnouncementPolicy.Waiting(initiallyActive, normalizedReason),
                ChatMessageType.Notice);
            await WaitForActiveSessionsAsync(cancellationToken).ConfigureAwait(false);
        }

        // Whether users were playing or only waiting in the lobby, every connected
        // client gets the full warning period before process termination.
        if (_server.ClientLookup.GetAll().Any())
        {
            for (int seconds = 10; seconds >= 1; seconds--)
            {
                _administration.BroadcastChat(
                    ShutdownAnnouncementPolicy.Countdown(seconds, normalizedReason),
                    ChatMessageType.Alert);
                await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _administration.BroadcastChat(
            ShutdownAnnouncementPolicy.Now(normalizedReason),
            ChatMessageType.Alert);
        await DelayAsync(FinalAnnouncementSettle, cancellationToken).ConfigureAwait(false);

        int disconnectedDuringDrain = Math.Max(
            0, initiallyConnected - _server.ClientLookup.GetAll().Count);
        int pendingProcessTermination = _server.ClientLookup.GetAll().Count;

        // Deliberately leave every accepted socket open. The CLI terminates the process
        // after this drain returns, making Windows tear down the live socket handles in
        // the same way as Ctrl+C. Calling DjMaxServer.Stop or Client.Close first changes
        // the wire-level close and leaves this retail client on a stale lobby/room scene.
        return new GracefulShutdownResult(
            normalizedReason,
            initiallyConnected,
            initiallyActive,
            disconnectedDuringDrain,
            PendingProcessTermination: pendingProcessTermination);
    }

    private async Task WaitForActiveSessionsAsync(CancellationToken cancellationToken)
    {
        int lastCount = -1;
        while (true)
        {
            int current = _server.GetShutdownProtectedClients().Count;
            if (current != lastCount)
            {
                Logger.Info($"Graceful shutdown waiting on {current} active player(s).");
                lastCount = current;
            }

            if (current == 0)
            {
                // FinishPlay changes the room phase before it finishes course advancement
                // and result delivery. Require a second idle observation one second later
                // so a course cannot be mistaken for complete between two stages.
                await DelayAsync(ActivePollInterval, cancellationToken).ConfigureAwait(false);
                if (_server.GetShutdownProtectedClients().Count == 0)
                {
                    return;
                }
                continue;
            }

            await DelayAsync(ActivePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _timeProvider, cancellationToken);
}
