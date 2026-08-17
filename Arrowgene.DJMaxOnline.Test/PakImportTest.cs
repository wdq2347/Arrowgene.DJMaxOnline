using Arrowgene.DJMaxOnline.Server.Korea400.Pak;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Verifies the XIP2 reader against the real client pak. The strongest check available is
/// that every catalog it extracts is byte-identical to the copy already sitting in DATA,
/// which was produced independently by the Python tooling.
///
/// The client data is not part of a clean checkout, so these are skipped when it is absent
/// rather than failing.
/// </summary>
[TestFixture]
public class PakImportTest
{
    private static string? RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Arrowgene.DJMaxOnline.sln")))
            {
                return directory.FullName;
            }
        }
        return null;
    }

    private static (string Pak, string Data, XipKeyPair Keys) RequireClientData()
    {
        string? root = RepositoryRoot();
        if (root == null)
        {
            Assert.Ignore("Repository root not found.");
        }

        string pak = Path.Combine(root!, "custom", "system.pak");
        string data = Path.Combine(root!, "DATA");
        string keyDirectory = Path.Combine(data, "xipkeys");
        if (!File.Exists(pak) || !XipKeyPair.Available(keyDirectory))
        {
            Assert.Ignore("Client pak or key tables not present in this checkout.");
        }

        return (pak, data, XipKeyPair.Load(keyDirectory));
    }

    [Test]
    public void ReadsTheEntryTable()
    {
        (string pak, _, XipKeyPair keys) = RequireClientData();

        XipArchive archive = XipArchive.Open(pak, keys);

        Assert.That(archive.Entries, Is.Not.Empty);
        Assert.That(archive.Entries.Any(e => e.NormalisedName == "Song/DiscStock.csv"), Is.True,
            "Song/DiscStock.csv should be listed in system.pak.");
        Assert.That(archive.Entries.Any(e => e.NormalisedName == "System/shop/ItemStock.csv"),
            Is.True);
    }

    /// <summary>
    /// The decisive test: LZO decode, the RSA-like prefix, the Japanese header XOR and the
    /// text dword mask all have to be right for these to match byte for byte.
    /// </summary>
    // DiscStock.csv and ItemStock.csv are deliberately absent: both are meant to be
    // edited (custom songs are patched into one, shop availability into the other), so
    // neither matches the stock copy in the pak.
    [TestCase("System/Icon/IconSet.csv")]
    [TestCase("System/FontSet.csv")]
    [TestCase("System/shop/Goods_Item_Battle.lst")]
    public void ExtractedFileMatchesTheCopyInData(string archivePath)
    {
        (string pak, string data, XipKeyPair keys) = RequireClientData();
        string local = Path.Combine(data, archivePath[(archivePath.LastIndexOf('/') + 1)..]);
        if (!File.Exists(local))
        {
            Assert.Ignore($"{local} is not in DATA.");
        }

        XipArchive archive = XipArchive.Open(pak, keys);
        XipEntry entry = archive.Entries.Single(e =>
            e.NormalisedName.Equals(archivePath, StringComparison.OrdinalIgnoreCase));

        Assert.That(archive.Extract(entry), Is.EqualTo(File.ReadAllBytes(local)));
    }

    /// <summary>The .ini members are the ones behind the additive text mask.</summary>
    [TestCase("System/courseclub/CourseSection.ini")]
    [TestCase("System/courseclub/CourseGeneral.ini")]
    public void ExtractedCourseScriptMatchesTheCopyInData(string archivePath)
    {
        (string pak, string data, XipKeyPair keys) = RequireClientData();
        string local = Path.Combine(data, archivePath[(archivePath.LastIndexOf('/') + 1)..]);
        if (!File.Exists(local))
        {
            Assert.Ignore($"{local} is not in DATA.");
        }

        XipArchive archive = XipArchive.Open(pak, keys);
        XipEntry entry = archive.Entries.Single(e =>
            e.NormalisedName.Equals(archivePath, StringComparison.OrdinalIgnoreCase));

        Assert.That(archive.Extract(entry), Is.EqualTo(File.ReadAllBytes(local)));
    }

    [Test]
    public void ImportPullsEveryFileTheServerNeeds()
    {
        (string pak, string data, XipKeyPair keys) = RequireClientData();
        using TemporaryDirectory destination = new();

        XipArchive archive = XipArchive.Open(pak, keys);
        GameDataImportResult result = new GameDataImporter().Import(pak, destination.Path, keys);

        Assert.That(result.Imported, Is.Not.Empty);

        // Every client catalog in DATA should come out of the pak chain. Matched by
        // extension rather than by listing the folder wholesale: DATA also holds our own
        // files, and an editor with a catalog open leaves lock files like
        // ".~lock.DiscStock.csv#" lying next to them.
        string[] catalogExtensions = [".csv", ".ini", ".lst"];
        // Only require coverage of catalogs the pak actually ships. DATA is a working
        // folder: operators keep backups and hand-made variants beside the real files,
        // and the importer is not expected to produce those.
        HashSet<string> shipped = new(
            archive.Entries.Where(entry => !entry.Deleted).Select(entry => entry.FileName),
            StringComparer.OrdinalIgnoreCase);
        string[] expected =
        [
            .. Directory.EnumerateFiles(data)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .Where(name => !name.StartsWith('.') &&
                               catalogExtensions.Contains(
                                   Path.GetExtension(name).ToLowerInvariant()) &&
                               shipped.Contains(name))
        ];

        string[] imported = [.. result.Imported.Select(file => file.FileName)];
        Assert.That(imported, Is.SupersetOf(expected),
            "Import should cover every catalog the server reads.");

        foreach (ImportedFile file in result.Imported)
        {
            string produced = Path.Combine(destination.Path, file.FileName);
            Assert.That(File.Exists(produced), Is.True, $"{file.FileName} was not written.");
        }
    }

    [Test]
    public void ImportNeverWritesThePlayerDatabase()
    {
        (string pak, _, XipKeyPair keys) = RequireClientData();
        using TemporaryDirectory destination = new();
        string database = Path.Combine(destination.Path, "djmax.sqlite3");
        File.WriteAllText(database, "player data");

        new GameDataImporter().Import(pak, destination.Path, keys);

        Assert.That(File.ReadAllText(database), Is.EqualTo("player data"));
    }

    [Test]
    public void PatchPaksLayerOverTheBaseInNumericOrder()
    {
        string? root = RepositoryRoot();
        if (root == null)
        {
            Assert.Ignore("Repository root not found.");
        }

        using TemporaryDirectory directory = new();
        foreach (string name in new[]
                 {
                     "system.pak", "system_0001.pak", "system_0010.pak", "system_0002.pak",
                     "crc.pak", "systemx.pak"
                 })
        {
            File.WriteAllText(Path.Combine(directory.Path, name), string.Empty);
        }

        IReadOnlyList<string> chain =
            GameDataImporter.ResolvePakChain(Path.Combine(directory.Path, "system.pak"));

        Assert.That(chain.Select(Path.GetFileName),
            Is.EqualTo(new[]
            {
                "system.pak", "system_0001.pak", "system_0002.pak", "system_0010.pak"
            }));
    }

    [Test]
    public void ChainIsTheSameWhicheverMemberIsNamed()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "system.pak"), string.Empty);
        File.WriteAllText(Path.Combine(directory.Path, "system_0001.pak"), string.Empty);

        Assert.That(
            GameDataImporter.ResolvePakChain(Path.Combine(directory.Path, "system_0001.pak")),
            Is.EqualTo(GameDataImporter.ResolvePakChain(
                Path.Combine(directory.Path, "system.pak"))));
    }
}
