using System.Security.Cryptography;
using System.Text;
using Arrowgene.DJMaxOnline.Updater;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

[TestFixture]
public class UpdateManifestTest
{
    private static string Md5(string content) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    [Test]
    public void ParsesHashAndPath()
    {
        UpdateManifest manifest = UpdateManifest.Parse(
            "# a comment\n" +
            "d41d8cd98f00b204e9800998ecf8427e  Song/example.pak\n" +
            "\n" +
            "D41D8CD98F00B204E9800998ECF8427E *System/other.pak\n");

        Assert.That(manifest.Entries, Has.Count.EqualTo(2));
        Assert.That(manifest.Entries[0].Path, Is.EqualTo("Song/example.pak"));
        Assert.That(manifest.Entries[1].Path, Is.EqualTo("System/other.pak"));
        // Casing is normalised so comparisons never have to care.
        Assert.That(manifest.Entries[1].Md5, Is.EqualTo("d41d8cd98f00b204e9800998ecf8427e"));
    }

    [Test]
    public void KeepsSpacesInPaths()
    {
        UpdateManifest manifest = UpdateManifest.Parse(
            "d41d8cd98f00b204e9800998ecf8427e  Song/my song.pak\n");

        Assert.That(manifest.Entries[0].Path, Is.EqualTo("Song/my song.pak"));
    }

    [Test]
    public void NormalisesBackslashes()
    {
        UpdateManifest manifest = UpdateManifest.Parse(
            "d41d8cd98f00b204e9800998ecf8427e  Song\\example.pak\n");

        Assert.That(manifest.Entries[0].Path, Is.EqualTo("Song/example.pak"));
    }

