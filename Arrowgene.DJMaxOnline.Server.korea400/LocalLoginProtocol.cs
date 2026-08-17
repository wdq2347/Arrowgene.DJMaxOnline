using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

public sealed record LocalLoginRequest(string AccountId, string Password);

public sealed record LocalLoginResponse(
    bool Success,
    string? Token,
    int ExpiresInSeconds,
    string? Error)
{
    public static LocalLoginResponse Rejected() =>
        new(false, null, 0, "Invalid account or password.");

    /// <summary>
    /// Turned away because the account is locked, not because the credentials were wrong.
    ///
    /// This is the only rejection that says anything specific, and it is only ever returned
    /// AFTER the password has been verified - otherwise anyone could probe which accounts
    /// are banned without knowing a password.
    ///
    /// The operator's reason is deliberately not included. It is recorded server-side and
    /// stays there.
    /// </summary>
    public static LocalLoginResponse Locked(AccountLockState state) =>
        new(false, null, 0, state == AccountLockState.UnderReview
            ? "This account is under review by the operations team and cannot be used yet."
            : "This account has been banned.");
}

/// <summary>Length-prefixed JSON used only over the same-user local named pipe.</summary>
public static class LocalLoginProtocol
{
    public const string DefaultPipeName = "Arrowgene.DJMaxOnline.Login.v1";
    private const int MaximumMessageSize = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length == 0 || payload.Length > MaximumMessageSize)
        {
            throw new InvalidDataException("Local-login message size is invalid.");
        }

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageSize)
        {
            throw new InvalidDataException("Local-login message size is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions) ??
                   throw new InvalidDataException(
                       "Local-login message is empty or malformed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}

public static class LocalLoginClient
{
    public static async Task<LocalLoginResponse> AuthenticateAsync(
        string accountId,
        string password,
        string pipeName = LocalLoginProtocol.DefaultPipeName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeoutSource = new(timeout ?? TimeSpan.FromSeconds(5));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token, cancellationToken);
        await using NamedPipeClientStream pipe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
        await LocalLoginProtocol.WriteAsync(
            pipe, new LocalLoginRequest(accountId, password), linked.Token).ConfigureAwait(false);
        return await LocalLoginProtocol.ReadAsync<LocalLoginResponse>(
            pipe, linked.Token).ConfigureAwait(false);
    }
}
