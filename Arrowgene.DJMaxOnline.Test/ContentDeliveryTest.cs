using System.Net;
using Arrowgene.DJMaxOnline.Server;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

[TestFixture]
public class ContentDeliveryTest
{
    private static Setting Http(string baseUrl)
    {
        Setting setting = new()
        {
            ContentDelivery = ContentDeliveryMode.Http,
            ContentBaseUrl = baseUrl,
            ContentSongPath = "song/",
            ContentPatchPath = "patch/"
        };
        return setting;
    }

    [Test]
    public void FtpModeKeepsTheConfiguredDownloadUrl()
    {
        Setting setting = new()
        {
            ContentDelivery = ContentDeliveryMode.Ftp,
            DownloadUrl = "ftp://DJMAX:DJMAX@127.0.0.1/song/"
        };

        Assert.That(ContentDelivery.SongUrl(setting),
            Is.EqualTo("ftp://DJMAX:DJMAX@127.0.0.1/song/"));
        // FTP mode has no launcher update route.
        Assert.That(ContentDelivery.PatchUrl(setting), Is.Null);
    }

    [Test]
    public void HttpModeBuildsBothRoutesFromOneBase()
    {
        // One host, two routes - that is the whole content surface.
        Setting setting = Http("https://updates.example.com/");

        Assert.That(ContentDelivery.SongUrl(setting),
            Is.EqualTo("https://updates.example.com/song/"));
        Assert.That(ContentDelivery.PatchUrl(setting),
            Is.EqualTo("https://updates.example.com/patch/"));
    }

    [TestCase("https://example.com", "https://example.com/song/")]
    [TestCase("https://example.com/", "https://example.com/song/")]
    [TestCase("https://example.com/djmax", "https://example.com/djmax/song/")]
    [TestCase("  https://example.com/  ", "https://example.com/song/")]
    public void BaseUrlIsNormalised(string baseUrl, string expected)
    {
        Assert.That(ContentDelivery.SongUrl(Http(baseUrl)), Is.EqualTo(expected));
    }

    [Test]
    public void LeadingSlashOnTheFolderDoesNotMakeItAbsolute()
    {
        Setting setting = Http("https://example.com/djmax/");
        setting.ContentSongPath = "/song";

        Assert.That(ContentDelivery.SongUrl(setting),
            Is.EqualTo("https://example.com/djmax/song/"));
    }

    [Test]
    public void HttpModeDefaultsAreUsableOutOfTheBox()
    {
        Setting setting = new() { ContentDelivery = ContentDeliveryMode.Http };

        // The shipped defaults have to compose into something the client can fetch.
        Assert.That(ContentDelivery.SongUrl(setting), Does.StartWith("http://"));
        Assert.That(ContentDelivery.SongUrl(setting), Does.EndWith("/song/"));
        Assert.That(ContentDelivery.PatchUrl(setting), Does.EndWith("/patch/"));
    }

    [Test]
    public void LocalBaseUrlFollowsTheListenerSettings()
    {
        Setting setting = new()
        {
            HttpContentListenIpAddress = IPAddress.Loopback,
            HttpContentPort = 9123
        };

        Assert.That(ContentDelivery.LocalBaseUrl(setting), Is.EqualTo("http://127.0.0.1:9123/"));
    }
}

[TestFixture]
public class LocalContentServerTest
{
    private static int NextPort() => Random.Shared.Next(41000, 46000);

    private static (Setting Setting, TemporaryDirectory Songs, TemporaryDirectory Paks)
        Configure(int port)
    {
        TemporaryDirectory songs = new();
        TemporaryDirectory paks = new();
        Setting setting = new()
        {
            ContentDelivery = ContentDeliveryMode.Http,
            HttpContentEnabled = true,
            HttpContentListenIpAddress = IPAddress.Loopback,
            HttpContentPort = (ushort)port,
            ContentSongPath = "song/",
            ContentPatchPath = "patch/",
            PakDirectory = songs.Path,
            PatchDirectory = paks.Path,
            PatchManifestAutoBuild = true,
            PatchManifestFileName = "md5list.txt",
            PatchNewsFileName = "news.txt"
        };
        return (setting, songs, paks);
    }

    [Test]
    public async Task ServesSongsAndPatchesFromOneHost()
    {
        int port = NextPort();
        (Setting setting, TemporaryDirectory songs, TemporaryDirectory paks) = Configure(port);
        using (songs)
        using (paks)
        {
            File.WriteAllText(Path.Combine(songs.Path, "example.pak"), "song payload");
            // crc.pak lives at the ROOT of the patch folder because that is where it sits
            // in the game folder - no route of its own.
            File.WriteAllText(Path.Combine(paks.Path, "crc.pak"), "crc payload");

            using LocalContentServer server = new(setting);
            server.Start();
            using HttpClient client = new();

            Assert.That(
                await client.GetStringAsync($"http://127.0.0.1:{port}/song/example.pak"),
                Is.EqualTo("song payload"));
            Assert.That(
                await client.GetStringAsync($"http://127.0.0.1:{port}/patch/crc.pak"),
                Is.EqualTo("crc payload"));
        }
    }

