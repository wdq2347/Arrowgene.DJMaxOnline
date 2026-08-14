using System.Net;
using System.Runtime.Serialization;
using Arrowgene.Networking;
using Arrowgene.Networking.Tcp.Server.AsyncEvent;

namespace Arrowgene.DJMaxOnline.Server;

public class Setting
{
    [DataMember(Order = 1)] public string Name { get; set; }

    [DataMember(Order = 2)] public IPAddress ListenIpAddress { get; set; }

    [DataMember(Order = 3)] public ushort ServerPort { get; set; }

    [DataMember(Order = 4)] public ushort SevenKeyServerPort { get; set; }

    [DataMember(Order = 5)] public IPAddress AdvertisedIpAddress { get; set; }

    [DataMember(Order = 6)] public string DownloadUrl { get; set; }

    [DataMember(Order = 7)] public string GameInfoDirectory { get; set; }

    [DataMember(Order = 8)] public uint GameInfoFallbackDiscId { get; set; }

    [DataMember(Order = 9)] public bool FtpEnabled { get; set; }

    [DataMember(Order = 10)] public IPAddress FtpListenIpAddress { get; set; }

    [DataMember(Order = 11)] public ushort FtpPort { get; set; }

    [DataMember(Order = 12)] public IPAddress FtpAdvertisedIpAddress { get; set; }

    [DataMember(Order = 13)] public string FtpRootDirectory { get; set; }

    [DataMember(Order = 14)] public string FtpUsername { get; set; }

    [DataMember(Order = 15)] public string FtpPassword { get; set; }

    [DataMember(Order = 16)] public string? FtpFallbackChartPath { get; set; }

    [DataMember(Order = 17)] public string SongCatalogPath { get; set; }

    /// <summary>
    /// Directory of plaintext PTFF charts (<c>&lt;tag&gt;_5k.pt</c> / <c>_7k.pt</c>).
    /// When set, game-info payloads are generated on the fly from these charts;
    /// otherwise the captured payloads in <see cref="GameInfoDirectory"/> are used.
    /// </summary>
    [DataMember(Order = 18)] public string? PatternsDirectory { get; set; }

    /// <summary>Extracted System/shop catalog and Goods-list directory.</summary>
    [DataMember(Order = 19)] public string ShopDataDirectory { get; set; }

    /// <summary>
    /// Directory of packed song archives (<c>&lt;tag&gt;.pak</c> and their
    /// <c>&lt;tag&gt;_0001.pak</c> patch volumes). This is what the FTP server hands the
    /// client for <c>/song/&lt;tag&gt;.pak</c>; it is deliberately separate from
    /// <see cref="PatternsDirectory"/>, which holds only plaintext charts.
    /// </summary>
    [DataMember(Order = 34)] public string PakDirectory { get; set; }

    /// <summary>Password hashing cost and length limits.</summary>
    [DataMember(Order = 35)] public PasswordPolicySetting PasswordPolicy { get; set; }

    /// <summary>Base money/experience rates before the multipliers above are applied.</summary>
    [DataMember(Order = 36)] public RewardRateSetting RewardRates { get; set; }

    /// <summary>
    /// How close a run's accuracy must be to an <see cref="AccuracyDiscs"/> entry to earn
    /// it. The client reports a float, so an exact equality test would never fire.
    /// </summary>
    [DataMember(Order = 37)] public double AccuracyDiscTolerance { get; set; }

    /// <summary>How long a launcher login ticket stays redeemable.</summary>
    [DataMember(Order = 38)] public int LoginTicketLifetimeSeconds { get; set; }

    /// <summary>
    /// How the client fetches song archives. <c>Ftp</c> keeps the embedded FTP server;
    /// <c>Http</c> points DOWNLOADURL at <see cref="ContentBaseUrl"/> instead.
    ///
    /// The client is scheme-agnostic: its downloader is WinINet (InternetOpenUrlA), and it
    /// asks for the size with HttpQueryInfo(CONTENT_LENGTH) first, falling back to
    /// FtpGetFileSize (sub_482AEF). http:// and https:// both work with no client change.
    /// </summary>
    [DataMember(Order = 39)] public ContentDeliveryMode ContentDelivery { get; set; }

    /// <summary>
    /// Root of the content site, e.g. <c>https://updates.example.com/</c>. The song and
    /// client-pak folders hang off it, so one host serves both.
    /// </summary>
    [DataMember(Order = 40)] public string ContentBaseUrl { get; set; }

