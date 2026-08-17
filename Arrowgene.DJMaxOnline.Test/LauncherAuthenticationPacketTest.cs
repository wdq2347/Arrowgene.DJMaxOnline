using System.Buffers.Binary;
using System.Text;
using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Microsoft.Data.Sqlite;

namespace Arrowgene.DJMaxOnline.Test;

public sealed class LauncherAuthenticationPacketTest
{
    [Test]
    public void ConnectFromNmDisabledCarriesTicketThenSeed()
    {
        byte[] seed = Enumerable.Range(1, JpConnectConfirmReqPacket.SeedSize)
            .Select(value => (byte)value)
            .ToArray();
        byte[] wire = new byte[PacketMeta.JpConnectConfirmReq.Size];
        BinaryPrimitives.WriteUInt16LittleEndian(
            wire, (ushort)PacketId.JpConnectConfirmReq);
        wire[2] = 0xCC;
        const string ticket = "AbCdEfGhIjKlMnOpQrStUv";
        Encoding.ASCII.GetBytes(ticket).CopyTo(wire, 3);
        seed.CopyTo(wire, 30);

        LauncherAuthenticationRequest request =
            JpConnectConfirmReqPacket.Parse(ReadOne(wire));

        Assert.Multiple(() =>
        {
            Assert.That(request.Ticket, Is.EqualTo(ticket));
            Assert.That(request.CipherSeed, Is.EqualTo(seed));
            Assert.That(ReadOne(wire).ToLog(), Does.Not.Contain(ticket));
            Assert.That(ReadOne(wire).ToLog(), Does.Contain("redacted"));
        });
    }

    [Test]
    public void ConnectFromNmEnabledCarriesDynamicClipboardTicket()
    {
        const string ticket = "VwXyZ0123456789AbCdEfG";
        byte[] seed = Enumerable.Range(31, NetmarbleAuthenticateReqPacket.SeedSize)
            .Select(value => (byte)value)
            .ToArray();
        byte[] encoded = Encoding.ASCII.GetBytes(ticket);
        byte[] wire = new byte[37 + encoded.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            wire, (ushort)PacketId.NetmarbleAuthenticateReq);
        wire[2] = 0xCC;
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(3), (uint)wire.Length);
        seed.CopyTo(wire, 7);
        encoded.CopyTo(wire, 37);

        LauncherAuthenticationRequest request =
            NetmarbleAuthenticateReqPacket.Parse(ReadOne(wire));

        Assert.Multiple(() =>
        {
            Assert.That(request.Ticket, Is.EqualTo(ticket));
            Assert.That(request.CipherSeed, Is.EqualTo(seed));
        });
    }

    private static Packet ReadOne(byte[] wire)
    {
        PacketFactory factory = new();
        factory.FillReadBuffer(wire);
        List<Packet> packets = factory.ReadPackets();
        Assert.That(packets, Has.Count.EqualTo(1));
        return packets[0];
    }
}

public sealed class LocalAccountResolverTest
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "djmax-login-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void TicketCreatesTransitionSessionAndCachesTheResolvedStore()
    {
        SqlitePlayerRepository repository = new(
            Path.Combine(_directory, "players.sqlite3"));
        LocalPlayerProfile first = repository.Create("FIRST", "PublicOne");
        LocalPlayerProfile second = repository.Create("SECOND", "PublicTwo");
        string shopDirectory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        ShopCatalog shop = ShopCatalog.Load(shopDirectory);
        LocalPlayerStore fallback = new(first, shop, repository: repository);
        LoginTicketService tickets = new();
        LocalAccountResolver resolver = new(fallback, shop, repository, tickets);
        string ticket = tickets.Issue(second.UserId);

        Assert.That(
            resolver.TryResolveTicket(
                ticket,
                out LocalPlayerStore? selected,
                out LoginSessionLease? firstLease),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.Not.Null);
            Assert.That(selected!.Profile.UserId, Is.EqualTo(second.UserId));
            Assert.That(
                resolver.TryResolveAuthenticatedUser(
                    second.UserId,
                    out LocalPlayerStore? transitioned,
                    out LoginSessionLease? secondLease),
                Is.True,
                "LogInReq must be able to claim the launcher session by user id");
            Assert.That(transitioned, Is.SameAs(selected));
            Assert.That(firstLease!.IsReconnect, Is.False);
            Assert.That(secondLease!.IsReconnect, Is.True);
            Assert.That(
                resolver.TryResolveTicket("SECOND", out _, out _),
                Is.False,
                "an account id must not bypass launcher authentication");
            Assert.That(
                resolver.TryResolveTicket(
                    tickets.Issue(second.UserId),
                    out LocalPlayerStore? repeated,
                    out _),
                Is.True);
            Assert.That(repeated, Is.SameAs(selected),
                "two sockets on one account must share the synchronization lock");
        });
    }

    [Test]
    public async Task SameUserPipeAuthenticatesPasswordAndReturnsConsumableTicket()
    {
        SqlitePlayerRepository repository = new(
            Path.Combine(_directory, "pipe-players.sqlite3"));
        LocalPlayerProfile player = repository.Create("PIPEUSER", "PipePlayer");
        repository.SetPasswordCredential(
            PasswordSecurity.Create(player.UserId, "pipe test password"));
        LoginTicketService tickets = new();
        string pipeName = $"Arrowgene.DJMaxOnline.Test.{Guid.NewGuid():N}";
        LocalLoginServer server = new(repository, tickets, pipeName);
        server.Start();
        try
        {
            LocalLoginResponse rejected = await LocalLoginClient.AuthenticateAsync(
                "PIPEUSER", "wrong password", pipeName, TimeSpan.FromSeconds(10));
            LocalLoginResponse accepted = await LocalLoginClient.AuthenticateAsync(
                "pipeuser", "pipe test password", pipeName, TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(rejected.Success, Is.False);
                Assert.That(rejected.Token, Is.Null);
                Assert.That(accepted.Success, Is.True);
                Assert.That(accepted.Token, Has.Length.EqualTo(LoginTicketService.TokenLength));
                Assert.That(tickets.TryConsume(accepted.Token!, out uint userId), Is.True);
                Assert.That(userId, Is.EqualTo(player.UserId));
                Assert.That(tickets.TryConsume(accepted.Token!, out _), Is.False);
            });
        }
        finally
        {
            server.Stop();
        }
    }
}

