using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// Reads and writes <see cref="Setting"/> as a flat <c>key = value</c> text file.
///
/// The server used to be configured entirely by <c>new Setting()</c> plus a handful of CLI
/// flags, so anything without a flag - password hashing cost, reward rates - could only be
/// changed by editing source and rebuilding. Everything tunable now lives in one file that
/// is one setting per line, no nesting, no punctuation to get wrong:
///
///     ServerPort = 3000
///     PasswordPolicy.Iterations = 600000
///     MessageOfTheDay = Welcome            # repeat a key to build a list
///
/// Nested groups use a dotted prefix. Lists repeat their key. Blank lines and lines
/// starting with # or ; are ignored, and unknown keys are reported rather than silently
/// dropped, so a typo does not quietly leave a setting at its default.
///
/// Precedence is file first, then CLI flags and environment variables on top, so existing
/// launch scripts keep working and a one-off override needs no file edit.
/// </summary>
public static class SettingFile
{
    public const string DefaultFileName = "settings.ini";

    private const string ListSeparator = " | ";
    private static readonly HashSet<string> FileSystemPathKeys = new(
        StringComparer.OrdinalIgnoreCase)
    {
        nameof(Setting.GameInfoDirectory),
        nameof(Setting.FtpRootDirectory),
        nameof(Setting.FtpFallbackChartPath),
        nameof(Setting.SongCatalogPath),
        nameof(Setting.PatternsDirectory),
        nameof(Setting.ShopDataDirectory),
        nameof(Setting.PlayerDatabasePath),
        nameof(Setting.PakDirectory),
        nameof(Setting.PatchDirectory)
    };

    public static Setting Load(string path) => Load(path, out _);

    /// <param name="unknownKeys">Keys in the file that match no setting.</param>
    public static Setting Load(string path, out IReadOnlyList<string> unknownKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string baseDirectory = Path.GetDirectoryName(fullPath)!;
        Setting setting = new();
        Dictionary<string, List<string>> values = Parse(File.ReadAllLines(fullPath));
        List<string> unknown = [];

        foreach ((string key, List<string> raw) in values)
        {
            if (!TryAssign(setting, key, raw))
            {
                unknown.Add(key);
            }
        }

        // A file written by an older build has no line for a setting added since. Append
        // the missing ones with their defaults so upgrading does not silently leave new
        // features unreachable, and so the file stays a complete list of what is tunable.
        AppendMissingKeys(fullPath, setting, values.Keys);
        ResolveFileSystemPaths(setting, baseDirectory);

        unknownKeys = unknown;
        return setting;
    }

