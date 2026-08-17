using System.Text;
using Arrowgene.DJMaxOnline.Server.Japan400;
using Arrowgene.DJMaxOnline.Server.Japan400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline;

public partial class Program
{
    public static void Main(string[] args)
    {
        // Start each run with a fresh log so the current session is easy to read.
        try
        {
            File.WriteAllText(
                Path.Combine(Directory.GetCurrentDirectory(), "log.txt"), string.Empty);
        }
        catch
        {
            // ignore — logging still works, the file just keeps appending
        }

        LogProvider.OnLogWrite += LogProviderOnOnLogWrite;
        LogProvider.Start();
        Program p = new Program();
        if (args.Length >= 2 && args[0] == "--decrypt")
        {
            p.RunDecrypt(args[1]);
        }
        else if (args.Length >= 2 && args[0] == "--verify-captures")
        {
            p.RunVerifyCaptures(args[1]);
        }
        else if (args.Length >= 2 && args[0] == "--protocol-report")
        {
            p.RunProtocolReport(args[1]);
        }
        else if (args.Length >= 3 && args[0] == "--import-game-info")
        {
            p.RunImportGameInfo(args[1], args[2]);
        }
        else if (args.Length >= 2 && args[0] == "--import-data")
        {
            p.RunImportData(args);
        }
        else if (args.Length >= 2 && args[0] == "--verify-song-catalog")
        {
            p.RunVerifySongCatalog(args[1]);
        }
        else if (args.Length >= 2 && args[0] == "--verify-game-data")
        {
            p.RunVerifyGameData(args[1]);
        }
        else if (args.Length >= 4 && args[0] == "--build-game-info")
        {
            p.RunBuildGameInfo(args);
        }
        else
        {
            p.Run(args);
        }
        LogProvider.Stop();
    }

    private static readonly ILogger Logger = LogProvider.Logger(typeof(Program));

    private static readonly string RootPath = Directory.GetCurrentDirectory();


    /// <summary>
    /// Runs on the logger's own write thread, where an escaping exception terminates the
    /// process. Neither destination is worth killing a running server for: a resized
    /// console or a log file held open by an editor must not take players offline.
    /// </summary>
    private static void LogProviderOnOnLogWrite(object? sender, LogWriteEventArgs e)
    {
        string line = e.Log.ToString();
        try
        {
            InteractiveConsole.WriteLogLine(line);
        }
        catch
        {
            // ignore - the file copy below is the durable record
        }

        try
        {
            File.AppendAllText(Path.Combine(RootPath, "log.txt"), line + Environment.NewLine);
        }
        catch
        {
            // ignore - the line already reached the console
        }
    }

    /// <summary>Settings file for the japan400 build; see LoadSettings.</summary>
    private const string Japan400SettingsFileName = "settings.japan400.ini";