    [TestCase("../evil.dll")]
    [TestCase("Song/../../evil.dll")]
    [TestCase("/etc/passwd")]
    [TestCase("C:/Windows/System32/evil.dll")]
    [TestCase("\\\\server\\share\\evil.dll")]
    public void RejectsPathsThatEscapeTheGameFolder(string path)
    {
        Assert.That(
            () => UpdateManifest.Parse($"d41d8cd98f00b204e9800998ecf8427e  {path}\n"),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void RejectsNonHashFirstToken()
    {
        Assert.That(
            () => UpdateManifest.Parse("notahash  Song/example.pak\n"),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void RejectsDuplicatePaths()
    {
        Assert.That(
            () => UpdateManifest.Parse(
                "d41d8cd98f00b204e9800998ecf8427e  a.pak\n" +
                "d41d8cd98f00b204e9800998ecf8427e  A.PAK\n"),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void RoundTripsThroughWrite()
    {
        UpdateManifest original = UpdateManifest.Parse(
            "d41d8cd98f00b204e9800998ecf8427e  b.pak\n" +
            "0800fc577294c34e0b28ad2839435945  a/c.pak\n");

        UpdateManifest reparsed = UpdateManifest.Parse(original.Write());

        Assert.That(reparsed.Entries.Select(entry => entry.Path),
            Is.EqualTo(new[] { "a/c.pak", "b.pak" }));
    }

    [Test]
    public void CreateHashesEveryFileButTheManifest()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(directory.Path, "sub"));
        File.WriteAllText(Path.Combine(directory.Path, "sub", "two.txt"), "world");
        File.WriteAllText(
            Path.Combine(directory.Path, UpdateManifest.DefaultFileName), "stale");

        UpdateManifest manifest = UpdateManifest.Create(directory.Path);

        Assert.That(manifest.Entries.Select(entry => entry.Path),
            Is.EqualTo(new[] { "one.txt", "sub/two.txt" }));
        Assert.That(manifest.Entries[0].Md5, Is.EqualTo(Md5("hello")));
    }
}

[TestFixture]
public class UpdateServiceTest
{
    private static string Md5(string content) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>Publishes <paramref name="files"/> into a folder plus a matching manifest.</summary>
    private static void Publish(string root, params (string Path, string Content)[] files)
    {
        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        File.WriteAllText(
            Path.Combine(root, UpdateManifest.DefaultFileName),
            UpdateManifest.Create(root).Write());
    }

    [Test]
    public async Task DownloadsMissingAndChangedFilesOnly()
    {
        using TemporaryDirectory site = new();
        using TemporaryDirectory game = new();
        Publish(site.Path,
            ("same.pak", "unchanged"),
            ("changed.pak", "new version"),
            ("sub/missing.pak", "brand new"));

        File.WriteAllText(Path.Combine(game.Path, "same.pak"), "unchanged");
        File.WriteAllText(Path.Combine(game.Path, "changed.pak"), "old version");

        UpdateService service = new(new FolderUpdateTransport(site.Path));
        UpdateManifest manifest = await service.FetchManifestAsync(CancellationToken.None);
        UpdatePlan plan = UpdateService.Plan(manifest, game.Path);

        Assert.That(plan.Examined, Is.EqualTo(3));
        Assert.That(plan.Actions.Select(action => action.Entry.Path),
            Is.EquivalentTo(new[] { "changed.pak", "sub/missing.pak" }));
        Assert.That(
            plan.Actions.Single(action => action.Entry.Path == "changed.pak").Reason,
            Is.EqualTo(UpdateReason.Changed));

        await service.ApplyAsync(plan, game.Path, null, CancellationToken.None);

        Assert.That(File.ReadAllText(Path.Combine(game.Path, "changed.pak")),
            Is.EqualTo("new version"));
        Assert.That(File.ReadAllText(Path.Combine(game.Path, "sub", "missing.pak")),
            Is.EqualTo("brand new"));

        // Re-planning after applying must come back clean.
        Assert.That(UpdateService.Plan(manifest, game.Path).UpToDate, Is.True);
    }

    [Test]
    public async Task ReportsProgressForEveryFile()
    {
        using TemporaryDirectory site = new();
        using TemporaryDirectory game = new();
        Publish(site.Path, ("a.pak", "aaaa"), ("b.pak", "bbbb"));

        UpdateService service = new(new FolderUpdateTransport(site.Path));
        UpdateManifest manifest = await service.FetchManifestAsync(CancellationToken.None);
        UpdatePlan plan = UpdateService.Plan(manifest, game.Path);

        List<UpdateProgress> reports = [];
        await service.ApplyAsync(plan, game.Path,
            new SynchronousProgress<UpdateProgress>(reports.Add), CancellationToken.None);

        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports.Select(report => report.Path).Distinct(),
            Is.EquivalentTo(new[] { "a.pak", "b.pak" }));
        Assert.That(reports[^1].OverallFraction, Is.EqualTo(1F));
    }

    [Test]
    public async Task LeavesTheOriginalInPlaceWhenTheChecksumIsWrong()
    {
        using TemporaryDirectory site = new();
        using TemporaryDirectory game = new();
        Publish(site.Path, ("a.pak", "correct"));
        // Tamper with the published file after the manifest was written.
        File.WriteAllText(Path.Combine(site.Path, "a.pak"), "tampered");
        File.WriteAllText(Path.Combine(game.Path, "a.pak"), "original");

        UpdateService service = new(new FolderUpdateTransport(site.Path));
        UpdateManifest manifest = await service.FetchManifestAsync(CancellationToken.None);
        UpdatePlan plan = UpdateService.Plan(manifest, game.Path);

        Assert.That(
            async () => await service.ApplyAsync(plan, game.Path, null, CancellationToken.None),
            Throws.InstanceOf<InvalidDataException>());

        Assert.That(File.ReadAllText(Path.Combine(game.Path, "a.pak")), Is.EqualTo("original"));
        Assert.That(File.Exists(Path.Combine(game.Path, "a.pak.part")), Is.False);
    }

    [Test]
    public void PlanIgnoresLocalFilesTheManifestDoesNotList()
    {
        using TemporaryDirectory game = new();
        File.WriteAllText(Path.Combine(game.Path, "untracked.pak"), "mine");

        UpdateManifest manifest = UpdateManifest.Parse(string.Empty);
        UpdatePlan plan = UpdateService.Plan(manifest, game.Path);

        Assert.That(plan.UpToDate, Is.True);
        Assert.That(File.Exists(Path.Combine(game.Path, "untracked.pak")), Is.True);
    }

    [Test]
    public void CreateTransportPicksWebForUrlsAndFolderForPaths()
    {
        using HttpClient client = new();

        Assert.That(UpdateService.CreateTransport("https://example.com/patch/", client),
            Is.InstanceOf<WebUpdateTransport>());
        Assert.That(UpdateService.CreateTransport(@"C:\updates", client),
            Is.InstanceOf<FolderUpdateTransport>());
        Assert.That(UpdateService.CreateTransport("update", client),
            Is.InstanceOf<FolderUpdateTransport>());
    }

    [Test]
    public void FolderTransportRefusesToReadOutsideItsRoot()
    {
        using TemporaryDirectory site = new();
        FolderUpdateTransport transport = new(site.Path);

        Assert.That(
            async () => await transport.ReadTextAsync("../secret.txt", CancellationToken.None),
            Throws.InstanceOf<InvalidDataException>());
    }

    /// <summary>
    /// <see cref="Progress{T}"/> posts to the synchronisation context, which a test has
    /// none of - so reports would arrive after the assertions. This one runs inline.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>A scratch folder that deletes itself at the end of the test.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "djmax-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch folder is not worth failing a test over.
        }
    }
}
