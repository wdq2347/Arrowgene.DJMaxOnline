using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// No account is loaded until a player redeems their own launcher ticket. These pin down
/// that the server holds no player at startup, that there is no stand-in identity, and
/// that a ticket always resolves to that ticket's own account.
/// </summary>
[TestFixture]
public class AccountIsolationTest
{
    private static ShopCatalog Shop() => ShopCatalog.Load(
        ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
        throw new DirectoryNotFoundException("Test shop DATA was not found."));

    [Test]
    public void NoAccountIsLoadedUntilATicketIsRedeemed()
    {
        using TemporaryDirectory directory = new();
        SqlitePlayerRepository repository =
            new(Path.Combine(directory.Path, "players.sqlite3"));
        repository.Create("ADMIN", "Admin", gender: 0);

        // This is how DjMaxServer builds the resolver now: no fallback store at all.
        LoginTicketService tickets = new(TimeSpan.FromSeconds(30));
        LocalAccountResolver resolver = new(null, Shop(), repository, tickets);

        Assert.That(resolver.TryResolveTicket("not-a-ticket", out LocalPlayerStore? store, out _),
            Is.False);
        Assert.That(store, Is.Null, "an unauthenticated caller gets no player at all");
    }

    [Test]
    public void ATicketResolvesToItsOwnAccount()
    {
        using TemporaryDirectory directory = new();
        SqlitePlayerRepository repository =
            new(Path.Combine(directory.Path, "players.sqlite3"));
        LocalPlayerProfile admin = repository.Create("ADMIN", "Admin", gender: 0);
        LocalPlayerProfile other = repository.Create("PLAYER2", "Player Two", gender: 0);

        LoginTicketService tickets = new(TimeSpan.FromSeconds(30));
        LocalAccountResolver resolver = new(null, Shop(), repository, tickets);

        Assert.That(
            resolver.TryResolveTicket(tickets.Issue(other.UserId),
                out LocalPlayerStore? resolved, out _), Is.True);
        Assert.That(resolved!.Profile.AccountId, Is.EqualTo("PLAYER2"));
        Assert.That(resolved.Profile.UserId, Is.Not.EqualTo(admin.UserId));
    }

    /// <summary>There is no guest, placeholder or default identity to fall back on.</summary>
    [Test]
    public void ThereIsNoStandInIdentity()
    {
        Assert.That(typeof(LocalPlayerProfile).GetProperty("Guest"), Is.Null,
            "a guest profile must not exist");
        Assert.That(typeof(LocalPlayerProfile).GetProperty("IsGuest"), Is.Null);
    }

}
