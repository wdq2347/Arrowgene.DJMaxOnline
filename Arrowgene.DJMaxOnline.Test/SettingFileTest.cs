using System.Net;
using Arrowgene.DJMaxOnline.Server.Korea400;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The settings file is the only way to change deployment policy without a rebuild, so it
/// has to survive a round trip, tolerate a hand-edited partial file, and refuse values the
/// crypto primitives would reject.
/// </summary>
public class SettingFileTest
{
    private string _path = null!;

    [SetUp]
    public void CreateTempPath() =>
        _path = Path.Combine(Path.GetTempPath(), $"djmax-settings-{Guid.NewGuid():N}.ini");

    [TearDown]
    public void RemoveTempFile()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Test]
    public void RoundTripsEveryConfiguredValue()
    {
        Setting written = new()
        {
            Name = "Round Trip",
            ListenIpAddress = IPAddress.Parse("10.1.2.3"),
            ServerPort = 4100,
            ExperienceMultiplier = 7,
            UnlockAllCourses = true,
            AccuracyDiscTolerance = 0.25,
            LoginTicketLifetimeSeconds = 120,
            PasswordPolicy = new PasswordPolicySetting
            {
                Iterations = 250_000, SaltSize = 32, MinimumLength = 12, MaximumLength = 64
            },
            RewardRates = new RewardRateSetting
            {
                MoneyPerNoteHit = 5, NotesHitPerExperience = 9, FailureDivisor = 4
            }
        };

        SettingFile.Save(_path, written);
        Setting read = SettingFile.Load(_path);

        Assert.Multiple(() =>
        {
            Assert.That(read.Name, Is.EqualTo("Round Trip"));
            // IPAddress needs its own text conversion; this pins it.
            Assert.That(read.ListenIpAddress, Is.EqualTo(IPAddress.Parse("10.1.2.3")));
            Assert.That(read.ServerPort, Is.EqualTo(4100));
            Assert.That(read.ExperienceMultiplier, Is.EqualTo(7u));
            Assert.That(read.UnlockAllCourses, Is.True);
            Assert.That(read.AccuracyDiscTolerance, Is.EqualTo(0.25));
            Assert.That(read.LoginTicketLifetimeSeconds, Is.EqualTo(120));
            Assert.That(read.PasswordPolicy.Iterations, Is.EqualTo(250_000));
            Assert.That(read.PasswordPolicy.SaltSize, Is.EqualTo(32));
            Assert.That(read.PasswordPolicy.MinimumLength, Is.EqualTo(12));
            Assert.That(read.RewardRates.MoneyPerNoteHit, Is.EqualTo(5u));
            Assert.That(read.RewardRates.NotesHitPerExperience, Is.EqualTo(9u));
            Assert.That(read.RewardRates.FailureDivisor, Is.EqualTo(4u));
        });
    }

    [Test]
    public void RelativeFileSystemPathsResolveFromTheSettingsFileDirectory()
    {
        using TemporaryDirectory directory = new();
        string configDirectory = Path.Combine(directory.Path, "copied-server");
        Directory.CreateDirectory(configDirectory);
        string path = Path.Combine(configDirectory, "settings.ini");
        File.WriteAllText(path, string.Join(Environment.NewLine,
            "GameInfoDirectory = DATA",
            "FtpRootDirectory = paks",
            "FtpFallbackChartPath = Patterns/fallback.pt",
            "SongCatalogPath = DATA/DiscStock.csv",
            "PatternsDirectory = Patterns",
            "ShopDataDirectory = DATA",
            "PakDirectory = paks",
            "PatchDirectory = patch"));

        Setting read = SettingFile.Load(path);

        Assert.Multiple(() =>
        {
            Assert.That(read.GameInfoDirectory,
                Is.EqualTo(Path.Combine(configDirectory, "DATA")));
            Assert.That(read.FtpRootDirectory,
                Is.EqualTo(Path.Combine(configDirectory, "paks")));
            Assert.That(read.FtpFallbackChartPath,
                Is.EqualTo(Path.Combine(configDirectory, "Patterns", "fallback.pt")));
            Assert.That(read.SongCatalogPath,
                Is.EqualTo(Path.Combine(configDirectory, "DATA", "DiscStock.csv")));
            Assert.That(read.PatternsDirectory,
                Is.EqualTo(Path.Combine(configDirectory, "Patterns")));
            Assert.That(read.ShopDataDirectory,
                Is.EqualTo(Path.Combine(configDirectory, "DATA")));
            Assert.That(read.PakDirectory,
                Is.EqualTo(Path.Combine(configDirectory, "paks")));
            Assert.That(read.PatchDirectory,
                Is.EqualTo(Path.Combine(configDirectory, "patch")));
        });
    }

    [Test]
    public void SaveWritesContainedFileSystemPathsAsPortableRelativePaths()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.ini");
        Setting setting = new()
        {
            GameInfoDirectory = Path.Combine(directory.Path, "DATA"),
            SongCatalogPath = Path.Combine(directory.Path, "DATA", "DiscStock.csv"),
            PatternsDirectory = Path.Combine(directory.Path, "Patterns"),
            ShopDataDirectory = Path.Combine(directory.Path, "DATA"),
            PakDirectory = Path.Combine(directory.Path, "paks"),
            PatchDirectory = Path.Combine(directory.Path, "patch")
        };

        SettingFile.Save(path, setting);
        string saved = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(saved, Does.Contain("GameInfoDirectory = DATA"));
            Assert.That(saved, Does.Contain("SongCatalogPath = DATA/DiscStock.csv"));
            Assert.That(saved, Does.Contain("PatternsDirectory = Patterns"));
            Assert.That(saved, Does.Contain("ShopDataDirectory = DATA"));
            Assert.That(saved, Does.Contain("PakDirectory = paks"));
            Assert.That(saved, Does.Contain("PatchDirectory = patch"));
            Assert.That(saved, Does.Not.Contain(directory.Path));
        });
    }

    /// <summary>
    /// A file written by an older build must not lose the options it does not mention, or
    /// adding a setting would silently reset everyone's config.
    /// </summary>
    [Test]
    public void PartialFileKeepsDefaultsForAbsentKeys()
    {
        File.WriteAllText(_path, string.Join('\n',
            "# a hand written file",
            "Name = Only A Name",
            "ServerPort = 1234"));

        Setting read = SettingFile.Load(_path);
        Setting defaults = new();

        Assert.Multiple(() =>
        {
            Assert.That(read.Name, Is.EqualTo("Only A Name"));
            Assert.That(read.ServerPort, Is.EqualTo(1234));
            Assert.That(read.PasswordPolicy, Is.Not.Null, "a section absent from the file");
            Assert.That(read.PasswordPolicy.Iterations,
                Is.EqualTo(defaults.PasswordPolicy.Iterations));
            Assert.That(read.RewardRates.NotesHitPerExperience,
                Is.EqualTo(defaults.RewardRates.NotesHitPerExperience));
            Assert.That(read.FtpUsername, Is.EqualTo(defaults.FtpUsername));
            Assert.That(read.UnlockAllCourses, Is.False,
                "normal prerequisite progression remains the default");
        });
    }

    [Test]
    public void CommentsBlankLinesAndUnknownKeysAreHandled()
    {
        File.WriteAllText(_path, string.Join(Environment.NewLine,
            "# a comment",
            "; another comment",
            "",
            "ServerPort = 4242",
            "NotARealSetting = 1",
            "PasswordPolicy.AlsoNotReal = 2"));

        Setting read = SettingFile.Load(_path, out IReadOnlyList<string> unknown);

        Assert.Multiple(() =>
        {
            Assert.That(read.ServerPort, Is.EqualTo(4242));
            // Reported, not silently dropped - a typo must not look like "no effect".
            Assert.That(unknown, Is.EquivalentTo(
                new[] { "NotARealSetting", "PasswordPolicy.AlsoNotReal" }));
        });
    }

    /// <summary>Repeating a key builds a list; that is the only list syntax.</summary>
    [Test]
    public void RepeatedKeysBuildLists()
    {
        File.WriteAllText(_path, string.Join(Environment.NewLine,
            "MessageOfTheDay = first",
            "MessageOfTheDay = second",
            "JudgmentAdjustmentMsByMatchMode = 0",
            "JudgmentAdjustmentMsByMatchMode = -3",
            "AccuracyDiscs = Test Disc | 12.5 | 5 | 0x40A"));

        Setting read = SettingFile.Load(_path);

        Assert.Multiple(() =>
        {
            Assert.That(read.MessageOfTheDay, Is.EqualTo(new[] { "first", "second" }));
            Assert.That(read.JudgmentAdjustmentMsByMatchMode, Is.EqualTo(new[] { 0, -3 }));
            Assert.That(read.AccuracyDiscs, Has.Count.EqualTo(1));
            Assert.That(read.AccuracyDiscs[0].Name, Is.EqualTo("Test Disc"));
            Assert.That(read.AccuracyDiscs[0].Accuracy, Is.EqualTo(12.5));
            Assert.That(read.AccuracyDiscs[0].NoteMultiple, Is.EqualTo(5));
            Assert.That(read.AccuracyDiscs[0].Code, Is.EqualTo(0x40A), "0x form is accepted");
        });
    }

    /// <summary>Large costs stay readable in the file.</summary>
    [Test]
    public void UnderscoreSeparatorsAreAccepted()
    {
        File.WriteAllText(_path, "PasswordPolicy.Iterations = 1_200_000");

        Assert.That(SettingFile.Load(_path).PasswordPolicy.Iterations,
            Is.EqualTo(1_200_000));
    }

    /// <summary>Nothing hand-edited may produce a zero-iteration hash or a /0 payout.</summary>
    [Test]
    public void ValidationClampsValuesTheCryptoWouldReject()
    {
        PasswordPolicySetting password = new PasswordPolicySetting
        {
            Iterations = 0, SaltSize = 1, MinimumLength = 0, MaximumLength = 2
        }.Validated();
        RewardRateSetting rates = new RewardRateSetting
        {
            NotesHitPerExperience = 0, FailureDivisor = 0
        }.Validated();

        Assert.Multiple(() =>
        {
            Assert.That(password.Iterations, Is.GreaterThanOrEqualTo(1_000));
            Assert.That(password.SaltSize, Is.GreaterThanOrEqualTo(8));
            Assert.That(password.MinimumLength, Is.GreaterThanOrEqualTo(1));
            Assert.That(password.MaximumLength,
                Is.GreaterThanOrEqualTo(password.MinimumLength),
                "a max below the min would reject every password");
            Assert.That(rates.NotesHitPerExperience, Is.EqualTo(1u), "divisor, must not be 0");
            Assert.That(rates.FailureDivisor, Is.EqualTo(1u), "divisor, must not be 0");
        });
    }

    /// <summary>
    /// Applying the policy has to actually reach the static holders the server reads.
    /// Restores the defaults afterwards so test order cannot matter.
    /// </summary>
    [Test]
    public void ApplyPushesPolicyIntoTheStaticHolders()
    {
        try
        {
            SettingFile.Apply(new Setting
            {
                PasswordPolicy = new PasswordPolicySetting { Iterations = 123_000 },
                RewardRates = new RewardRateSetting { MoneyPerNoteHit = 11 },
                AccuracyDiscTolerance = 0.5
            });

            Assert.Multiple(() =>
            {
                Assert.That(PasswordSecurity.Iterations, Is.EqualTo(123_000));
                Assert.That(StageRewardPolicy.MoneyPerNoteHit, Is.EqualTo(11u));
                Assert.That(CollectionDiscs.Tolerance, Is.EqualTo(0.5));
            });
        }
        finally
        {
            SettingFile.Apply(new Setting());
        }
    }
}