    public static void Save(string path, Setting setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(setting);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            fullPath, Render(setting, directory), new UTF8Encoding(false));
    }

    /// <summary>
    /// Filesystem paths in the config belong to the config, not the shell that happened
    /// to launch the server. This makes a copied server tree work from any drive/folder.
    /// </summary>
    private static void ResolveFileSystemPaths(Setting setting, string baseDirectory)
    {
        setting.GameInfoDirectory = ResolvePath(setting.GameInfoDirectory, baseDirectory)!;
        setting.FtpRootDirectory = ResolvePath(setting.FtpRootDirectory, baseDirectory)!;
        setting.FtpFallbackChartPath = ResolvePath(
            setting.FtpFallbackChartPath, baseDirectory);
        setting.SongCatalogPath = ResolvePath(setting.SongCatalogPath, baseDirectory)!;
        setting.PatternsDirectory = ResolvePath(setting.PatternsDirectory, baseDirectory);
        setting.ShopDataDirectory = ResolvePath(setting.ShopDataDirectory, baseDirectory)!;
        setting.PlayerDatabasePath = ResolvePath(setting.PlayerDatabasePath, baseDirectory);
        setting.PakDirectory = ResolvePath(setting.PakDirectory, baseDirectory)!;
        setting.PatchDirectory = ResolvePath(setting.PatchDirectory, baseDirectory)!;
    }

    private static string? ResolvePath(string? value, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value);
        return Path.GetFullPath(expanded, baseDirectory);
    }

    /// <summary>
    /// Finds the settings file by walking up from the working directory and the binary, the
    /// same way the data folders are located, so it resolves whether the CLI is run from
    /// the repo root or from <c>bin/Debug/net8.0</c>.
    /// </summary>
    public static string? Find(string fileName = DefaultFileName)
    {
        foreach (string start in new[]
                 {
                     Directory.GetCurrentDirectory(), AppContext.BaseDirectory
                 })
        {
            for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Pushes the configured policy into the static holders. Called once at startup, before
    /// anything hashes a password or scores a song.
    /// </summary>
    public static void Apply(Setting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        PasswordSecurity.Configure(setting.PasswordPolicy);
        StageRewardPolicy.Configure(setting.RewardRates);
        CollectionDiscs.Configure(setting.AccuracyDiscTolerance);
    }

    // ------------------------------------------------------------------ parsing

    private static Dictionary<string, List<string>> Parse(IEnumerable<string> lines)
    {
        Dictionary<string, List<string>> values =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
            {
                continue;
            }

            int split = trimmed.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            string key = trimmed[..split].Trim();
            string value = trimmed[(split + 1)..].Trim();
            if (!values.TryGetValue(key, out List<string>? list))
            {
                values[key] = list = [];
            }

            // An explicit empty value clears a list rather than adding a blank entry.
            if (value.Length != 0)
            {
                list.Add(value);
            }
        }

        return values;
    }

    private static bool TryAssign(Setting setting, string key, List<string> raw)
    {
        object target = setting;
        string name = key;
        int dot = key.IndexOf('.');
        if (dot > 0)
        {
            PropertyInfo? group = typeof(Setting).GetProperty(
                key[..dot],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (group == null)
            {
                return false;
            }

            object? instance = group.GetValue(setting);
            if (instance == null)
            {
                instance = Activator.CreateInstance(group.PropertyType);
                group.SetValue(setting, instance);
            }

            target = instance!;
            name = key[(dot + 1)..];
        }

        PropertyInfo? property = target.GetType().GetProperty(
            name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property == null || !property.CanWrite)
        {
            return false;
        }

        object? converted = Convert(property.PropertyType, raw);
        if (converted == null && raw.Count != 0)
        {
            return false;
        }

        property.SetValue(target, converted);
        return true;
    }

    private static object? Convert(Type type, List<string> raw)
    {
        if (type == typeof(List<string>))
        {
            return new List<string>(raw);
        }

        if (type == typeof(List<int>))
        {
            return raw.Select(ParseInt).ToList();
        }

        if (type == typeof(List<AccuracyDiscRule>))
        {
            return raw.Select(ParseDisc).ToList();
        }

        string single = raw.Count == 0 ? string.Empty : raw[^1];
        return ConvertScalar(type, single);
    }

    private static object? ConvertScalar(Type type, string value)
    {
        if (type == typeof(string))
        {
            return value;
        }

        if (type == typeof(IPAddress))
        {
            return IPAddress.Parse(value);
        }

        if (type == typeof(bool))
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        if (type.IsEnum)
        {
            return Enum.TryParse(type, value, ignoreCase: true, out object? parsed)
                ? parsed
                : throw new FormatException(
                    $"'{value}' is not one of: {string.Join(", ", Enum.GetNames(type))}.");
        }

        if (type == typeof(double))
        {
            return double.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(int))
        {
            return ParseInt(value);
        }

        if (type == typeof(uint))
        {
            return (uint)ParseLong(value);
        }

        if (type == typeof(ushort))
        {
            return (ushort)ParseLong(value);
        }

        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return value.Length == 0 ? null : ConvertScalar(inner, value);
        }

        return null;
    }

    // Underscores are allowed so large costs stay readable (600_000), and 0x prefixes so
    // disc codes can be written the way the client's tables show them.
    private static long ParseLong(string value)
    {
        string text = value.Replace("_", string.Empty).Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? System.Convert.ToInt64(text[2..], 16)
            : long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static int ParseInt(string value) => (int)ParseLong(value);

    private static AccuracyDiscRule ParseDisc(string value)
    {
        string[] parts = value.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            throw new InvalidDataException(
                $"AccuracyDiscs needs 'name | accuracy | noteMultiple | code', got '{value}'.");
        }

        return new AccuracyDiscRule
        {
            Name = parts[0],
            Accuracy = double.Parse(parts[1], CultureInfo.InvariantCulture),
            NoteMultiple = ParseInt(parts[2]),
            Code = (ushort)ParseLong(parts[3])
        };
    }

    // ------------------------------------------------------------------ writing

    private static readonly Dictionary<string, string> Notes = new()
    {
        ["Name"] = "Channel name shown in the client's server list.",
        ["ListenIpAddress"] = "Interface the game server binds to. 0.0.0.0 accepts from anywhere.",
        ["ServerPort"] = "Port for the 5-key channel.",
        ["SevenKeyServerPort"] = "Port for the 7-key channel.",
        ["AdvertisedIpAddress"] = "Address handed to clients when they switch channel. Must be reachable BY THEM, not by the server.",
        ["GameInfoFallbackDiscId"] = "Disc whose captured payload is used when a chart has none of its own.",
        ["FtpEnabled"] = "Embedded read-only FTP server. Only used when ContentDelivery=Ftp.",
        ["FtpListenIpAddress"] = "Interface the embedded FTP server binds to.",
        ["FtpPort"] = "Port for the embedded FTP server (21 by convention).",
        ["FtpAdvertisedIpAddress"] = "Address sent in the FTP PASV reply. Must be reachable by the client.",
        ["FtpRootDirectory"] = "Folder the embedded FTP server serves. Relative to this file.",
        ["FtpUsername"] = "Username the client sends. The client has these compiled in - changing them needs a client change.",
        ["FtpPassword"] = "Password the client sends. Stored as plain text; this is not an access control.",
        ["FtpFallbackChartPath"] = "Chart handed out when a requested one is missing. Empty disables the fallback.",
        ["StatusApiEnabled"] = "Read-only JSON API for LOCAL tooling (the Discord bot). NOT FOR PUBLIC EXPOSURE.",
        ["StatusApiListenIpAddress"] = "Keep on 127.0.0.1 unless it sits behind a proxy you control - there is no TLS.",
        ["StatusApiPort"] = "Port for the local status API.",
        ["StatusApiToken"] = "Optional shared secret sent as X-Status-Token. Empty means no check, which only suits loopback.",
        ["AsyncEventSettings"] = "Socket server tuning (buffer counts and sizes). Leave alone unless profiling says otherwise.",
        ["LoginPipeName"] = "Named pipe the launcher hands login tickets over. Must match the launcher's pipeName.",
        ["LoginApiEnabled"] = "HTTP login API for REMOTE launchers. Put it behind a Cloudflare Tunnel or TLS proxy - it has no TLS of its own.",
        ["LoginApiListenIpAddress"] = "Interface the login API binds to. Loopback is right when a tunnel or proxy runs on this machine.",
        ["LoginApiPort"] = "Port for the login API. The launcher's loginUrl points at whatever fronts it.",
        ["LoginReconnectGraceSeconds"] = "How long a session survives while the client moves between channel sockets.",
        ["EquipmentMaxPercent"] = "Percentage of an item's catalog MAX bonus that is actually paid out.",
        ["EquipmentExperiencePercent"] = "Percentage of an item's catalog EXP bonus that is actually paid out.",
        ["DownloadUrl"] = "Song archive URL used when ContentDelivery=Ftp. Must match the FTP settings below.",
        ["ContentDelivery"] = "Ftp (embedded FTP server) or Http (serve songs and patches over the web).",
        ["ContentBaseUrl"] = "Root of the content site when ContentDelivery=Http, e.g. https://example.com/",
        ["ContentSongPath"] = "Folder under ContentBaseUrl holding song archives; becomes DOWNLOADURL.",
        ["ContentPatchPath"] = "Route the launcher updates from; mirrors the game folder (crc.pak, system paks, subfolders).",
        ["HttpContentEnabled"] = "Serve /song/ and /patch/ from the built-in HTTP server instead of a real host.",
        ["HttpContentListenIpAddress"] = "Interface the built-in HTTP content server binds to.",
        ["HttpContentPort"] = "Port for the built-in HTTP content server.",
        ["PatchDirectory"] = "Local folder served as ContentPatchPath. Mirrors the game folder. Relative to this file.",
        ["PatchManifestAutoBuild"] = "Rebuild the checksum list from this folder at startup so published hashes cannot go stale.",
        ["PatchManifestFileName"] = "Name of the checksum list served from the patch folder.",
        ["PatchNewsFileName"] = "Launcher news file, served from the patch folder and excluded from the checksum list.",
        ["GameInfoDirectory"] = "Captured game-info payloads and client catalogs. Relative to this file.",
        ["PatternsDirectory"] = "Plaintext PTFF charts. Empty = use captured payloads instead. Relative to this file.",
        ["PakDirectory"] = "Packed song archives served as /song/<tag>.pak. Relative to this file.",
        ["ShopDataDirectory"] = "ItemStock/DiscStock/Goods lists. Relative to this file.",
        ["PlayerDatabasePath"] = "SQLite account/score database. Empty = ShopDataDirectory/djmax.japan400.sqlite3. Relative to this file.",
        ["SongCatalogPath"] = "DiscStock.csv. MUST match the client's copy or charts fail to decode. Relative to this file.",
        ["ExperienceMultiplier"] = "Scales the final EXP award. Level thresholds cannot change.",
        ["MoneyMultiplier"] = "Scales the final MAX award.",
        ["PremiumRewardBonusPercent"] = "Extra payout for premium accounts, on top of everything else.",
        ["MissionMatchChancePercent"] = "Chance a random-disc pick becomes a DJ Mission match.",
        ["BattleItemComboInterval"] =
            "Combo per item-battle item drop. Server policy; the client has no drop rule.",
        ["DefaultAccountClassFlags"] = "Granted to every account, e.g. Premium.",
        ["EquipmentHpPercent"] = "0 makes gear cosmetic; 100 is full effect.",
        ["ItemsNeverExpire"] = "New shop/course items are permanent and the server never removes timed inventory. Existing expiry values are left unchanged.",
        ["UnlockAllCourses"] =
            "Offer every course in CourseSection.ini; false uses normal prerequisites.",
        ["LoginTicketLifetimeSeconds"] = "How long a launcher ticket stays redeemable (max 300).",
        ["AccuracyDiscTolerance"] = "How close accuracy must be to an AccuracyDiscs entry to earn it.",
        ["MessageOfTheDay"] = "Repeat this key for more lines.",
        ["JudgmentAdjustmentMsByMatchMode"] = "Per match mode (0 free, 1 ranked, 2 score, 3 item, 4 course). Negative = tighter.",
        ["AccuracyDiscs"] = "name | accuracy | noteMultiple | code",
        ["PasswordPolicy"] =
            "Password HASHING POLICY only. Salts and hashes live per-account in the\n" +
            "# player database, never here. Iterations and SaltSize affect only NEW\n" +
            "# credentials: each stored credential records the cost it was hashed with,\n" +
            "# so raising this does not invalidate existing accounts.",
        ["RewardRates"] =
            "Base payout before the multipliers above. Pure server policy - the retail\n" +
            "# economy is not encoded anywhere in the client."
    };

    /// <summary>
    /// Appends any setting the file does not mention yet, with the value already loaded
    /// (so a default). Existing lines are never touched, and a failure to write is not
    /// fatal - the server runs on the defaults either way.
    /// </summary>
    private static void AppendMissingKeys(
        string path, Setting setting, IEnumerable<string> present)
    {
        HashSet<string> known = new(present, StringComparer.OrdinalIgnoreCase);
        StringBuilder added = new();
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        foreach (PropertyInfo property in Ordered(typeof(Setting)))
        {
            if (property.GetCustomAttribute<DataMemberAttribute>() == null ||
                IsExcluded(property.PropertyType))
            {
                continue;
            }

            object? value = property.GetValue(setting);
            if (IsGroup(property.PropertyType))
            {
                foreach (PropertyInfo child in Ordered(property.PropertyType))
                {
                    string key = $"{property.Name}.{child.Name}";
                    if (!known.Contains(key))
                    {
                        Write(added, key, child.GetValue(value), null, baseDirectory);
                    }
                }

                continue;
            }

            if (!known.Contains(property.Name))
            {
                Notes.TryGetValue(property.Name, out string? note);
                Write(added, property.Name, value, note, baseDirectory);
            }
        }

        if (added.Length == 0)
        {
            return;
        }

        try
        {
            File.AppendAllText(path,
                Environment.NewLine + "# Added by a newer build." + Environment.NewLine +
                added);
        }
        catch (Exception)
        {
            // Read-only config directory; the loaded defaults still apply.
        }
    }

    private static string Render(Setting setting, string baseDirectory)
    {
        StringBuilder text = new();
        text.AppendLine("# Arrowgene.DJMaxOnline server settings.");
        text.AppendLine("# One setting per line. Repeat a key to build a list.");
        text.AppendLine("# Lines starting with # or ; are ignored. Delete this file to regenerate it.");
        text.AppendLine("# CLI flags and environment variables override anything set here.");
        text.AppendLine("# Relative filesystem paths are resolved from the folder containing this file.");
        text.AppendLine();

        foreach (PropertyInfo property in Ordered(typeof(Setting)))
        {
            if (property.GetCustomAttribute<DataMemberAttribute>() == null ||
                IsExcluded(property.PropertyType))
            {
                continue;
            }

            object? value = property.GetValue(setting);
            if (IsGroup(property.PropertyType))
            {
                text.AppendLine();
                if (Notes.TryGetValue(property.Name, out string? groupNote))
                {
                    text.AppendLine($"# {groupNote}");
                }

                foreach (PropertyInfo child in Ordered(property.PropertyType))
                {
                    Write(
                        text,
                        $"{property.Name}.{child.Name}",
                        child.GetValue(value),
                        null,
                        baseDirectory);
                }

                continue;
            }

            Notes.TryGetValue(property.Name, out string? note);
            Write(text, property.Name, value, note, baseDirectory);
        }

        return text.ToString();
    }

    private static IEnumerable<PropertyInfo> Ordered(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.GetCustomAttribute<DataMemberAttribute>()?.Order ?? int.MaxValue);

    private static bool IsGroup(Type type) =>
        type == typeof(PasswordPolicySetting) || type == typeof(RewardRateSetting);

    private static void Write(
        StringBuilder text,
        string key,
        object? value,
        string? note,
        string? baseDirectory = null)
    {
        if (note != null)
        {
            text.AppendLine($"# {note}");
        }

        object? portableValue = PortableValue(key, value, baseDirectory);
        switch (portableValue)
        {
            case null:
                text.AppendLine($"{key} =");
                return;
            case List<string> strings:
                WriteList(text, key, strings);
                return;
            case List<int> ints:
                WriteList(text, key, ints.Select(i => i.ToString(CultureInfo.InvariantCulture)));
                return;
            case List<AccuracyDiscRule> discs:
                WriteList(text, key, discs.Select(d => string.Join(
                    ListSeparator,
                    d.Name,
                    d.Accuracy.ToString(CultureInfo.InvariantCulture),
                    d.NoteMultiple.ToString(CultureInfo.InvariantCulture),
                    $"0x{d.Code:X}")));
                return;
            case double number:
                text.AppendLine($"{key} = {number.ToString(CultureInfo.InvariantCulture)}");
                return;
            case bool flag:
                text.AppendLine($"{key} = {(flag ? "true" : "false")}");
                return;
            default:
                text.AppendLine($"{key} = {portableValue}");
                return;
        }
    }

    private static object? PortableValue(
        string key, object? value, string? baseDirectory)
    {
        if (baseDirectory == null || !FileSystemPathKeys.Contains(key) ||
            value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return value;
        }

        string fullPath = Path.GetFullPath(path, baseDirectory);
        string relative = Path.GetRelativePath(baseDirectory, fullPath);
        string parentPrefix = ".." + Path.DirectorySeparatorChar;
        if (relative == ".." ||
            relative.StartsWith(parentPrefix, StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative))
        {
            // An explicitly configured external path cannot become self-contained merely
            // by rewriting it; preserve it instead of silently changing its meaning.
            return path;
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void WriteList(StringBuilder text, string key, IEnumerable<string> items)
    {
        bool any = false;
        foreach (string item in items)
        {
            text.AppendLine($"{key} = {item}");
            any = true;
        }

        if (!any)
        {
            text.AppendLine($"{key} =");
        }
    }

    /// <summary>
    /// Transport tuning from Arrowgene.Networking - socket internals, not deployment
    /// policy, and not expressible as flat key/value pairs.
    /// </summary>
    private static bool IsExcluded(Type type) =>
        type == typeof(Arrowgene.Networking.Tcp.Server.AsyncEvent.AsyncEventSettings);
}