    /// <summary>Folder under <see cref="ContentBaseUrl"/> holding the song archives.</summary>
    [DataMember(Order = 41)] public string ContentSongPath { get; set; }

    /// <summary>Serve the content folders from the built-in HTTP server rather than externally.</summary>
    [DataMember(Order = 43)] public bool HttpContentEnabled { get; set; }

    [DataMember(Order = 44)] public IPAddress HttpContentListenIpAddress { get; set; }

    [DataMember(Order = 45)] public ushort HttpContentPort { get; set; }

    /// <summary>
    /// The launcher's update route: the patch files, the checksum list describing them,
    /// and the news file.
    ///
    /// THE PATCH FOLDER MIRRORS THE GAME FOLDER. A file belongs at the same path here as
    /// it does in the player's install - crc.pak and the system paks sit at the root
    /// because that is where the game keeps them, and a file under System/ goes in a
    /// System subfolder. That is why this route serves NESTED paths while /song/ stays
    /// flat: the song route answers a bare file name the client asks for, this one
    /// answers a path relative to the game folder.
    ///
    /// One route for everything the launcher installs - there is no separate client-pak
    /// site, because a client pak is just another file in the game folder.
    /// </summary>
    [DataMember(Order = 55)] public string ContentPatchPath { get; set; }

    /// <summary>Local directory served as <see cref="ContentPatchPath"/>.</summary>
    [DataMember(Order = 56)] public string PatchDirectory { get; set; }

    /// <summary>
    /// Rebuild the patch checksum list from the files on disk at startup.
    ///
    /// On means the published hashes cannot go stale: drop a new system_0001.pak into the
    /// patch folder, restart, and every launcher sees the new hash. Off means the list is
    /// whatever you uploaded by hand, which is what a mirrored or CDN-fronted setup wants.
    /// </summary>
    [DataMember(Order = 57)] public bool PatchManifestAutoBuild { get; set; }

    /// <summary>The checksum list's file name, served from the patch folder.</summary>
    [DataMember(Order = 58)] public string PatchManifestFileName { get; set; }

    /// <summary>
    /// HTTP login API for launchers on ANOTHER MACHINE. The named pipe stays on either
    /// way and is what a local launcher uses; this only adds a remote route.
    ///
    /// It speaks plain HTTP and has no TLS of its own, so it is meant to sit behind
    /// something that provides it - a Cloudflare Tunnel (which needs no inbound port at
    /// all), a proxied hostname on Full (strict), or your own reverse proxy. Exposed
    /// directly to the internet it would carry account passwords in the clear.
    ///
    /// Authentication is the same code the pipe uses, including the per-account rate
    /// limit - which is what protects it, since a proxy replaces the client IP with its
    /// own and makes per-IP limits at the origin meaningless.
    /// </summary>
    [DataMember(Order = 60)] public bool LoginApiEnabled { get; set; }

    /// <summary>
    /// Interface the login API binds to. Loopback is right when a tunnel or proxy runs on
    /// this machine; it only needs to be reachable by that, not by players.
    /// </summary>
    [DataMember(Order = 61)] public IPAddress LoginApiListenIpAddress { get; set; }

    [DataMember(Order = 62)] public ushort LoginApiPort { get; set; }

    /// <summary>
    /// The launcher's news file, served from the patch folder.
    ///
    /// Deliberately EXCLUDED from the checksum list: everything a manifest names gets
    /// downloaded into the player's game folder, and the news belongs on the site, not in
    /// their install. Blank turns the news feed off and the launcher keeps its built-in text.
    /// </summary>
    [DataMember(Order = 59)] public string PatchNewsFileName { get; set; }

    /// <summary>
    /// Read-only JSON status API for LOCAL tooling (the Discord bot). NOT FOR PUBLIC
    /// EXPOSURE: no TLS, no rate limiting, no user authentication beyond the optional
    /// shared token below. It publishes rooms and player nicknames only - never account
    /// ids, passwords, tickets or addresses.
    /// </summary>
    [DataMember(Order = 51)] public bool StatusApiEnabled { get; set; }

    /// <summary>Keep this on loopback unless it sits behind a proxy you control.</summary>
    [DataMember(Order = 52)] public IPAddress StatusApiListenIpAddress { get; set; }

    [DataMember(Order = 53)] public ushort StatusApiPort { get; set; }

