using Arrowgene.DJMaxOnline.Server;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

[TestFixture]
public class SettingFileUpgradeTest
{
    [Test]
    public void LoadAppendsSettingsTheFileDoesNotMentionYet()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.ini");
        File.WriteAllText(path, "ServerPort = 4321\n");

        Setting setting = SettingFile.Load(path, out IReadOnlyList<string> unknown);

        Assert.That(unknown, Is.Empty);
        Assert.That(setting.ServerPort, Is.EqualTo(4321));

        string upgraded = File.ReadAllText(path);
        Assert.That(upgraded, Does.Contain("ServerPort = 4321"), "existing lines survive");
        Assert.That(upgraded, Does.Contain("ContentDelivery"), "new settings are added");
        Assert.That(upgraded, Does.Contain("ContentPatchPath"));
        Assert.That(upgraded, Does.Contain("UnlockAllCourses = false"));

        // Reloading the upgraded file must produce the same values and add nothing more.
        string before = File.ReadAllText(path);
        SettingFile.Load(path, out IReadOnlyList<string> second);
        Assert.That(second, Is.Empty);
        Assert.That(File.ReadAllText(path), Is.EqualTo(before), "upgrade is idempotent");
    }

    [Test]
    public void ExistingValuesAreNotOverwrittenByTheUpgrade()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.ini");
        File.WriteAllText(path, "ContentDelivery = Http\nContentBaseUrl = https://example.com/\n");

        Setting setting = SettingFile.Load(path);

        Assert.That(setting.ContentDelivery, Is.EqualTo(ContentDeliveryMode.Http));
        Assert.That(ContentDelivery.SongUrl(setting), Is.EqualTo("https://example.com/song/"));
        // Count assignment lines only - the comments on other settings mention the key too.
        int assignments = File.ReadAllLines(path).Count(line =>
            line.TrimStart().StartsWith("ContentDelivery ", StringComparison.Ordinal));
        Assert.That(assignments, Is.EqualTo(1),
            "the key should be assigned exactly once after the upgrade");
    }

    [Test]
    public void EnumSettingsRoundTrip()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.ini");
        SettingFile.Save(path, new Setting { ContentDelivery = ContentDeliveryMode.Http });

        Assert.That(SettingFile.Load(path).ContentDelivery,
            Is.EqualTo(ContentDeliveryMode.Http));
    }

    [Test]
    public void AnUnparseableEnumIsReportedRatherThanSilentlyDefaulted()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.ini");
        File.WriteAllText(path, "ContentDelivery = Carrier Pigeon\n");

        Assert.That(() => SettingFile.Load(path), Throws.InstanceOf<FormatException>());
    }
}
