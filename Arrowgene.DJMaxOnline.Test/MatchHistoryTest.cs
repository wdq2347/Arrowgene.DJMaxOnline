using System.Net;
using System.Text.Json;
using Arrowgene.DJMaxOnline.Server.Korea400;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Match history is what lets a caller ask "who won", which no score row can answer.
/// These pin the cursor behaviour the poller depends on, the bound that keeps a long
/// running server from growing forever, and - as with every other published record - that
/// nothing account-shaped can come out of the endpoint.
/// </summary>
[TestFixture]
public class MatchHistoryTest
{
    /// <summary>
    /// A free port from the OS. Picking one at random risks landing in the ephemeral
    /// range (49152+), which Windows also hands to ordinary outbound connections.
    /// </summary>
    private static int NextPort()
    {
        using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static MatchStatus Match(long id, params MatchPlayerStatus[] players) => new(
        MatchId: id,
        FinishedUtc: DateTime.UtcNow.ToString("O"),
        Channel: "SEOUL",
        RoomIndex: 3,
        RoomTitle: "come play",
        KeyMode: 7,
        MatchMode: 2,
        GameType: 0,
        TeamBattle: false,
        Ranked: true,
        SongId: 220,
        CourseId: null,
        Players: players);

    private static MatchPlayerStatus Player(
        string nickname, byte slot, int placement, uint score, byte team = 0) => new(
        Nickname: nickname,
        Slot: slot,
        Team: team,
        Placement: placement,
        Winner: placement == 0,
        Failed: false,
        Score: score,
        Accuracy: 95.5,
        MaxCombo: 700,
        Breaks: 2,
        FullCombo: false,
        IsBot: false);

    [Test]
    public void RecordHandsOutIncreasingIdsStartingAtOne()
    {
        MatchHistory history = new();
        Assert.That(history.LatestMatchId, Is.EqualTo(0));

        MatchStatus first = history.Record(id => Match(id, Player("Blade", 0, 0, 200)));
        MatchStatus second = history.Record(id => Match(id, Player("Blade", 0, 0, 300)));

        Assert.Multiple(() =>
        {
            Assert.That(first.MatchId, Is.EqualTo(1));
            Assert.That(second.MatchId, Is.EqualTo(2));
            Assert.That(history.LatestMatchId, Is.EqualTo(2));
        });
    }

    [Test]
    public void RecentReturnsOnlyMatchesAfterTheCursorOldestFirst()
    {
        MatchHistory history = new();
        for (int index = 0; index < 5; index++)
        {
            history.Record(id => Match(id, Player("Blade", 0, 0, 200)));
        }

        IReadOnlyList<MatchStatus> page = history.Recent(afterMatchId: 2, limit: 25);

        Assert.That(page.Select(match => match.MatchId), Is.EqualTo(new long[] { 3, 4, 5 }));
    }

    [Test]
    public void RecentClampsTheRequestedLimit()
    {
        MatchHistory history = new();
        for (int index = 0; index < 150; index++)
        {
            history.Record(id => Match(id, Player("Blade", 0, 0, 200)));
        }

        Assert.Multiple(() =>
        {
            // Zero means "unspecified", not "none".
            Assert.That(history.Recent(0, 0), Has.Count.EqualTo(25));
            Assert.That(history.Recent(0, 10_000), Has.Count.EqualTo(100));
        });
    }

    [Test]
    public void OldestMatchesAreDroppedOnceTheBufferIsFull()
    {
        MatchHistory history = new(capacity: 3);
        for (int index = 0; index < 5; index++)
        {
            history.Record(id => Match(id, Player("Blade", 0, 0, 200)));
        }

        // Ids keep counting even though the early matches are gone, so a poller that was
        // behind skips forward rather than replaying.
        Assert.That(
            history.Recent(0, 25).Select(match => match.MatchId),
            Is.EqualTo(new long[] { 3, 4, 5 }));
    }

    [Test]
    public void EndpointPublishesPlacementsAndNothingAccountShaped()
    {
        int port = NextPort();
        MatchHistory history = new();
        history.Record(id => Match(
            id,
            Player("Blade", 0, 0, 241_276),
            Player("Rival", 1, 1, 180_002)));

        using LocalStatusApi api = new(
            new Setting
            {
                StatusApiEnabled = true,
                StatusApiListenIpAddress = IPAddress.Loopback,
                StatusApiPort = (ushort)port,
                StatusApiToken = string.Empty
            },
            () => [],
            scores: null,
            matches: history);
        api.Start();

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        string body = client
            .GetStringAsync($"http://127.0.0.1:{port}/matches/recent")
            .GetAwaiter()
            .GetResult();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement match = document.RootElement.GetProperty("matches")[0];
        JsonElement players = match.GetProperty("players");

        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("latestMatchId").GetInt64(),
                Is.EqualTo(1));
            Assert.That(players[0].GetProperty("nickname").GetString(), Is.EqualTo("Blade"));
            Assert.That(players[0].GetProperty("winner").GetBoolean(), Is.True);
            Assert.That(players[1].GetProperty("winner").GetBoolean(), Is.False);
            Assert.That(players[1].GetProperty("placement").GetInt32(), Is.EqualTo(1));
            Assert.That(match.GetProperty("songId").GetUInt32(), Is.EqualTo(220));

            foreach (string forbidden in
                     new[] { "accountId", "account_id", "password", "ticket", "secondaryId" })
            {
                Assert.That(body, Does.Not.Contain(forbidden),
                    $"The match feed must never publish {forbidden}.");
            }
        });
    }

    [Test]
    public void EndpointAnswersEmptyWhenNoHistoryIsAttached()
    {
        int port = NextPort();
        using LocalStatusApi api = new(
            new Setting
            {
                StatusApiEnabled = true,
                StatusApiListenIpAddress = IPAddress.Loopback,
                StatusApiPort = (ushort)port,
                StatusApiToken = string.Empty
            },
            () => []);
        api.Start();

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        string body = client
            .GetStringAsync($"http://127.0.0.1:{port}/matches/recent")
            .GetAwaiter()
            .GetResult();

        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("matches").GetArrayLength(),
                Is.EqualTo(0));
            Assert.That(document.RootElement.GetProperty("latestMatchId").GetInt64(),
                Is.EqualTo(0));
        });
    }
}