    /// <summary>
    /// Optional shared secret. When set, callers must send it as the X-Status-Token
    /// header. Empty means no check, which is only sensible on loopback.
    /// </summary>
    [DataMember(Order = 54)] public string StatusApiToken { get; set; }

    /// <summary>
    /// Offer every course defined by CourseSection.ini, ignoring prerequisite progress.
    /// This is an in-memory availability override and does not rewrite saved unlocks.
    /// </summary>
    [DataMember(Order = 47)] public bool UnlockAllCourses { get; set; }

    /// <summary>
    /// Transport tuning from Arrowgene.Networking. Not written to the settings file: it is
    /// socket internals rather than deployment policy.
    /// </summary>
    [DataMember(Order = 20)] public AsyncEventSettings AsyncEventSettings { get; set; }

    [DataMember(Order = 21)] public string LoginPipeName { get; set; }

    /// <summary>
    /// How long a launcher-authenticated player may attach a replacement socket
    /// after a channel transition or unexpected disconnect.
    /// </summary>
    [DataMember(Order = 22)] public int LoginReconnectGraceSeconds { get; set; }

    /// <summary>
    /// Message of the day, shown in chat when a player enters a channel. One entry per
    /// line; empty disables it. The channel name/description line is sent regardless.
    /// </summary>
    [DataMember(Order = 23)] public List<string> MessageOfTheDay { get; set; }

    /// <summary>
    /// Multiplies the experience a finished song pays. The level thresholds cannot be
    /// changed (the client draws its EXP bar from its own copy of the table), so this is
    /// the knob for how fast levelling goes.
    /// </summary>
    [DataMember(Order = 24)] public uint ExperienceMultiplier { get; set; }

    /// <summary>
    /// Multiplies the MAX (money) a finished song pays, the same way
    /// <see cref="ExperienceMultiplier"/> scales experience.
    /// </summary>
    [DataMember(Order = 29)] public uint MoneyMultiplier { get; set; }

    /// <summary>
    /// Extra MAX and EXP a Premium account earns, as a percentage. 100 = +100% (double),
    /// 0 = no premium bonus. Applied to the whole payout - base award plus equipment and
    /// booster bonuses - for anyone the client would label Premium.
    /// </summary>
    [DataMember(Order = 30)] public int PremiumRewardBonusPercent { get; set; }

    /// <summary>
    /// Chance, in percent, that picking the RANDOM disc procs DJ Mission Match rather
    /// than simply playing a random song. 0 disables missions, 100 makes every random
    /// pick a mission.
    /// </summary>
    [DataMember(Order = 31)] public int MissionMatchChancePercent { get; set; }

    /// <summary>
    /// Combo needed for one item-battle item drop. The interval is server policy: the
    /// client has no drop rule of its own, it only requests an item when it hits a note
    /// the server marked with OnCrItemInf. Its own 30-counter drives item LEVEL-UPS and
    /// counts note hits, not combo, so it is unrelated to this.
    ///
    /// The player's live state only reaches the server every five seconds, so a drop
    /// lands on the first report after the combo is crossed, never mid-note.
    /// </summary>
    [DataMember(Order = 34)] public int BattleItemComboInterval { get; set; }

    /// <summary>
    /// Millisecond adjustment to every judgment window, indexed by the room's match mode:
    /// 0 = freemode, 1 = RANKED, 2 = score battle, 3 = item battle, 4 = course. NEGATIVE
    /// is stricter, so index 1 is the one to tighten; positive is more forgiving. The client
    /// has no judgment table of its own online: it uses the 13 windows the server ships
    /// with each chart, so this needs no client-side change.
    /// </summary>
    [DataMember(Order = 32)] public List<int> JudgmentAdjustmentMsByMatchMode { get; set; }

    /// <summary>
    /// Collection discs awarded for finishing a chart on an EXACT judgment percentage.
    /// Empty disables them. See <see cref="AccuracyDiscRule"/> - the Code of each is the
    /// one value not proven from the client, so correct it if a disc draws wrong.
    /// </summary>
    [DataMember(Order = 33)] public List<AccuracyDiscRule> AccuracyDiscs { get; set; }

    /// <summary>
    /// Account-class flags granted to EVERY account on login, on top of whatever the
    /// database stores. Names come from AccountClassFlags and are case-insensitive:
    ///   Normal, Admin, GameMaster, Observer, MasterOfCeremonies, Challenger, Jjang,
    ///   Premium, PremiumAlternate, Credit, PcBang
    /// e.g. ["Premium"] makes the whole server premium without editing any rows. This is
    /// applied in memory only - it never writes the granted bits back to the database, so
    /// removing a name here removes the privilege again.
    /// </summary>
    [DataMember(Order = 25)] public List<string> DefaultAccountClassFlags { get; set; }

