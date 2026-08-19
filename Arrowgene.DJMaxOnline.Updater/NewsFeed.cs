namespace Arrowgene.DJMaxOnline.Updater;

/// <summary>One news item as the launcher shows it.</summary>
public sealed record NewsItem(string Headline, string Date, string Body);

/// <summary>
/// The launcher's news, published on the update site beside the checksum list.
///
/// It is fetched over the same transport as the patch files, so there is one address to
/// configure and one thing to keep online. It is deliberately NOT part of the checksum
/// list: a manifest entry is a file to install into the player's game folder, and the news
/// belongs on the site.
///
/// Format - blank-line separated blocks, matching the news.txt the launcher used to read
/// from disk, so an existing file can be uploaded as-is:
/// <code>
/// Server online
/// [2026-08-11]
/// SEOUL and TOKYO are up.
///
/// Course mode
/// Available
/// Course rankings are recorded per key mode.
/// </code>
/// Line 1 is the headline, line 2 the date (surrounding brackets are stripped), and the
/// rest is the body. Its line breaks are retained so a news card can use one line per
/// change rather than squeezing the whole announcement into one paragraph.
///
/// This is REMOTE TEXT: it is displayed, never executed, never written to disk, and never
/// used to name a file. The only bound worth enforcing is size, so a broken or hostile
/// site cannot make the launcher chew through memory.
/// </summary>
public static class NewsFeed
{
    public const string DefaultFileName = "news.txt";

    /// <summary>Plenty for a news card; anything larger is not news.</summary>
    public const int MaximumLength = 64 * 1024;

    /// <summary>Nobody scrolls past this, and it caps the work done on a bad feed.</summary>
    public const int MaximumItems = 20;

    public static IReadOnlyList<NewsItem> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.Length > MaximumLength)
        {
            text = text[..MaximumLength];
        }

        List<NewsItem> items = [];
        foreach (string block in text.Replace("\r\n", "\n").Replace('\r', '\n')
                     .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (items.Count >= MaximumItems)
            {
                break;
            }

            string[] lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                // A stray line on its own is not an item; skip it rather than inventing
                // a headline with no date or body.
                continue;
            }

            string headline = lines[0].Trim();
            if (headline.Length == 0 || headline[0] is '#' or ';')
            {
                continue;
            }

            items.Add(new NewsItem(
                headline,
                lines[1].Trim().Trim('[', ']').Trim(),
                string.Join('\n', lines[2..].Select(line => line.Trim())).Trim()));
        }

        return items;
    }

    /// <summary>
    /// Fetches and parses the news. Returns an empty list when the site has no news file -
    /// news being unavailable is not an error worth blocking a login over, so the caller
    /// falls back to whatever it wants to show instead.
    /// </summary>
    public static async Task<IReadOnlyList<NewsItem>> FetchAsync(
        IUpdateTransport transport,
        string fileName = DefaultFileName,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return [];
        }

        return Parse(await transport.ReadTextAsync(fileName, cancellation));
    }
}
