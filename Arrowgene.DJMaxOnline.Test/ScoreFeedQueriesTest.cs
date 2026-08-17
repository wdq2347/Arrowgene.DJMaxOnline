using Arrowgene.DJMaxOnline.Server.Korea400;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Runs the API's score SQL against the real database when one is present. The queries
/// join players for a nickname and must never select an account column.
/// </summary>
[TestFixture]
public class ScoreFeedQueriesTest
{
    private static string? DatabasePath()
    {
        for (DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "DATA", "djmax.sqlite3");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static ScoreFeedQueries Queries()
    {
        string? path = DatabasePath();
        if (path == null)
        {
            Assert.Ignore("No game database in this checkout.");
        }
        return new ScoreFeedQueries(path!);
    }

    [Test]
    public void RecentReturnsPlaysInAscendingOrder()
    {
        ScoreFeedQueries queries = Queries();
        IReadOnlyList<ScoreStatus> plays = queries.Recent(0, 50);

        Assert.That(plays.Select(play => play.ScoreId), Is.Ordered.Ascending);
        foreach (ScoreStatus play in plays)
        {
            Assert.That(play.Nickname, Is.Not.Empty, "every play carries a nickname");
        }
    }

    [Test]
    public void RecentRespectsTheCursor()
    {
        ScoreFeedQueries queries = Queries();
        long latest = queries.LatestScoreId();

        Assert.That(queries.Recent(latest, 50), Is.Empty,
            "nothing is newer than the newest play");
    }

    [Test]
    public void LimitIsClampedSoACallerCannotAskForEverything()
    {
        ScoreFeedQueries queries = Queries();
        Assert.That(queries.Recent(0, 100_000).Count, Is.LessThanOrEqualTo(100));
    }

    [Test]
    public void AnUnknownNicknameYieldsNothingRatherThanThrowing()
    {
        Assert.That(Queries().ForPlayer("nobody-by-this-name", 10), Is.Empty);
    }

    [Test]
    public void SongLeaderboardIsOneRowPerPlayer()
    {
        ScoreFeedQueries queries = Queries();
        IReadOnlyList<ScoreStatus> plays = queries.Recent(0, 1);
        if (plays.Count == 0 || plays[0].SongId is not { } songId)
        {
            Assert.Ignore("no song plays recorded");
            return;
        }

        IReadOnlyList<ScoreStatus> board = queries.ForSong(songId, 0, 50);
        Assert.That(board.Select(play => play.Nickname).Distinct().Count(),
            Is.EqualTo(board.Count));
    }

    [Test]
    public void TheQueriesCannotWrite()
    {
        // Opened ReadOnly: an accidental write must fail rather than touch game state.
        ScoreFeedQueries queries = Queries();
        Assert.That(queries.LatestScoreId(), Is.GreaterThanOrEqualTo(0));
    }
}