    public void Run(string[] args)
    {
        Setting setting = LoadSettings(args, out string? settingsToWrite);
        ConfigureFtp(setting, args);
        ConfigureSongCatalog(setting, args);
        ConfigurePatterns(setting, args);
        ConfigurePaks(setting, args);
        ConfigureShopCatalog(setting, args);
        WriteSettingsIfNew(setting, settingsToWrite);
        string profilePath = ResolvePlayerProfilePath(args);
        string databasePath = ResolvePlayerDatabasePath(setting, args);
        SqlitePlayerRepository players = new(databasePath);
        bool createUser = TryResolveCreateUser(
            args, out string accountId, out string nickname, out byte gender);
        bool grantAccountClass = TryResolveAccountClassGrant(
            args, out string accountClassSelector, out uint accountClassFlags);
        string? setPasswordSelector = ResolveOption(args, "--set-password");
        string? debugProfileSelector = ResolveOption(args, "--seed-debug-profile");
        bool listUsers = args.Any(argument =>
            string.Equals(argument, "--list-users", StringComparison.OrdinalIgnoreCase));
        // Account-management commands must not accidentally make a pre-existing legacy
        // player disappear by creating a different first row. Import it before creating
        // or listing users, but do not manufacture the neutral default just for a list.
        if ((createUser || grantAccountClass || listUsers || setPasswordSelector != null ||
             debugProfileSelector != null) &&
            players.UserCount == 0 && File.Exists(profilePath))
        {
            PlayerDatabaseSelection imported = PlayerDatabaseBootstrap.Select(
                players, profilePath);
            Logger.Info(
                $"Imported legacy player {imported.Profile.Nickname} from {profilePath}; " +
                "the JSON file was left untouched.");
        }
        if (createUser)
        {
            LocalPlayerProfile created = players.Create(accountId, nickname, gender);
            Logger.Info(
                $"Created SQLite player {created.Nickname} ({created.AccountId}, " +
                $"id {created.UserId}) in {databasePath}.");
            SetPasswordInteractively(players, created);
            Logger.Info("Password configured; this account can use the secure launcher.");
            return;
        }
        if (setPasswordSelector != null)
        {
            if (!players.TryLoad(setPasswordSelector, out LocalPlayerProfile? selected) ||
                selected == null)
            {
                throw new KeyNotFoundException(
                    $"No SQLite player matches '{setPasswordSelector}'.");
            }
            SetPasswordInteractively(players, selected);
            Logger.Info(
                $"Password updated for {selected.AccountId} (user {selected.UserId}). " +
                "All previously issued login tickets remain short-lived and one-use.");
            return;
        }
        if (grantAccountClass)
        {
            if (!players.TryLoad(accountClassSelector, out LocalPlayerProfile? selected) ||
                selected == null)
            {
                throw new KeyNotFoundException(
                    $"No SQLite player matches '{accountClassSelector}'.");
            }
            uint previous = selected.AccountClass;
            selected.AccountClass |= accountClassFlags;
            players.Save(selected);
            Logger.Info(
                $"Granted {string.Join(", ", AccountClassInfo.ToNames(accountClassFlags))} " +
                $"to {selected.AccountId} (user {selected.UserId}). Account class: " +
                $"0x{previous:X} -> 0x{selected.AccountClass:X} in {databasePath}.");
            return;
        }
        if (debugProfileSelector != null)
        {
            PlayerDatabaseSelection selected = PlayerDatabaseBootstrap.Select(
                players, profilePath, debugProfileSelector);
            // Explicit diagnostic command: this deliberately replaces game-facing
            // profile data with markers that are unmistakable in every packet/UI.
            DebugProfileSeed.Apply(selected.Profile);
            players.Save(selected.Profile);
            Logger.Info(
                $"Seeded DEBUG profile for {selected.Profile.AccountId} / " +
                $"{selected.Profile.Nickname} (user {selected.Profile.UserId}, " +
                $"male, level {selected.Profile.Progress.Level}) in {databasePath}.");
            return;
        }
        if (listUsers)
        {
            foreach (PlayerAccountSummary user in players.ListUsers())
            {
                Logger.Info(
                    $"SQLite player {user.UserId}: {user.AccountId} / " +
                    $"{user.Nickname}, level {user.Level}, launcher login " +
                    (players.TryGetPasswordCredential(user.AccountId, out _)
                        ? "enabled"
                        : "DISABLED (run --set-password)"));
            }
            return;
        }
        string? pinnedUser = ResolveOption(args, "--user");

        // A brand new database has no account to log in with, so seed one. This is the
        // only reason startup looks at the database at all; when it already has accounts
        // nothing is read from it, and no profile is loaded until a launcher ticket names
        // one.
        PlayerDatabaseSelection? seeded =
            PlayerDatabaseBootstrap.EnsureSeeded(players, profilePath);
        if (seeded is { ImportedLegacyJson: true })
        {
            Logger.Info(
                $"Imported legacy player {seeded.Profile.Nickname} from {profilePath}; " +
                "the JSON file was left untouched.");
        }
        else if (seeded is { CreatedDefault: true })
        {
            Logger.Info("Created the default player in the empty SQLite database.");
        }

        LocalPlayerProfile? pinnedProfile = null;
        if (pinnedUser == null)
        {
            Logger.Info(
                $"{players.UserCount} account(s) in {databasePath}. None is loaded now - " +
                "each player is read from the database when their launcher ticket is " +
                "redeemed.");
        }
        else
        {
            // --user deliberately pins one account as the server's shared store, which
            // every unauthenticated connection would then see. It is a debugging aid.
            pinnedProfile = PlayerDatabaseBootstrap
                .Select(players, profilePath, pinnedUser).Profile;
            Logger.Info(
                $"Pinned SQLite player {pinnedProfile.Nickname} ({pinnedProfile.AccountId}, " +
                $"id {pinnedProfile.UserId}) via --user; {players.UserCount} account(s) in " +
                $"{databasePath}. Unauthenticated connections will see this account.");
        }

        DjMaxServer server = new(
            setting,
            pinnedProfile,
            profilePath: null,
            playerRepository: players);
        server.Start();
        bool terminateProcess = false;
        try
        {
            // The ticket service is REQUIRED, not optional. Without it a ban cannot revoke
            // the launcher session, so the banned client reconnects, resumes it and has to
            // be kicked again on a loop.
            ServerAdministrationService administration = new(
                players, server.ClientLookup, server.LoginTickets);
            GracefulShutdownCoordinator shutdown = new(server, administration);
            LiveAdministrationConsole console = new(
                administration, ReadConfirmedPassword, shutdown.Shutdown);
            terminateProcess = console.Run();
        }
        finally
        {
            // /stop must leave accepted sockets open until the operating system ends the
            // process. That is the observable difference between Ctrl+C and the managed
            // AsyncEventServer.Stop path for this retail client.
            if (!terminateProcess)
            {
                server.Stop();
            }
        }

        if (terminateProcess)
        {
            Console.WriteLine(
                "Shutdown drain finished; terminating the process with live sockets " +
                "to match Ctrl+C.");
            LogProvider.Stop();
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Loads settings.json, creating it with defaults on first run so every tunable is
    /// visible and editable without touching the source. CLI flags and environment
    /// variables are applied afterwards and win, so one-off overrides need no file edit.
    /// </summary>
    private static Setting LoadSettings(
        IReadOnlyList<string> args,
        out string? writeAfterDiscovery)
    {
        string? path = ResolveOption(args, "--config") ??
                       Environment.GetEnvironmentVariable("DJMAX_CONFIG") ??
                       // This build hosts japan400, so it reads japan400's own settings
                       // file. Sharing settings.ini with the korea400 build would give
                       // both servers the same ports and the same data directories.
                       SettingFile.Find(Japan400SettingsFileName);

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            Setting loaded = SettingFile.Load(path, out IReadOnlyList<string> unknown);
            SettingFile.Apply(loaded);
            Logger.Info($"Settings: {Path.GetFullPath(path)}");
            if (unknown.Count != 0)
            {
                // A typo would otherwise look like the setting simply had no effect.
                Logger.Error(
                    $"Ignoring unknown settings: {string.Join(", ", unknown)}.");
            }

            writeAfterDiscovery = null;
            return loaded;
        }

        Setting setting = new();
        SettingFile.Apply(setting);
        writeAfterDiscovery = Path.GetFullPath(
            path is { Length: > 0 } ? path : SettingFile.DefaultFileName);
        return setting;
    }

    /// <summary>
    /// Writes the settings file on first run, after path discovery, so it records the
    /// folders actually resolved rather than the working directory at startup.
    /// </summary>
    private static void WriteSettingsIfNew(Setting setting, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            SettingFile.Save(path, setting);
            Logger.Info($"Settings: wrote defaults to {path}");
        }
        catch (Exception exception)
        {
            // A read-only working directory is not fatal; the defaults still apply.
            Logger.Info($"Settings: using defaults ({exception.Message}).");
        }
    }