    /// <summary>
    /// Percentage of an equipped loadout's HP that reaches the gauge. 100 keeps the
    /// ItemStock values as written, 0 makes gear cosmetic so every player starts on the
    /// same gauge. See <see cref="LocalLobby.EquipmentHpPercent"/>.
    /// </summary>
    [DataMember(Order = 26)] public int EquipmentHpPercent { get; set; }

    /// <summary>
    /// Percentage of an item's <c>max</c> paid as bonus MAX (money) per song, from worn
    /// gear and armed MAX boosters. 0 disables the bonus.
    /// </summary>
    [DataMember(Order = 27)] public int EquipmentMaxPercent { get; set; }

    /// <summary>
    /// Percentage of an item's <c>exp</c> paid as bonus EXP per song, from worn gear and
    /// armed EXP boosters. Stacks with <see cref="ExperienceMultiplier"/>, which scales
    /// the base award rather than the item bonus. 0 disables the bonus.
    /// </summary>
    [DataMember(Order = 28)] public int EquipmentExperiencePercent { get; set; }

    public Setting()
    {
        Name = "DjMaxServer";
        ListenIpAddress = IPAddress.Loopback;
        // Korean client rejects channel ports > 9000 (client sub_433170 gates on
        // channel word@86 <= 0x2328). Its own default ServerAddress is localhost:3000,
        // so keep both channel ports low.
        ServerPort = 3000;
        SevenKeyServerPort = 3005;
        AdvertisedIpAddress = IPAddress.Loopback;
        DownloadUrl = "ftp://DJMAX:DJMAX@127.0.0.1/song/";
        // Captured payloads, the song catalog and the shop lists all live in DATA now;
        // the old separate game-info folder is gone.
        GameInfoDirectory = "DATA";
        GameInfoFallbackDiscId = 1;
        FtpEnabled = true;
        FtpListenIpAddress = IPAddress.Loopback;
        FtpPort = 21;
        FtpAdvertisedIpAddress = IPAddress.Loopback;
        FtpRootDirectory = "ftp";
        FtpUsername = "DJMAX";
        FtpPassword = "DJMAX";
        FtpFallbackChartPath = null;
        // HTTP is the default: the client's downloader is scheme-agnostic (sub_482AEF
        // probes Content-Length before falling back to FtpGetFileSize), and one web host
        // can serve both the songs and the client paks. Set ContentDelivery = Ftp to go
        // back to the embedded FTP server.
        ContentDelivery = ContentDeliveryMode.Http;
        ContentBaseUrl = "http://127.0.0.1:8080/";
        ContentSongPath = "song/";
        HttpContentEnabled = true;
        HttpContentListenIpAddress = IPAddress.Loopback;
        HttpContentPort = 8080;
        ContentPatchPath = "patch/";
        PatchDirectory = "patch";
        PatchManifestAutoBuild = true;
        PatchManifestFileName = "md5list.txt";
        PatchNewsFileName = "news.txt";
        // Off by default: the pipe covers a local launcher, and this one puts a
        // password exchange on the network.
        LoginApiEnabled = false;
        LoginApiListenIpAddress = IPAddress.Loopback;
        LoginApiPort = 8091;
        // Off by default: nothing opens a port unless the operator asks for it, and this
        // one is a LOCAL API with no TLS and no user authentication.
        StatusApiEnabled = false;
        StatusApiListenIpAddress = IPAddress.Loopback;
        StatusApiPort = 8090;
        StatusApiToken = string.Empty;
        SongCatalogPath = string.Empty;
        PatternsDirectory = null;
        ShopDataDirectory = "DATA";
        PakDirectory = "paks";
        AsyncEventSettings = new AsyncEventSettings();
        LoginPipeName = LocalLoginProtocol.DefaultPipeName;
        LoginReconnectGraceSeconds = 30;
        MessageOfTheDay = [];
        ExperienceMultiplier = 1;
        DefaultAccountClassFlags = ["Premium"];
        EquipmentHpPercent = 100;
        EquipmentMaxPercent = 100;
        EquipmentExperiencePercent = 100;
        MoneyMultiplier = 1;
        PremiumRewardBonusPercent = 100;
        MissionMatchChancePercent = 10;
        BattleItemComboInterval = BattleItemComboRewardTracker.DefaultComboInterval;
        JudgmentAdjustmentMsByMatchMode = [];
        AccuracyDiscs = CollectionDiscs.Default();
        PasswordPolicy = new PasswordPolicySetting();
        RewardRates = new RewardRateSetting();
        AccuracyDiscTolerance = CollectionDiscs.DefaultTolerance;
        LoginTicketLifetimeSeconds = 60;
        UnlockAllCourses = false;
    }

