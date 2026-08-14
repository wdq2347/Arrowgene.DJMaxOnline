using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Arrowgene.DJMaxOnline.Launcher;

/// <summary>
/// The server's reply to a login. Declared here rather than shared with the server so the
/// launcher needs nothing from that project - see <see cref="LoginClient"/>.
///
/// The property names are the JSON contract with the server's login API. They must not be
/// renamed without changing it too.
/// </summary>
internal sealed record LoginResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds,
    [property: JsonPropertyName("error")] string? Error)
{
    public static LoginResponse Failed(string error) => new(false, null, 0, error);
}

/// <summary>
/// Logs in against the server's HTTP login API and returns a one-use game ticket.
///
/// This is the launcher's ONLY server dependency, and it is deliberately hand-rolled: the
/// launcher used to reference the whole server project for a handful of types, which
/// dragged the TCP networking library, SQLite and its three native providers into the
/// shipped folder - roughly 50 MB and ten DLLs to send one JSON request. HttpClient can do
/// that on its own.
///
/// One transport for local and remote. A server on this machine is just
/// http://127.0.0.1:8091/login, which keeps a single code path rather than a pipe for one
/// case and HTTP for the other.
/// </summary>
internal static class LoginClient
{
    /// <summary>
    /// Length of a ticket, as issued by LoginTicketService: 16 random bytes in base64url
    /// with the padding removed. Checked before the ticket is handed to the game so a
    /// truncated or wrong-shaped reply is caught here rather than by the client.
    /// </summary>
    public const int TokenLength = 22;

    /// <summary>Matches the server's PasswordSecurity.MaximumLength.</summary>
    public const int MaximumPasswordLength = 128;

    /// <summary>The account field in the game's own login packet, including its terminator.</summary>
    public const int MaximumAccountLength = 25;

    public static async Task<LoginResponse> AuthenticateAsync(
        HttpClient http,
        string loginUrl,
        string accountId,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(loginUrl);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        // POST, never a query string: a password in a URL lands in every proxy log along
        // the way.
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            loginUrl,
            new LoginRequest(accountId, password),
            linked.Token);

        if (!response.IsSuccessStatusCode)
        {
            return LoginResponse.Failed(
                $"The login server answered HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<LoginResponse>(linked.Token)
                ?? LoginResponse.Failed("The login server sent no answer.");
        }
        catch (System.Text.Json.JsonException)
        {
            // Usually a proxy or captive portal answering in place of the server.
            return LoginResponse.Failed(
                "The login server sent something that was not a login reply.");
        }
    }

    private sealed record LoginRequest(
        [property: JsonPropertyName("accountId")] string AccountId,
        [property: JsonPropertyName("password")] string Password);
}
