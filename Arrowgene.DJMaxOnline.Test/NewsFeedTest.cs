using Arrowgene.DJMaxOnline.Updater;

namespace Arrowgene.DJMaxOnline.Test;

[TestFixture]
public class NewsFeedTest
{
    [Test]
    public void BodyLineBreaksAreRetained()
    {
        IReadOnlyList<NewsItem> items = NewsFeed.Parse(
            "Server online\r\n[2026-08-18]\r\n" +
            "New courses are live.\r\n" +
            "Windowed mode is available.");

        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items[0].Body,
                Is.EqualTo("New courses are live.\nWindowed mode is available."));
        });
    }
}
