using System.Collections.Concurrent;

namespace Arrowgene.DJMaxOnline.Server;

/// <summary>
/// One occasion where the chart the server sent was not the chart that was asked for.
/// </summary>
/// <param name="Requested">The difficulty the player selected, e.g. "MX".</param>
/// <param name="Served">
/// The difficulty of the file that actually went out, read back from its name, or
/// "unnamed" when the file carries no difficulty at all and the server cannot tell.
/// </param>
public sealed record ChartServeIssue(
    long IssueId,
    string OccurredUtc,
    uint DiscId,
    string Tag,
    int KeyMode,
    string Requested,
    string Served,
    string FileName);

/// <summary>
/// A small in-memory record of wrong-difficulty chart serves, for the status API.
///
/// The provider FALLS BACK rather than failing when the requested difficulty has no
/// chart, so the player is otherwise the first to notice they got the wrong one. This
/// keeps the last few hundred occurrences so a bot can surface them instead.
///
/// Deliberately NOT in the database: it is diagnostic, it must never slow down a chart
/// load, and it is worthless once the missing chart is added.
/// </summary>
public static class ChartServeIssues
{
    /// <summary>Kept small - this is a live diagnostic, not a history.</summary>
    private const int Capacity = 200;

    private static readonly ConcurrentQueue<ChartServeIssue> Issues = new();
    private static long _nextId;

    public static long LatestIssueId => Interlocked.Read(ref _nextId);

    public static void Record(
        uint discId, string tag, int keyMode, string requested, string served, string fileName)
    {
        long id = Interlocked.Increment(ref _nextId);
        Issues.Enqueue(new ChartServeIssue(
            id,
            DateTimeOffset.UtcNow.ToString("O"),
            discId,
            tag ?? string.Empty,
            keyMode,
            requested ?? string.Empty,
            served ?? string.Empty,
            fileName ?? string.Empty));
        while (Issues.Count > Capacity && Issues.TryDequeue(out _))
        {
        }
    }

    /// <summary>Issues newer than <paramref name="afterIssueId"/>, oldest first.</summary>
    public static IReadOnlyList<ChartServeIssue> Recent(long afterIssueId, int limit)
    {
        int take = limit <= 0 ? 25 : Math.Min(limit, Capacity);
        return [.. Issues
            .Where(issue => issue.IssueId > afterIssueId)
            .OrderBy(issue => issue.IssueId)
            .Take(take)];
    }
}
