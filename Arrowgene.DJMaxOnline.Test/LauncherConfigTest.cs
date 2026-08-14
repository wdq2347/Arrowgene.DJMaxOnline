using System.Reflection;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// launcher.cfg has to survive a launcher run.
///
/// The launcher rewrites the file when it closes. It used to build a brand-new config from
/// a hand-written list of fields, so any setting missing from that list was written back as
/// its DEFAULT - a hand-edited loginUrl simply vanished the next time the launcher was
/// opened and closed. These pin the round trip so a newly added setting cannot be quietly
/// dropped the same way.
///
/// The launcher is a Windows-only project, so its config type is reached by reflection
/// rather than a project reference.
/// </summary>
[TestFixture]
public class LauncherConfigTest
{
    private string _path = string.Empty;
    private Type _configType = null!;

    [SetUp]
    public void SetUp()
    {
        string assemblyPath = FindLauncherAssembly();
        if (assemblyPath.Length == 0)
        {
            Assert.Ignore("The launcher has not been built; nothing to check.");
        }

        _configType = Assembly.LoadFrom(assemblyPath)
            .GetType("Arrowgene.DJMaxOnline.Launcher.LauncherConfig")!;
        Assert.That(_configType, Is.Not.Null, "LauncherConfig was not found.");
        _path = Path.Combine(Path.GetTempPath(), $"launcher-{Guid.NewGuid():N}.cfg");
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    private static string FindLauncherAssembly()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "Arrowgene.DJMaxOnline.Launcher", "bin", "Debug", "net8.0-windows",
                "Arrowgene.DJMaxOnline.Launcher.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return string.Empty;
    }

    private object Load() =>
        _configType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [_path])!;

    private void Save(object config) =>
        _configType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(config, [_path]);

    private object? Get(object config, string name) =>
        _configType.GetProperty(name)!.GetValue(config);

    private void Set(object config, string name, object? value) =>
        _configType.GetProperty(name)!.SetValue(config, value);

    [Test]
    public void EverySettingSurvivesASaveAndReload()
    {
        object written = Activator.CreateInstance(_configType)!;
        Set(written, "LoginUrl", "https://play.example.com/login");
        Set(written, "UpdateUrl", "http://server:8080/patch/");
        Set(written, "UpdateNews", "announcements.txt");
        Set(written, "LocaleEmulator", @"C:\calocalemu\LEProc.exe");
        Set(written, "LocaleProfile", "208b1daa-34a6-4d21-838d-a3f813d742da");
        Set(written, "GamePath", @"C:\game\DJMax.exe");
        Save(written);

        object reloaded = Load();

        Assert.Multiple(() =>
        {
            Assert.That(Get(reloaded, "LoginUrl"), Is.EqualTo("https://play.example.com/login"));
            Assert.That(Get(reloaded, "UpdateUrl"), Is.EqualTo("http://server:8080/patch/"));
            Assert.That(Get(reloaded, "UpdateNews"), Is.EqualTo("announcements.txt"));
            Assert.That(Get(reloaded, "LocaleEmulator"), Is.EqualTo(@"C:\calocalemu\LEProc.exe"));
            Assert.That(Get(reloaded, "LocaleProfile"),
                Is.EqualTo("208b1daa-34a6-4d21-838d-a3f813d742da"));
            Assert.That(Get(reloaded, "GamePath"), Is.EqualTo(@"C:\game\DJMax.exe"));
        });
    }

    [Test]
    public void SavingAfterAReloadChangesNothing()
    {
        // What the launcher does on exit: load, then save. Anything the save path does not
        // know about must come back unchanged - this is the round trip that erased
        // loginUrl, because the saver rebuilt the config from scratch.
        object written = Activator.CreateInstance(_configType)!;
        Set(written, "LoginUrl", "https://play.example.com/login");
        Set(written, "UpdateNews", "announcements.txt");
        Save(written);

        object first = Load();
        Save(first);
        object second = Load();

        Assert.Multiple(() =>
        {
            Assert.That(Get(second, "LoginUrl"), Is.EqualTo("https://play.example.com/login"),
                "loginUrl must survive a launcher run");
            Assert.That(Get(second, "UpdateNews"), Is.EqualTo("announcements.txt"));
        });
    }

    [Test]
    public void CredentialsAreDroppedWhenRememberMeIsOff()
    {
        // The one thing that SHOULD be discarded on save.
        object written = Activator.CreateInstance(_configType)!;
        Set(written, "SaveCredentials", false);
        Set(written, "AccountId", "blade");
        Set(written, "Password", "hunter2");
        Set(written, "LoginUrl", "https://play.example.com/login");
        Save(written);

        object reloaded = Load();

        Assert.Multiple(() =>
        {
            Assert.That(Get(reloaded, "AccountId"), Is.Empty);
            Assert.That(Get(reloaded, "Password"), Is.Empty);
            Assert.That(File.ReadAllText(_path), Does.Not.Contain("hunter2"));
            // ...while everything else is still there.
            Assert.That(Get(reloaded, "LoginUrl"), Is.EqualTo("https://play.example.com/login"));
        });
    }
}