public sealed class PasswordAndTicketSecurityTest
{
    [Test]
    public void PasswordHashIsSaltedAndRejectsWrongPassword()
    {
        PlayerPasswordCredential first = PasswordSecurity.Create(1, "correct horse battery");
        PlayerPasswordCredential second = PasswordSecurity.Create(1, "correct horse battery");

        Assert.Multiple(() =>
        {
            Assert.That(first.Salt, Is.Not.EqualTo(second.Salt));
            Assert.That(first.Hash, Is.Not.EqualTo(second.Hash));
            Assert.That(PasswordSecurity.Verify("correct horse battery", first), Is.True);
            Assert.That(PasswordSecurity.Verify("incorrect password", first), Is.False);
        });
    }

    [Test]
    public void TicketExpiresAndCanOnlyBeConsumedOnce()
    {
        MutableTimeProvider time = new(new DateTimeOffset(
            2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        LoginTicketService tickets = new(TimeSpan.FromSeconds(10), time);
        string first = tickets.Issue(42);

        Assert.That(tickets.TryConsume(first, out uint userId), Is.True);
        Assert.That(userId, Is.EqualTo(42));
        Assert.That(tickets.TryConsume(first, out _), Is.False);

        string expired = tickets.Issue(43);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.That(tickets.TryConsume(expired, out _), Is.False);
    }

    [Test]
    public void SoleLauncherSessionAdmitsTheSecondClientSocket()
    {
        LoginTicketService tickets = new();
        tickets.Issue(42);

        Assert.That(
            tickets.TryOpenSoleLauncherSession(out uint firstUser, out LoginSessionLease? first),
            Is.True);
        Assert.That(firstUser, Is.EqualTo(42));
        Assert.That(first!.IsReconnect, Is.False);

        // The JP client opens its selected-channel socket without repeating its ticket.
        // It must join the one live launcher session rather than being handed a placeholder
        // identity just because the ticket was consumed by the server-list socket.
        Assert.That(
            tickets.TryOpenSoleLauncherSession(out uint secondUser, out LoginSessionLease? second),
            Is.True);
        Assert.That(secondUser, Is.EqualTo(42));
        Assert.That(second!.IsReconnect, Is.True);

        Assert.That(
            tickets.TryOpenSoleLauncherSession(out _, out _),
            Is.False,
            "only the server-list and selected-channel sockets may overlap");
    }

    [Test]
    public void RepeatedLoginForOneAccountLeavesOnePendingLauncherContext()
    {
        LoginTicketService tickets = new();
        tickets.Issue(42);
        tickets.Issue(42);
        tickets.Issue(42);

        Assert.That(
            tickets.TryOpenSoleLauncherSession(out uint userId, out LoginSessionLease? lease),
            Is.True,
            "launcher retry traffic for one account must not look like several players");
        Assert.That(userId, Is.EqualTo(42));
        Assert.That(lease, Is.Not.Null);

        LoginTicketService competingTickets = new();
        competingTickets.Issue(42);
        competingTickets.Issue(42);
        competingTickets.Issue(99);
        Assert.That(
            competingTickets.TryOpenSoleLauncherSession(out _, out _),
            Is.False,
            "a pending login for another account must still be treated as ambiguous");
    }

    [Test]
    public void AuthenticatedSessionAllowsHandoffAndShortReconnectOnly()
    {
        MutableTimeProvider time = new(new DateTimeOffset(
            2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        LoginTicketService tickets = new(
            lifetime: TimeSpan.FromSeconds(10),
            timeProvider: time,
            reconnectGrace: TimeSpan.FromSeconds(5));
        string token = tickets.Issue(42);

        Assert.That(
            tickets.TryOpenSession(token, out uint firstUser, out LoginSessionLease? first),
            Is.True);
        Assert.That(firstUser, Is.EqualTo(42));
        Assert.That(first!.IsReconnect, Is.False);

        // The selected channel can overlap the server-list socket, but a third
        // concurrent replay is rejected. A live server-list socket keeps this
        // handoff available even after the disconnected-socket grace has elapsed.
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.That(
            tickets.TryOpenTransition(42, out LoginSessionLease? second),
            Is.True);
        Assert.That(second!.IsReconnect, Is.True);
        Assert.That(tickets.TryOpenSession(token, out _, out _), Is.False);

        Assert.That(tickets.Release(first), Is.True);
        Assert.That(tickets.Release(second), Is.True);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.That(
            tickets.TryOpenSession(token, out _, out LoginSessionLease? reconnected),
            Is.True);
        Assert.That(tickets.Release(reconnected), Is.True);

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.That(tickets.TryOpenSession(token, out _, out _), Is.False);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