    /// <summary>A path from the settings file, or null when it was left blank (auto).</summary>
    private static string? Configured(string? value) =>
        string.IsNullOrWhiteSpace(value) || !(File.Exists(value) || Directory.Exists(value))
            ? null
            : value;

    private static void ConfigureFtp(Setting setting, IReadOnlyList<string> args)
    {
        string? root = ResolveOption(args, "--ftp-root") ??
                       Environment.GetEnvironmentVariable("DJMAX_FTP_ROOT") ??
                       Configured(setting.FtpRootDirectory);
        string? fallback = ResolveOption(args, "--ftp-fallback") ??
                           Environment.GetEnvironmentVariable("DJMAX_FTP_FALLBACK");

        if (string.IsNullOrWhiteSpace(root))
        {
            root = FindExtractedSongRoot();
        }

        if (!string.IsNullOrWhiteSpace(root))
        {
            setting.FtpRootDirectory = Path.GetFullPath(root);
        }

        if (string.IsNullOrWhiteSpace(fallback) &&
            Directory.Exists(setting.FtpRootDirectory))
        {
            // Prefer a real extracted chart over the UI's tiny Testsong.pt.
            fallback = Directory
                .EnumerateFiles(
                    setting.FtpRootDirectory,
                    "*.pt",
                    SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file =>
                    !string.Equals(
                        file.Name,
                        "Testsong.pt",
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.Length)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }

        setting.FtpFallbackChartPath = string.IsNullOrWhiteSpace(fallback)
            ? null
            : Path.GetFullPath(fallback);

        // Only announce the FTP root when the FTP server is the one that will serve it.
        // This ran unconditionally and read exactly like "FTP is starting" on a server
        // delivering over HTTP with FtpEnabled=false - which starts nothing at all: the
        // FTP path is skipped for ContentDelivery=Http, and LocalFtpServer.Start returns
        // early without the flag.
        bool ftpWillServe =
            setting.ContentDelivery == ContentDeliveryMode.Ftp && setting.FtpEnabled;
        Logger.Info(ftpWillServe
            ? $"Local FTP root: {setting.FtpRootDirectory}; fallback chart: " +
              (setting.FtpFallbackChartPath ?? "none")
            : "FTP delivery is off; songs and patches are served over HTTP " +
              $"(chart root: {setting.FtpRootDirectory}).");
    }

    private static string? ResolveOption(
        IReadOnlyList<string> args,
        string option)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++i >= args.Count)
            {
                throw new ArgumentException($"{option} requires a directory or file path.");
            }

