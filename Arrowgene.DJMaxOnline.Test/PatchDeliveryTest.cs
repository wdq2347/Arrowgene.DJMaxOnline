using System.Net;
using System.Security.Cryptography;
using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Updater;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The patch pipeline end to end: the server publishes a checksum list for what is in its
/// patch folder, serves those files over HTTP, and the launcher's updater downloads and
/// verifies them.
///
/// These deliberately drive BOTH halves together. Testing the server's routing and the
/// updater's downloader separately proves each is self-consistent and says nothing about
/// whether they agree - which is exactly where this kind of thing breaks.
/// </summary>
[TestFixture]
public class PatchDeliveryTest
{
    /// <summary>
    /// Asks the OS for a free port rather than picking one.
    ///
    /// Both earlier attempts were wrong for the same reason: 49152-65535 is Windows'
    /// EPHEMERAL range, handed out to ordinary outbound connections, so any fixed or
    /// random choice in there eventually lands on a port something else already took.
    /// Binding port 0 makes the OS name one it knows is free.
    /// </summary>
    private static int TakePort()
    {
        using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string _root = string.Empty;
    private string _patch = string.Empty;
    private string _game = string.Empty;
    private int _port;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "djmax-patch-" + Guid.NewGuid().ToString("N"));
        _patch = Path.Combine(_root, "patch");
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(_patch, "System"));
        Directory.CreateDirectory(_game);
        _port = TakePort();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open by a failed test is not worth failing the run over.
        }
    }

    private Setting Configure() => new()
    {
        HttpContentEnabled = true,
        HttpContentListenIpAddress = IPAddress.Loopback,
        HttpContentPort = (ushort)_port,
        ContentSongPath = "song/",
        ContentPatchPath = "patch/",
        PatchDirectory = _patch,
        PakDirectory = Path.Combine(_root, "paks"),
        PatchManifestAutoBuild = true,
        PatchManifestFileName = "md5list.txt",
        PatchNewsFileName = "news.txt"
    };

    private void WritePatchFile(string relative, string content)
    {
        string path = Path.Combine(_patch, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Md5(string content) =>
        Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    private static async Task<HttpResponseMessage> GetAsync(int port, string path)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        return await client.GetAsync($"http://127.0.0.1:{port}{path}");
    }

    // ------------------------------------------------------------------ manifest

    [Test]
    public void StartupPublishesAChecksumListForThePatchFiles()
    {
        WritePatchFile("System/system_0001.pak", "patch payload");
        WritePatchFile("System/system.crc", "crc payload");

        using LocalContentServer server = new(Configure());
        server.Start();

        string manifest = File.ReadAllText(Path.Combine(_patch, "md5list.txt"));
        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("System/system_0001.pak"));
            Assert.That(manifest, Does.Contain("System/system.crc"));
            Assert.That(manifest, Does.Contain(Md5("patch payload")));
            // Forward slashes, whatever the platform - the launcher joins them onto a URL.
            Assert.That(manifest, Does.Not.Contain(@"System\system"));
        });
    }

    [Test]
    public void TheNewsFileIsServedButNeverListedForDownload()
    {
        WritePatchFile("System/system_0001.pak", "patch payload");
        WritePatchFile("news.txt", "Server online\n[2026-08-11]\nWe are up.");

        using LocalContentServer server = new(Configure());
        server.Start();

        string manifest = File.ReadAllText(Path.Combine(_patch, "md5list.txt"));
        // Anything in the list gets written into the player's game folder. News must not.
        Assert.That(manifest, Does.Not.Contain("news.txt"));
    }

    [Test]
    public void TheManifestNeverListsItself()
    {
        WritePatchFile("System/system_0001.pak", "patch payload");
        File.WriteAllText(Path.Combine(_patch, "md5list.txt"), "stale content");

        using LocalContentServer server = new(Configure());
        server.Start();

        Assert.That(File.ReadAllText(Path.Combine(_patch, "md5list.txt")),
            Does.Not.Contain("md5list.txt"));
    }

    [Test]
    public void RepublishingPicksUpAReplacedPatchFile()
    {
        WritePatchFile("System/system_0001.pak", "first");
        using (LocalContentServer first = new(Configure()))
        {
            first.Start();
        }
        WritePatchFile("System/system_0001.pak", "second");

        _port = TakePort();
        using LocalContentServer second = new(Configure());
        second.Start();

        string manifest = File.ReadAllText(Path.Combine(_patch, "md5list.txt"));
        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain(Md5("second")));
            Assert.That(manifest, Does.Not.Contain(Md5("first")));
        });
    }

    [Test]
    public void SiteBookkeepingFilesAreNeverListed()
    {
        // Found the hard way: a README documenting the patch folder was published, so
        // every player would have had it installed into their game directory.
        WritePatchFile("System/system_0001.pak", "patch payload");
        WritePatchFile("README.md", "how to use this folder");
        WritePatchFile("Thumbs.db", "windows junk");
        WritePatchFile(".gitkeep", "");
        WritePatchFile("System/system_0002.pak.part", "half a download");

        using LocalContentServer server = new(Configure());
        server.Start();

        string manifest = File.ReadAllText(Path.Combine(_patch, "md5list.txt"));
        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("System/system_0001.pak"));
            foreach (string junk in new[] { "README.md", "Thumbs.db", ".gitkeep", ".part" })
            {
                Assert.That(manifest, Does.Not.Contain(junk));
            }
        });
    }

    [Test]
    public void SongArchivesAreServedButNeverChecksummed()
    {
        // Songs live in the game folder like everything else, but the CLIENT fetches them
        // in game when a chart is picked. Listing them would make the launcher hash and
        // download the entire music library before anyone could log in.
        WritePatchFile("crc.pak", "crc payload");
        WritePatchFile("song/example.pak", "song payload");
        WritePatchFile("song/another.pak", "more song payload");

        using LocalContentServer server = new(Configure());
        server.Start();

        string manifest = File.ReadAllText(Path.Combine(_patch, "md5list.txt"));
        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("crc.pak"));
            Assert.That(manifest, Does.Not.Contain("song/"));
            Assert.That(manifest, Does.Not.Contain("example.pak"));
        });
    }

    [Test]
    public async Task ASongInThePatchFolderIsStillDownloadable()
    {
        // Excluded from the checksum list, but still served - the client asks for it.
        WritePatchFile("song/example.pak", "song payload");

        using LocalContentServer server = new(Configure());
        server.Start();

        using HttpResponseMessage response = await GetAsync(_port, "/patch/song/example.pak");
        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("song payload"));
        });
    }

    // ------------------------------------------------------------------- serving

    [Test]
    public async Task NestedPatchPathsAreServed()
    {
        WritePatchFile("System/system_0001.pak", "patch payload");

        using LocalContentServer server = new(Configure());
        server.Start();

        using HttpResponseMessage response =
            await GetAsync(_port, "/patch/System/system_0001.pak");
        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(),
                Is.EqualTo("patch payload"));
        });
    }

    [Test]
    public async Task TheSongRouteStaysFlat()
    {
        // The client asks for a bare file name; nesting there would be a behaviour change
        // to the path the game itself uses.
        Setting setting = Configure();
        Directory.CreateDirectory(Path.Combine(setting.PakDirectory, "nested"));
        File.WriteAllText(
            Path.Combine(setting.PakDirectory, "nested", "song.pak"), "nope");

        using LocalContentServer server = new(setting);
        server.Start();

        using HttpResponseMessage response = await GetAsync(_port, "/song/nested/song.pak");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task TraversalOutOfThePatchFolderIsRefused()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "not yours");
        WritePatchFile("System/system_0001.pak", "patch payload");

        using LocalContentServer server = new(Configure());
        server.Start();

        foreach (string path in new[]
                 {
                     "/patch/../secret.txt",
                     "/patch/System/../../secret.txt",
                     "/patch/%2e%2e/secret.txt"
                 })
        {
            using HttpResponseMessage response = await GetAsync(_port, path);
            Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK), path);
        }
    }

    // ---------------------------------------------------------------- end to end

    [Test]
    public async Task TheLauncherUpdatesAndVerifiesAgainstTheRunningServer()
    {
        WritePatchFile("System/system_0001.pak", "patch payload");
        WritePatchFile("System/system.crc", "crc payload");

        using LocalContentServer server = new(Configure());
        server.Start();

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
        UpdateService service = new(
            UpdateService.CreateTransport($"http://127.0.0.1:{_port}/patch/", client));

        UpdateManifest manifest = await service.FetchManifestAsync(CancellationToken.None);
        UpdatePlan plan = UpdateService.Plan(manifest, _game);
        Assert.That(plan.Actions, Has.Count.EqualTo(2), "both files start missing");

        await service.ApplyAsync(plan, _game, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(
                File.ReadAllText(Path.Combine(_game, "System", "system_0001.pak")),
                Is.EqualTo("patch payload"));
            // Re-planning is the verification pass the launcher runs after downloading.
            Assert.That(UpdateService.Plan(manifest, _game).UpToDate, Is.True);
        });
    }

    [Test]
    public async Task ACorruptedLocalFileIsDetectedAndRepaired()
    {
        WritePatchFile("System/system_0001.pak", "patch payload");

        using LocalContentServer server = new(Configure());
        server.Start();

        Directory.CreateDirectory(Path.Combine(_game, "System"));
        File.WriteAllText(Path.Combine(_game, "System", "system_0001.pak"), "corrupted");

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
        UpdateService service = new(
            UpdateService.CreateTransport($"http://127.0.0.1:{_port}/patch/", client));
        UpdateManifest manifest = await service.FetchManifestAsync(CancellationToken.None);

        UpdatePlan plan = UpdateService.Plan(manifest, _game);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Actions, Has.Count.EqualTo(1));
            Assert.That(plan.Actions[0].Reason, Is.EqualTo(UpdateReason.Changed));
        });

        await service.ApplyAsync(plan, _game, null, CancellationToken.None);
        Assert.That(
            File.ReadAllText(Path.Combine(_game, "System", "system_0001.pak")),
            Is.EqualTo("patch payload"));
    }

    [Test]
    public async Task AFileThatDoesNotMatchItsPublishedHashIsRejected()
    {
        WritePatchFile("System/system_0001.pak", "patch payload");

        using LocalContentServer server = new(Configure());
        server.Start();

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
        UpdateService service = new(
            UpdateService.CreateTransport($"http://127.0.0.1:{_port}/patch/", client));
        UpdateManifest manifest = await service.FetchManifestAsync(CancellationToken.None);
        UpdatePlan plan = UpdateService.Plan(manifest, _game);

        // The served bytes change after the list was published - a mirror serving stale
        // content, or a tampered file. The hash check is the whole point.
        WritePatchFile("System/system_0001.pak", "something else entirely");

        InvalidDataException? failure = Assert.ThrowsAsync<InvalidDataException>(
            async () => await service.ApplyAsync(plan, _game, null, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(failure!.Message, Does.Contain("failed its checksum"));
            // Nothing half-written is left behind for the game to load.
            Assert.That(File.Exists(Path.Combine(_game, "System", "system_0001.pak")),
                Is.False);
            Assert.That(
                Directory.EnumerateFiles(_game, "*.part", SearchOption.AllDirectories),
                Is.Empty);
        });
    }

    [Test]
    public async Task TheNewsIsFetchedFromTheServer()
    {
        WritePatchFile(
            "news.txt",
            "Server online\n[2026-08-11]\nSEOUL and TOKYO are up.\nWindowed mode is available.\n\n" +
            "Course mode\nAvailable\nRankings are per key mode.");

        using LocalContentServer server = new(Configure());
        server.Start();

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
        IReadOnlyList<NewsItem> news = await NewsFeed.FetchAsync(
            UpdateService.CreateTransport($"http://127.0.0.1:{_port}/patch/", client));

        Assert.Multiple(() =>
        {
            Assert.That(news, Has.Count.EqualTo(2));
            Assert.That(news[0].Headline, Is.EqualTo("Server online"));
            Assert.That(news[0].Date, Is.EqualTo("2026-08-11"), "brackets are stripped");
            Assert.That(news[0].Body,
                Is.EqualTo("SEOUL and TOKYO are up.\nWindowed mode is available."));
            Assert.That(news[1].Headline, Is.EqualTo("Course mode"));
        });
    }
}
