using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Arrowgene.DJMaxOnline.Test;

[NonParallelizable]
public class ServerAdministrationTest
{
    private string _directory = null!;
    private PasswordPolicySetting _originalPasswordPolicy = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), $"djmax-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _originalPasswordPolicy = new PasswordPolicySetting
        {
            Iterations = PasswordSecurity.Iterations,
            SaltSize = PasswordSecurity.SaltSize,
            MinimumLength = PasswordSecurity.MinimumLength,
            MaximumLength = PasswordSecurity.MaximumLength
        };
        PasswordSecurity.Configure(new PasswordPolicySetting
        {
            Iterations = 100_000,
            SaltSize = 16,
            MinimumLength = 10,
            MaximumLength = 128
        });
    }

    [TearDown]
    public void TearDown()
    {
        PasswordSecurity.Configure(_originalPasswordPolicy);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void CreatesAndResetsAccountsWhileRepositoryRemainsOpen()
    {
        SqlitePlayerRepository repository = new(
            Path.Combine(_directory, "players.sqlite3"));
        ServerAdministrationService administration = new(
            repository, new ClientLookup());

        LocalPlayerProfile created = administration.CreateAccount(
            "production-user", "Blade", "first-password", gender: 1);
        Assert.That(repository.TryGetPasswordCredential(
            created.AccountId, out PlayerPasswordCredential? first), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(created.UserId, Is.EqualTo(1u));
            Assert.That(created.Gender, Is.EqualTo(1));
            Assert.That(administration.ListAccounts(), Has.Count.EqualTo(1));
            Assert.That(first, Is.Not.Null);
            Assert.That(PasswordSecurity.Verify("first-password", first!), Is.True);
            Assert.That(
                () => administration.CreateAccount(
                    "blade", "Somebody", "another-password", gender: 0),
                Throws.InvalidOperationException,
                "new account ids must not collide with an existing nickname");
        });

        LocalPlayerProfile updated = administration.SetPassword(
            "production-user", "second-password");
        Assert.That(repository.TryGetPasswordCredential(
            updated.AccountId, out PlayerPasswordCredential? second), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Not.Null);
            Assert.That(PasswordSecurity.Verify("second-password", second!), Is.True);
            Assert.That(PasswordSecurity.Verify("first-password", second!), Is.False);
            Assert.That(administration.ListAccounts(), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void AnnouncementMethodsAreSafeWithNoPlayersOnline()
    {
        SqlitePlayerRepository repository = new(
            Path.Combine(_directory, "players.sqlite3"));
        ServerAdministrationService administration = new(
            repository, new ClientLookup());

        Assert.Multiple(() =>
        {
            Assert.That(
                administration.BroadcastChat("Maintenance in ten minutes"),
                Is.EqualTo(new AdministrationBroadcastResult(0, 0)));
            Assert.That(
                administration.BroadcastChat("Important", ChatMessageType.Alert),
                Is.EqualTo(new AdministrationBroadcastResult(0, 0)));
            Assert.That(
                administration.BroadcastBigNews("Server", "Welcome"),
                Is.EqualTo(new AdministrationBroadcastResult(0, 0)));
            Assert.That(
                () => administration.BroadcastChat("No emoji 🙂"),
                Throws.ArgumentException);
            Assert.That(
                () => administration.BroadcastBigNews("한국", "Body"),
                Throws.ArgumentException);
            Assert.That(
                () => administration.BroadcastBigNews("", ""),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void ShutdownAnnouncementsPreserveReasonAndFitClientPackets()
    {
        const string reason = "Database maintenance and weekly backup";
        string normalized = ShutdownAnnouncementPolicy.NormalizeReason($"  {reason}  ");

        Assert.Multiple(() =>
        {
            Assert.That(normalized, Is.EqualTo(reason));
            Assert.That(
                ShutdownAnnouncementPolicy.NormalizeReason(string.Empty),
                Is.EqualTo("Scheduled"));
            Assert.That(
                ShutdownAnnouncementPolicy.Initial(normalized),
                Does.Contain(reason));
            Assert.That(
                ShutdownAnnouncementPolicy.Waiting(1, normalized),
                Does.Contain("1 active player"));
            Assert.That(
                () => ShutdownAnnouncementPolicy.NormalizeReason("Bad\nreason"),
                Throws.ArgumentException);
            Assert.That(
                () => ShutdownAnnouncementPolicy.NormalizeReason("Non-ASCII \u2603"),
                Throws.ArgumentException);
            Assert.That(
                () => ShutdownAnnouncementPolicy.NormalizeReason(
                    new string('x', ShutdownAnnouncementPolicy.MaximumReasonLength + 1)),
                Throws.ArgumentException);
        });

        IEnumerable<string> messages =
        [
            ShutdownAnnouncementPolicy.Initial(normalized),
            ShutdownAnnouncementPolicy.Waiting(6, normalized),
            .. Enumerable.Range(1, 10)
                .Select(seconds => ShutdownAnnouncementPolicy.Countdown(seconds, normalized)),
            ShutdownAnnouncementPolicy.Now(normalized)
        ];
        Assert.That(messages.All(message =>
            Encoding.ASCII.GetByteCount(message) <= ChatInfPacket.MaximumTextLength),
            Is.True);

        string maximumReason = new('x', ShutdownAnnouncementPolicy.MaximumReasonLength);
        Assert.That(
            Encoding.ASCII.GetByteCount(
                ShutdownAnnouncementPolicy.Initial(maximumReason)),
            Is.LessThanOrEqualTo(ChatInfPacket.MaximumTextLength));
    }
}