            return args[i];
        }

        return null;
    }

    /// <summary>
    /// The FTP root only matters for serving already-EXTRACTED chart paths; packed
    /// archives are served from <see cref="Setting.PakDirectory"/>. Default it to the
    /// pak folder so a bare checkout still resolves somewhere real.
    /// </summary>
    private static string? FindExtractedSongRoot() =>
        FindDirectoryUpwards("paks", "*.pak");

    private static void ConfigurePatterns(Setting setting, IReadOnlyList<string> args)
    {
        string? dir = ResolveOption(args, "--patterns") ??
                      Environment.GetEnvironmentVariable("DJMAX_PATTERNS") ??
                      Configured(setting.PatternsDirectory) ??
                      FindPatternsDirectory();
        if (string.IsNullOrWhiteSpace(dir))
        {
            Logger.Info(
                "No Patterns directory found; using captured game-info payloads.");
            return;
        }

        setting.PatternsDirectory = Path.GetFullPath(dir);
        int charts = Directory.EnumerateFiles(setting.PatternsDirectory, "*.pt").Count();
        Logger.Info(
            $"Patterns directory: {setting.PatternsDirectory} ({charts} PTFF charts); " +
            "game-info will be generated on the fly.");
    }

    private static string? FindPatternsDirectory()
    {
        DirectoryInfo? dir = new(RootPath);
        for (int depth = 0; depth < 6 && dir != null; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Patterns");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*.pt").Any())
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Finds a file in the DATA folder, searching upwards from cwd and the binary.</summary>
    private static string? FindDataFile(string name)
    {
        foreach (string start in new[]
                 {
                     Directory.GetCurrentDirectory(), AppContext.BaseDirectory
                 })
        {
            for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "DATA", name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Finds the sibling folder holding the packed song archives.</summary>
    private static string? FindDirectoryUpwards(string name, string pattern)
    {
        foreach (string start in new[] { RootPath, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new(start);
            for (int depth = 0; depth < 6 && dir != null; depth++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, name);
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate, pattern).Any())
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void ConfigurePaks(Setting setting, IReadOnlyList<string> args)
    {
        string? dir = ResolveOption(args, "--paks") ??
                      Environment.GetEnvironmentVariable("DJMAX_PAKS") ??
                      Configured(setting.PakDirectory) ??
                      FindDirectoryUpwards("paks", "*.pak");
        if (string.IsNullOrWhiteSpace(dir))
        {
            Logger.Info("No paks directory found; the client cannot download songs.");
            return;
        }

        setting.PakDirectory = Path.GetFullPath(dir);
        int count = Directory.EnumerateFiles(setting.PakDirectory, "*.pak").Count();
        Logger.Info($"Pak directory: {setting.PakDirectory} ({count} archives).");
    }

    private static void ConfigureSongCatalog(
        Setting setting,
        IReadOnlyList<string> args)
    {
        string? path = ResolveOption(args, "--song-catalog") ??
                       Environment.GetEnvironmentVariable("DJMAX_SONG_CATALOG") ??
                       Configured(setting.SongCatalogPath) ??
                       FindSongCatalog();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException(
                "DiscStock.csv was not found. Use --song-catalog or " +
                "DJMAX_SONG_CATALOG to select the merged song catalog.");
        }

        setting.SongCatalogPath = Path.GetFullPath(path);
        Logger.Info($"Song catalog: {setting.SongCatalogPath}");
    }

    private static void ConfigureShopCatalog(
        Setting setting,
        IReadOnlyList<string> args)
    {
        string? directory = ResolveOption(args, "--shop-data") ??
                            Environment.GetEnvironmentVariable("DJMAX_SHOP_DATA") ??
                            Configured(setting.ShopDataDirectory) ??
                            ShopCatalog.FindDataDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new DirectoryNotFoundException(
                "Shop DATA was not found. Use --shop-data or DJMAX_SHOP_DATA to " +
                "select the directory containing ItemStock.csv and the Goods lists.");
        }

        setting.ShopDataDirectory = Path.GetFullPath(directory);
        Logger.Info($"Shop data: {setting.ShopDataDirectory}");
    }

    private static string? FindSongCatalog()
    {
        // The client's DiscStock.csv lives in DATA alongside ItemStock.csv/IconSet.csv
        // and the Goods lists; SongCatalog auto-detects its 25-column Korean layout.
        // Search up from both the working directory and the binary so it is found
        // regardless of how the CLI was launched. The old whole-drive scans for a
        // "C:\DJMAX\China2.60\..." install are gone - the server ships its own data.
        return FindDataFile("DiscStock.csv");
    }

    private static string ResolvePlayerProfilePath(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], "--player", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++i >= args.Count)
            {
                throw new ArgumentException("--player requires a JSON file path.");
            }

            return Path.GetFullPath(args[i]);
        }

        return Path.Combine(RootPath, "player.json");
    }

    private static string ResolvePlayerDatabasePath(
        Setting setting,
        IReadOnlyList<string> args)
    {
        string? path = ResolveOption(args, "--database") ??
                       Environment.GetEnvironmentVariable("DJMAX_DATABASE") ??
                       Configured(setting.PlayerDatabasePath);
        // A different file from korea400's djmax.sqlite3.  Keeping it configurable
        // also lets a JP extracted DATA tree be replaced without losing accounts.
        return Path.GetFullPath(path ??
            Path.Combine(setting.ShopDataDirectory, "djmax.japan400.sqlite3"));
    }

    private static bool TryResolveCreateUser(
        IReadOnlyList<string> args,
        out string accountId,
        out string nickname,
        out byte gender)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (!string.Equals(
                    args[index], "--create-user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (index + 2 >= args.Count)
            {
                throw new ArgumentException(
                    "--create-user requires <accountId> <nickname> [gender 0|1].");
            }
            accountId = args[index + 1];
            nickname = args[index + 2];
            gender = 1;
            if (index + 3 < args.Count && byte.TryParse(args[index + 3], out byte parsed))
            {
                gender = parsed;
            }
            if (gender > 1)
            {
                throw new ArgumentException("--create-user gender must be 0 or 1.");
            }
            return true;
        }
        accountId = string.Empty;
        nickname = string.Empty;
        gender = 1;
        return false;
    }

    /// <summary>
    /// Parses an additive privilege grant. Unlike a raw packed account-class override,
    /// this deliberately preserves all existing client flags (including premium and
    /// badge bits) and only adds the requested named roles.
    /// </summary>
    private static bool TryResolveAccountClassGrant(
        IReadOnlyList<string> args,
        out string selector,
        out uint flags)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--grant-account-class",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (index + 2 >= args.Count)
            {
                throw new ArgumentException(
                    "--grant-account-class requires <accountId|nickname> <flags>. " +
                    "Example: --grant-account-class male Admin,GameMaster");
            }

            selector = args[index + 1];
            flags = AccountClassInfo.FromNames(
                args[index + 2].Split(',', StringSplitOptions.RemoveEmptyEntries |
                                      StringSplitOptions.TrimEntries),
                out IReadOnlyList<string> unknown);
            if (unknown.Count != 0 || flags == 0)
            {
                string invalid = unknown.Count == 0
                    ? args[index + 2]
                    : string.Join(", ", unknown);
                throw new ArgumentException(
                    $"Unknown or empty account-class flags: {invalid}.");
            }
            return true;
        }

        selector = string.Empty;
        flags = 0;
        return false;
    }

    private static void SetPasswordInteractively(
        IPlayerRepository players,
        LocalPlayerProfile profile)
    {
        string password = ReadConfirmedPassword(profile.AccountId);
        PlayerPasswordCredential credential = PasswordSecurity.Create(
            profile.UserId, password);
        players.SetPasswordCredential(credential);
    }

    private static string ReadConfirmedPassword(string accountId)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Password setup requires an interactive console so the password is not " +
                "exposed in command-line arguments or redirected text.");
        }

        string first = InteractiveConsole.ReadSecret(
            $"New password for {accountId}: ", PasswordSecurity.MaximumLength);
        string second = InteractiveConsole.ReadSecret(
            "Confirm password: ", PasswordSecurity.MaximumLength);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Passwords did not match.");
        }
        PasswordSecurity.ValidateNewPassword(first);
        return first;
    }

    public void RunDecrypt(string capturePath)
    {
        capturePath = Path.GetFullPath(capturePath);
        string yaml = File.ReadAllText(capturePath);
        PacketReader r = new PacketReader();
        List<PacketReader.PcapPacket> packets = r.ReadYamlPcap(yaml);

        PacketFactory server = new PacketFactory();
        PacketFactory client = new PacketFactory();
        StringBuilder sb = new StringBuilder();

        try
        {
            foreach (PacketReader.PcapPacket packet in packets)
            {
                if (packet.Source == PacketSource.Client)
                {
                    client.FillReadBuffer(packet.Data);
                    while (true)
                    {
                        Packet? p = client.ReadPacket();
                        if (p == null)
                        {
                            break;
                        }

                        sb.AppendLine(
                            $"enc = new byte[] {{ 0x{BitConverter.ToString(p.Encrypted).Replace("-", ", 0x")} }}");
                        sb.AppendLine(
                            $"dec = new byte[] {{ 0x{BitConverter.ToString(p.Data).Replace("-", ", 0x")} }}");
                        sb.AppendLine(p.ToLog());
                        packet.ResolvedPackets.Add(p);

                        if (p.Meta.Source != PacketSource.Client)
                        {
                            Console.WriteLine($"!!!! EXPECTED SERVER PACKET {p.Meta.ToLog()}");
                        }
                    }
                }
                else if (packet.Source == PacketSource.Server)
                {
                    if (packet.Data[0] == 0x0A && packet.Data[1] == 0x00)
                    {
                        // adjust packet
                        packet.Data[0] = 0x09;
                    }

                    server.FillReadBuffer(packet.Data);
                    while (true)
                    {
                        Packet? p = server.ReadPacket();
                        if (p == null)
                        {
                            break;
                        }

                        if (p.Meta.Id == PacketId.OnConnectAck)
                        {
                            server.InitCrypto(DjMaxCrypto.FromOnConnectAckPacket(p));
                            client.InitCrypto(DjMaxCrypto.FromOnConnectAckPacket(p));
                        }

                        sb.AppendLine(
                            $"enc = new byte[] {{ 0x{BitConverter.ToString(p.Encrypted).Replace("-", ", 0x")} }}");
                        sb.AppendLine(
                            $"dec = new byte[] {{ 0x{BitConverter.ToString(p.Data).Replace("-", ", 0x")} }}");
                        sb.AppendLine(p.ToLog());
                        packet.ResolvedPackets.Add(p);

                        if (p.Meta.Source != PacketSource.Server)
                        {
                            Console.WriteLine($"!!!! EXPECTED CLIENT PACKET {p.Meta.ToLog()}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Exception(ex);
        }

        string outputName = $"{Path.GetFileNameWithoutExtension(capturePath)}.out.txt";
        File.WriteAllText(Path.Combine(RootPath, outputName), sb.ToString());
    }

    public void RunVerifyCaptures(string captureDirectory)
    {
        CaptureVerificationResult result = CaptureVerifier.VerifyDirectory(
            Path.GetFullPath(captureDirectory));
        Console.WriteLine(
            $"Verified {result.PacketCount} packets across {result.CaptureCount} captures; " +
            $"{result.DistinctPacketIds} packet IDs and {result.TypedRoundTrips} typed round-trips. " +
            $"Receive coverage: {result.CaptureVerifiedReceiveCases} capture-verified, " +
            $"{result.DeclaredButUncapturedReceiveCases} declared but uncaptured, " +
            $"{result.HandlerOnlyReceiveCases} handler-only.");
    }

    public void RunProtocolReport(string outputPath)
    {
        outputPath = Path.GetFullPath(outputPath);
        ProtocolReportWriter.Write(outputPath);
        Console.WriteLine($"Protocol report written to {outputPath}");
    }

    public void RunImportGameInfo(string captureDirectory, string outputDirectory)
    {
        GameInfoImportResult result = GameInfoCaptureImporter.ImportDirectory(
            captureDirectory, outputDirectory);
        Console.WriteLine(
            $"Imported {result.ImportedDiscs} discs from {result.ObservedPayloads} " +
            $"OnGameInfoInf packets across {result.CaptureCount} captures: " +
            string.Join(", ", result.DiscIds));
    }

    /// <summary>
    /// --build-game-info &lt;ptff.pt&gt; &lt;discId&gt; &lt;out.bin&gt;
    ///     [--session ".[7KEY] Local"] [--diff 0] [--template &lt;bin&gt;] [--int16 0]
    /// Assembles a session-keyed OnGameInfoInf blob from a raw PTFF chart so the
    /// client can load it without the descramble/decompress crash.
    /// </summary>
    public void RunBuildGameInfo(string[] args)
    {
        string ptPath = Path.GetFullPath(args[1]);
        uint discId = uint.Parse(args[2]);
        string outPath = Path.GetFullPath(args[3]);

        string session = ResolveOption(args, "--session") ?? ".[7KEY] Local";
        ushort diff = ushort.Parse(ResolveOption(args, "--diff") ?? "0");
        short int16 = short.Parse(ResolveOption(args, "--int16") ?? "0");

        byte[] ptff = File.ReadAllBytes(ptPath);
        if (ptff.Length < 4 || ptff[0] != (byte)'P' || ptff[1] != (byte)'T' ||
            ptff[2] != (byte)'F' || ptff[3] != (byte)'F')
        {
            throw new InvalidDataException($"{ptPath} does not start with the PTFF magic.");
        }

        byte[] blob = GameInfoBlobBuilder.Build(
            discId, diff, chartType: 0, ptff, session, int16);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, blob);
        Console.WriteLine(
            $"Wrote {blob.Length}-byte game-info blob for disc {discId} " +
            $"(PTFF {ptff.Length} B, session \"{session}\", diff {diff}) -> {outPath}");
    }

    public void RunVerifySongCatalog(string path)
    {
        SongCatalog catalog = SongCatalog.Load(path);
        int fiveKeyCharts = catalog.Songs.Sum(song => song.Charts.Count(chart =>
            chart.KeyMode == SongKeyMode.FiveKey && chart.IsAvailable));
        int sevenKeyCharts = catalog.Songs.Sum(song => song.Charts.Count(chart =>
            chart.KeyMode == SongKeyMode.SevenKey && chart.IsAvailable));
        Console.WriteLine(
            $"Verified {catalog.Count} songs ({catalog.Songs.Min(song => song.Id)}-" +
            $"{catalog.Songs.Max(song => song.Id)}), {fiveKeyCharts} available 5-key " +
            $"and {sevenKeyCharts} available 7-key chart definitions.");
    }

    /// <summary>
    /// Loads every server-side catalog from one extracted client DATA folder without
    /// opening a listening socket.  Use after --import-data to catch a wrong client
    /// version/layout before the normal server startup fails halfway through.
    /// </summary>
    public void RunVerifyGameData(string dataDirectory)
    {
        string root = Path.GetFullPath(dataDirectory);
        ShopCatalog shop = ShopCatalog.Load(root);
        SongCatalog songs = SongCatalog.Load(Path.Combine(root, "DiscStock.csv"));
        string coursePath = Path.Combine(root, "CourseSection.ini");
        CourseCatalog courses = File.Exists(coursePath)
            ? CourseCatalog.Load(coursePath)
            : CourseCatalog.Empty;
        Console.WriteLine(
            $"Verified {root}: {songs.Count} songs, {shop.ItemCount} items, " +
            $"{shop.ListingCount} listings, {shop.SetCount} sets, {courses.Count} courses.");
    }
}
