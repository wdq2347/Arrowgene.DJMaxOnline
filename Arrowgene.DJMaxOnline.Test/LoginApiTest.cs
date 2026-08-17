using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The HTTP login route a remote launcher uses.
///
/// It exists so a launcher that is not on the server's machine can authenticate, and it
/// deliberately owns no authentication of its own - every request goes through
/// <see cref="LocalLoginServer.Authenticate"/>, the same method the named pipe uses. These
/// pin the parts that would otherwise make it a weaker second door: the method, the
/// rejection shape, and that a wrong password is answered rather than thrown.
/// </summary>
[TestFixture]
public class LoginApiTest
{
    private const string Account = "tester";
    private const string Password = "correct horse battery";

    private static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A real repository on a temp database rather than a stub, so the test exercises the
    /// same credential path production does.
    /// </summary>
    private (SqlitePlayerRepository Repository, string Path) NewRepository()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"djmax-login-{Guid.NewGuid():N}.sqlite3");
        _databases.Add(path);
        SqlitePlayerRepository repository = new(path);
        LocalPlayerProfile profile = new()
        {
            UserId = 7,
            AccountId = Account,
            Nickname = "Tester",
        };
        repository.Save(profile);
        repository.SetPasswordCredential(PasswordSecurity.Create(7, Password));
        return (repository, path);
    }

    private readonly List<string> _databases = [];

    [TearDown]
    public void RemoveDatabases()
    {
        foreach (string path in _databases)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still held by a connection; the temp folder can have it.
            }
        }
        _databases.Clear();
    }

    private (LoginApi Api, int Port) StartApi()
    {
        int port = FreePort();
        Setting setting = new()
        {
            LoginApiEnabled = true,
            LoginApiListenIpAddress = IPAddress.Loopback,
            LoginApiPort = (ushort)port
        };
        LocalLoginServer logins = new(
            NewRepository().Repository,
            new LoginTicketService(),
            $"test-pipe-{Guid.NewGuid():N}");
        LoginApi api = new(setting, logins);
        api.Start();
        return (api, port);
    }

    private static async Task<(HttpStatusCode Code, string Body)> PostAsync(
        int port, string path, string json)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response =
            await client.PostAsync($"http://127.0.0.1:{port}{path}", content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task ACorrectPasswordReturnsAOneUseTicket()
    {
        (LoginApi api, int port) = StartApi();
        using (api)
        {
            (HttpStatusCode code, string body) = await PostAsync(
                port, "/login",
                JsonSerializer.Serialize(new { accountId = Account, password = Password }));

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(code, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(root.GetProperty("success").GetBoolean(), Is.True);
                Assert.That(root.GetProperty("token").GetString(),
                    Has.Length.EqualTo(LoginTicketService.TokenLength));
                Assert.That(root.GetProperty("expiresInSeconds").GetInt32(),
                    Is.GreaterThan(0));
            });
        }
    }

    [Test]
    public async Task AWrongPasswordIsAnsweredNotThrown()
    {
        (LoginApi api, int port) = StartApi();
        using (api)
        {
            (HttpStatusCode code, string body) = await PostAsync(
                port, "/login",
                JsonSerializer.Serialize(new { accountId = Account, password = "wrong" }));

            using JsonDocument document = JsonDocument.Parse(body);
            Assert.Multiple(() =>
            {
                // 200 with success=false: a rejection is an answer, and an HTTP error
                // code would let a proxy or CDN treat it as an outage.
                Assert.That(code, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(document.RootElement.GetProperty("success").GetBoolean(),
                    Is.False);
                Assert.That(body, Does.Not.Contain("wrong"), "never echo the password");
            });
        }
    }

    [Test]
    public async Task AnUnknownAccountIsRejectedTheSameWay()
    {
        (LoginApi api, int port) = StartApi();
        using (api)
        {
            (_, string body) = await PostAsync(
                port, "/login",
                JsonSerializer.Serialize(new { accountId = "nobody", password = Password }));

            using JsonDocument document = JsonDocument.Parse(body);
            // Identical shape to a wrong password, so the endpoint cannot be used to
            // enumerate which accounts exist.
            Assert.That(document.RootElement.GetProperty("success").GetBoolean(), Is.False);
        }
    }

    [Test]
    public async Task TheRateLimitIsSharedWithThePipe()
    {
        (LoginApi api, int port) = StartApi();
        using (api)
        {
            // Five attempts a minute per account, counted by the login server itself - so
            // the HTTP route cannot be used to get more tries than the pipe allows.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await PostAsync(port, "/login",
                    JsonSerializer.Serialize(new { accountId = Account, password = "wrong" }));
            }

            (_, string body) = await PostAsync(
                port, "/login",
                JsonSerializer.Serialize(new { accountId = Account, password = Password }));

            using JsonDocument document = JsonDocument.Parse(body);
            Assert.That(document.RootElement.GetProperty("success").GetBoolean(), Is.False,
                "the correct password must still be refused once the limit is hit");
        }
    }

    [Test]
    public async Task GetIsRefusedSoAPasswordCannotRideInAQueryString()
    {
        (LoginApi api, int port) = StartApi();
        using (api)
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpResponseMessage response = await client.GetAsync(
                $"http://127.0.0.1:{port}/login?accountId={Account}&password={Password}");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        }
    }

    [Test]
    public async Task GarbageAndUnknownPathsAreRefusedCleanly()
    {
        (LoginApi api, int port) = StartApi();
        using (api)
        {
            (HttpStatusCode bad, _) = await PostAsync(port, "/login", "not json at all");
            (HttpStatusCode missing, _) = await PostAsync(port, "/elsewhere", "{}");

            Assert.Multiple(() =>
            {
                Assert.That(bad, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(missing, Is.EqualTo(HttpStatusCode.NotFound));
            });
        }
    }

    [Test]
    public void TheApiStaysOffUnlessEnabled()
    {
        int port = FreePort();
        Setting setting = new()
        {
            LoginApiEnabled = false,
            LoginApiListenIpAddress = IPAddress.Loopback,
            LoginApiPort = (ushort)port
        };
        using LoginApi api = new(
            setting,
            new LocalLoginServer(
                NewRepository().Repository,
                new LoginTicketService(),
                $"test-pipe-{Guid.NewGuid():N}"));
        api.Start();

        // Nothing should be listening: a password endpoint must not appear by default.
        // Tested at the socket rather than over HTTP - a closed loopback port may refuse
        // or simply never answer, and either way it is not serving.
        using TcpClient probe = new();
        bool connected = probe.ConnectAsync(IPAddress.Loopback, port)
            .Wait(TimeSpan.FromSeconds(2));
        Assert.That(connected, Is.False, "the login API must not listen when disabled");
    }

    [Test]
    public void TheTransportIsNamedSoTheLogCanTellThemApart()
    {
        // Both routes call one Authenticate, so the transport label is the only thing in
        // the log that distinguishes a remote login from a local one.
        LocalLoginServer logins = new(
            NewRepository().Repository,
            new LoginTicketService(),
            $"test-pipe-{Guid.NewGuid():N}");

        LocalLoginResponse pipe = logins.Authenticate(
            new LocalLoginRequest(Account, Password), LocalLoginServer.PipeTransport);
        LocalLoginResponse api = logins.Authenticate(
            new LocalLoginRequest(Account, Password), "HTTP API from 127.0.0.1");

        Assert.Multiple(() =>
        {
            Assert.That(LocalLoginServer.PipeTransport, Is.EqualTo("named pipe"));
            // The label is for the log only - it must not change the answer.
            Assert.That(pipe.Success, Is.True);
            Assert.That(api.Success, Is.True);
            Assert.That(api.Token, Is.Not.EqualTo(pipe.Token), "each login is one-use");
        });
    }

    /// <summary>
    /// A locked account gets no ticket, on either transport.
    ///
    /// This is where a ban is actually enforced. The game client cannot be told why it was
    /// refused - the launcher hands it a ticket and it goes straight to channel select,
    /// whose dialog text is chosen from a hardcoded set with no account-lock case - so
    /// withholding the ticket is what makes a ban both effective and explicable.
    /// </summary>
    [TestCase(AccountLockState.Locked)]
    [TestCase(AccountLockState.UnderReview)]
    public void ALockedAccountIsRefusedAndIssuedNoTicket(AccountLockState state)
    {
        (SqlitePlayerRepository repository, _) = NewRepository();
        Assert.That(repository.TryLoad(7, out LocalPlayerProfile? profile), Is.True);
        profile!.LockState = state;
        profile.LockReason = "operator eyes only";
        repository.Save(profile);

        LocalLoginServer logins = new(
            repository, new LoginTicketService(), $"test-pipe-{Guid.NewGuid():N}");
        LocalLoginResponse response = logins.Authenticate(
            new LocalLoginRequest(Account, Password), LocalLoginServer.PipeTransport);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.Token, Is.Null, "a locked account must get no ticket");
            // The player is told they are banned; WHY is the operator's business and is
            // recorded only in the server log.
            Assert.That(response.Error, Is.Not.Null);
            Assert.That(response.Error, Does.Not.Contain("operator eyes only"));
        });
    }

    /// <summary>
    /// The lock is checked only after the password is verified. Otherwise the endpoint
    /// would report which accounts are banned to anyone who can guess a name.
    /// </summary>
    [Test]
    public void ALockedAccountIsIndistinguishableWithoutThePassword()
    {
        (SqlitePlayerRepository repository, _) = NewRepository();
        Assert.That(repository.TryLoad(7, out LocalPlayerProfile? profile), Is.True);
        profile!.LockState = AccountLockState.Locked;
        repository.Save(profile);

        LocalLoginServer logins = new(
            repository, new LoginTicketService(), $"test-pipe-{Guid.NewGuid():N}");
        LocalLoginResponse wrongPassword = logins.Authenticate(
            new LocalLoginRequest(Account, "not the password"));
        LocalLoginResponse noSuchAccount = logins.Authenticate(
            new LocalLoginRequest("nobody", "not the password"));

        Assert.That(
            wrongPassword.Error,
            Is.EqualTo(noSuchAccount.Error),
            "a locked account must look exactly like any other failed login");
    }

    /// <summary>Unlocking restores the ticket, without a restart.</summary>
    [Test]
    public void UnlockingLetsTheAccountLogInAgain()
    {
        (SqlitePlayerRepository repository, _) = NewRepository();
        LocalLoginServer logins = new(
            repository, new LoginTicketService(), $"test-pipe-{Guid.NewGuid():N}");

        Assert.That(repository.TryLoad(7, out LocalPlayerProfile? profile), Is.True);
        profile!.LockState = AccountLockState.Locked;
        repository.Save(profile);
        Assert.That(
            logins.Authenticate(new LocalLoginRequest(Account, Password)).Success,
            Is.False);

        profile.LockState = AccountLockState.None;
        profile.LockReason = string.Empty;
        repository.Save(profile);

        LocalLoginResponse response =
            logins.Authenticate(new LocalLoginRequest(Account, Password));
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Token, Is.Not.Null);
        });
    }

    /// <summary>
    /// Revoking takes away the live session, not just future tickets.
    ///
    /// This is what makes a ban stick rather than loop. A session outlives a disconnect on
    /// purpose so a channel change can resume it, so without this a banned player's client
    /// reconnects, resumes, runs the whole login and has to be dropped again, over and over.
    /// </summary>
    [Test]
    public void RevokingASessionStopsAReconnectFromResumingIt()
    {
        LoginTicketService tickets = new();
        string token = tickets.Issue(7);
        Assert.That(
            tickets.TryOpenSession(token, out uint userId, out LoginSessionLease? lease),
            Is.True);
        Assert.That(userId, Is.EqualTo(7u));
        Assert.That(lease, Is.Not.Null);
        // A reconnect resumes it, which is the behaviour a ban has to defeat.
        Assert.That(tickets.TryOpenTransition(7, out LoginSessionLease? resumed), Is.True);
        Assert.That(resumed, Is.Not.Null);

        int revoked = tickets.Revoke(7);

        Assert.Multiple(() =>
        {
            Assert.That(revoked, Is.GreaterThan(0));
            Assert.That(
                tickets.TryOpenTransition(7, out _),
                Is.False,
                "a revoked session must not be resumable");
        });
    }

    /// <summary>An unissued ticket is revoked too, so a ban cannot be raced by the launcher.</summary>
    [Test]
    public void RevokingAlsoDiscardsAnUnusedTicket()
    {
        LoginTicketService tickets = new();
        string token = tickets.Issue(7);

        Assert.That(tickets.Revoke(7), Is.GreaterThan(0));
        Assert.That(
            tickets.TryOpenSession(token, out _, out _),
            Is.False,
            "a ticket issued just before the ban must not still work");
    }

    /// <summary>Revoking one account leaves everyone else alone.</summary>
    [Test]
    public void RevokingIsScopedToOneAccount()
    {
        LoginTicketService tickets = new();
        string other = tickets.Issue(9);
        tickets.Issue(7);

        tickets.Revoke(7);

        Assert.That(tickets.TryOpenSession(other, out uint userId, out _), Is.True);
        Assert.That(userId, Is.EqualTo(9u));
    }
}