    public Setting(Setting setting)
    {
        Name = setting.Name;
        ListenIpAddress = setting.ListenIpAddress;
        ServerPort = setting.ServerPort;
        SevenKeyServerPort = setting.SevenKeyServerPort;
        AdvertisedIpAddress = setting.AdvertisedIpAddress;
        DownloadUrl = setting.DownloadUrl;
        GameInfoDirectory = setting.GameInfoDirectory;
        GameInfoFallbackDiscId = setting.GameInfoFallbackDiscId;
        FtpEnabled = setting.FtpEnabled;
        FtpListenIpAddress = setting.FtpListenIpAddress;
        FtpPort = setting.FtpPort;
        FtpAdvertisedIpAddress = setting.FtpAdvertisedIpAddress;
        FtpRootDirectory = setting.FtpRootDirectory;
        FtpUsername = setting.FtpUsername;
        FtpPassword = setting.FtpPassword;
        FtpFallbackChartPath = setting.FtpFallbackChartPath;
        ContentDelivery = setting.ContentDelivery;
        ContentBaseUrl = setting.ContentBaseUrl;
        ContentSongPath = setting.ContentSongPath;
        HttpContentEnabled = setting.HttpContentEnabled;
        HttpContentListenIpAddress = setting.HttpContentListenIpAddress;
        HttpContentPort = setting.HttpContentPort;
        ContentPatchPath = setting.ContentPatchPath;
        PatchDirectory = setting.PatchDirectory;
        PatchManifestAutoBuild = setting.PatchManifestAutoBuild;
        PatchManifestFileName = setting.PatchManifestFileName;
        PatchNewsFileName = setting.PatchNewsFileName;
        LoginApiEnabled = setting.LoginApiEnabled;
        LoginApiListenIpAddress = setting.LoginApiListenIpAddress;
        LoginApiPort = setting.LoginApiPort;
        StatusApiEnabled = setting.StatusApiEnabled;
        StatusApiListenIpAddress = setting.StatusApiListenIpAddress;
        StatusApiPort = setting.StatusApiPort;
        StatusApiToken = setting.StatusApiToken;
        SongCatalogPath = setting.SongCatalogPath;
        PatternsDirectory = setting.PatternsDirectory;
        ShopDataDirectory = setting.ShopDataDirectory;
        PakDirectory = setting.PakDirectory;
        AsyncEventSettings = new AsyncEventSettings(setting.AsyncEventSettings);
        LoginPipeName = setting.LoginPipeName;
        LoginReconnectGraceSeconds = setting.LoginReconnectGraceSeconds;
        MessageOfTheDay = [.. setting.MessageOfTheDay ?? []];
        ExperienceMultiplier = setting.ExperienceMultiplier;
        DefaultAccountClassFlags = [.. setting.DefaultAccountClassFlags ?? []];
        EquipmentHpPercent = setting.EquipmentHpPercent;
        EquipmentMaxPercent = setting.EquipmentMaxPercent;
        EquipmentExperiencePercent = setting.EquipmentExperiencePercent;
        MoneyMultiplier = setting.MoneyMultiplier;
        PremiumRewardBonusPercent = setting.PremiumRewardBonusPercent;
        MissionMatchChancePercent = setting.MissionMatchChancePercent;
        BattleItemComboInterval = setting.BattleItemComboInterval;
        JudgmentAdjustmentMsByMatchMode =
            [.. setting.JudgmentAdjustmentMsByMatchMode ?? []];
        AccuracyDiscs = [.. setting.AccuracyDiscs ?? []];
        PasswordPolicy = setting.PasswordPolicy ?? new PasswordPolicySetting();
        RewardRates = setting.RewardRates ?? new RewardRateSetting();
        AccuracyDiscTolerance = setting.AccuracyDiscTolerance;
        LoginTicketLifetimeSeconds = setting.LoginTicketLifetimeSeconds;
        UnlockAllCourses = setting.UnlockAllCourses;
    }
}