    /// <summary>
    /// The client probes the size with HttpQueryInfo(CONTENT_LENGTH) before reading, so a
    /// correct Content-Length on a HEAD is what makes the download start at all.
    /// </summary>
    [Test]
    public async Task ReportsContentLengthForTheSizeProbe()
    {
        int port = NextPort();
        (Setting setting, TemporaryDirectory songs, TemporaryDirectory paks) = Configure(port);
        using (songs)
        using (paks)
        {
            File.WriteAllBytes(Path.Combine(songs.Path, "sized.pak"), new byte[4096]);

            using LocalContentServer server = new(setting);
            server.Start();
            using HttpClient client = new();
            using HttpRequestMessage request = new(
                HttpMethod.Head, $"http://127.0.0.1:{port}/song/sized.pak");
            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.That(response.IsSuccessStatusCode, Is.True);
            Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(4096));
        }
    }

    /// <summary>InternetSetFilePointer becomes a ranged re-request under WinINet.</summary>
    [Test]
    public async Task HonoursRangeRequests()
    {
        int port = NextPort();
        (Setting setting, TemporaryDirectory songs, TemporaryDirectory paks) = Configure(port);
        using (songs)
        using (paks)
        {
            File.WriteAllText(Path.Combine(songs.Path, "ranged.pak"), "0123456789");

            using LocalContentServer server = new(setting);
            server.Start();
            using HttpClient client = new();
            using HttpRequestMessage request = new(
                HttpMethod.Get, $"http://127.0.0.1:{port}/song/ranged.pak");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(3, 6);
            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("3456"));
        }
    }

    [TestCase("/song/../../secret.txt")]
    [TestCase("/song/nested/deep.pak")]
    [TestCase("/other/example.pak")]
    [TestCase("/song/")]
    public async Task RefusesAnythingOutsideTheTwoFolders(string path)
    {
        int port = NextPort();
        (Setting setting, TemporaryDirectory songs, TemporaryDirectory paks) = Configure(port);
        using (songs)
        using (paks)
        {
            File.WriteAllText(Path.Combine(songs.Path, "example.pak"), "song payload");

            using LocalContentServer server = new(setting);
            server.Start();
            using HttpClient client = new();
            using HttpResponseMessage response =
                await client.GetAsync($"http://127.0.0.1:{port}{path}");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }

    [Test]
    public void StaysOffWhenDisabled()
    {
        int port = NextPort();
        (Setting setting, TemporaryDirectory songs, TemporaryDirectory paks) = Configure(port);
        using (songs)
        using (paks)
        {
            setting.HttpContentEnabled = false;

            using LocalContentServer server = new(setting);
            server.Start();

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            Assert.That(
                async () => await client.GetAsync($"http://127.0.0.1:{port}/song/example.pak"),
                Throws.InstanceOf<HttpRequestException>());
        }
    }
}

[TestFixture]
public class DownloadUrlPacketTest
{
    /// <summary>
    /// The setting the client is actually handed. This used to demand ftp:// and threw
    /// inside LogInReqHandler the moment HTTP delivery was switched on, which aborted the
    /// login and made every chart fail to start.
    /// </summary>
    [TestCase("ftp://DJMAX:DJMAX@127.0.0.1/song/")]
    [TestCase("http://127.0.0.1:8080/song/")]
    [TestCase("https://updates.example.com/song/")]
    public void AcceptsEverySchemeTheClientDownloaderSupports(string url)
    {
        Server.Packets.EnvironmentSetting setting =
            Server.Packets.OnEnvironmentInfPacket.DownloadUrl(url);

        Assert.That(setting.Name, Is.EqualTo("DOWNLOADURL"));
        Assert.That(setting.Value, Is.EqualTo(url));
    }

    [TestCase("song/")]
    [TestCase("file:///C:/songs/")]
    [TestCase("")]
    public void RejectsAnythingTheDownloaderCannotFetch(string url)
    {
        Assert.That(() => Server.Packets.OnEnvironmentInfPacket.DownloadUrl(url),
            Throws.InstanceOf<ArgumentException>());
    }

    /// <summary>The URL the server actually sends must survive that validation.</summary>
    [Test]
    public void TheHttpUrlTheServerSendsIsAccepted()
    {
        Setting setting = new()
        {
            ContentDelivery = ContentDeliveryMode.Http,
            ContentBaseUrl = "http://127.0.0.1:8080/",
            ContentSongPath = "song/"
        };

        Assert.That(
            () => Server.Packets.OnEnvironmentInfPacket.DownloadUrl(
                ContentDelivery.SongUrl(setting)),
            Throws.Nothing);
    }
}
