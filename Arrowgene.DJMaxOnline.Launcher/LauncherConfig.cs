using System.Text;
using Arrowgene.DJMaxOnline.Updater;

namespace Arrowgene.DJMaxOnline.Launcher;

internal sealed class LauncherConfig
{
    public const string FileName = "launcher.cfg";

    public bool SaveCredentials { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    /// <summary>
    /// The server's login API. One route for every case: a server on this machine is
    /// simply <c>http://127.0.0.1:8091/login</c>, a remote one is its public address.
    ///
    /// Prefer https for anything not on this machine - the request carries the account
    /// password, and over plain http it is readable by anything on the path.
    ///
    /// The server needs <c>LoginApiEnabled = true</c> for this to answer.
    /// </summary>
    public string LoginUrl { get; set; } = "http://127.0.0.1:8091/login";

    /// <summary>Rejects a login address the launcher cannot use, or null when it is fine.</summary>
    public static string? ValidateLoginUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "No login address is configured. Set loginUrl in " + FileName +
                   " to your server's login API, e.g. http://127.0.0.1:8091/login";
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"loginUrl must be an http:// or https:// address, not '{url}'.";
        }

        return null;
    }

    /// <summary>
    /// The update site, e.g. <c>http://your-server:8080/patch/</c>. This is where the
    /// checksum list, the patch files and the news all come from.
    ///
    /// It must be an http:// or https:// address. A folder path is rejected by
    /// <see cref="ValidateUpdateSource"/>: updates are served over the web so every
    /// player gets the same verified bytes, and a local folder only ever worked on the
    /// one machine that had it.
    /// </summary>
    /// <remarks>
    /// Defaults to the patch route of a server running with its own defaults, so a fresh
    /// install updates with no configuration at all. Point it at your public address when
    /// the server is not on this machine.
    /// </remarks>
    public string UpdateUrl { get; set; } = "http://127.0.0.1:8080/patch/";

    /// <summary>The checksum list, published in the same folder as the files it lists.</summary>
    public string UpdateManifest { get; set; } = Updater.UpdateManifest.DefaultFileName;

    /// <summary>
    /// The news file, published beside the checksum list. Blank keeps the launcher's
    /// built-in text.
    /// </summary>
    public string UpdateNews { get; set; } = NewsFeed.DefaultFileName;

    /// <summary>
    /// Rejects anything that is not a web address, with a message a player can act on.
    /// Returns null when the source is usable.
    /// </summary>
    public static string? ValidateUpdateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "No update site is configured. Set updateUrl in " + FileName +
                   " to your server's patch address, e.g. http://your-server:8080/patch/";
        }

        if (!Uri.TryCreate(source.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"updateUrl must be an http:// or https:// address, not '{source}'. " +
                   "Updates are served from the web server, not from a local folder.";
        }

        return null;
    }

    /// <summary>
    /// Optional locale emulator to launch the client through, e.g. Locale Emulator's
    /// LEProc.exe or ntleas.exe. The Korean client is a pure ANSI application - it converts
    /// its CP949 text with the SYSTEM ANSI codepage - so on a non-Korean Windows every
    /// Korean string is mojibake. There is no API to give a child process a different
    /// codepage, so the only per-process fix is to start it under one of these tools.
    ///
    /// Empty launches DJMax.exe directly, exactly as before.
    /// </summary>
    public string LocaleEmulator { get; set; } = string.Empty;

    /// <summary>
    /// Command line for <see cref="LocaleEmulator"/>. <c>{game}</c> is replaced with the
    /// quoted path to DJMax.exe, <c>{args}</c> with the ticket arguments, and
    /// <c>{profile}</c> with <see cref="LocaleProfile"/>.
    ///
    /// Locale Emulator has two forms, and which one works depends on where the profile is:
    ///
    ///   -run {game} {args}
    ///     Uses the PER-APPLICATION profile - the "DJMax.exe.le.config" that LEGUI writes
    ///     next to the game. Arguments are forwarded. THE PROFILE MUST EXIST FIRST: with no
    ///     saved profile, -run does not launch anything at all, it just opens the LEGUI
    ///     editor - so the game never starts and the login ticket goes nowhere.
    ///
    ///   -runas {profile} {game} {args}
    ///     Uses a GLOBAL profile from Locale Emulator's own LEConfig.xml, by GUID.
    ///     A per-application GUID does NOT work here; LEProc will silently do nothing.
    ///
    /// Both were verified by launching a program that dumps its command line.
    ///
    /// For ntleas use something like:
    ///   localeEmulatorArgs={game} {args} -cp:{codepage} -loc:{locale}
    /// </summary>
    public string LocaleEmulatorArgs { get; set; } = "-run {game} {args}";

    /// <summary>
    /// GLOBAL Locale Emulator profile GUID, substituted for <c>{profile}</c> when the
    /// template uses <c>-runas</c>. Copy it from the <c>Guid</c> attribute of a profile in
    /// Locale Emulator's own <c>LEConfig.xml</c> - NOT from a per-application
    /// <c>&lt;game&gt;.exe.le.config</c>, which <c>-runas</c> cannot resolve.
    ///
    /// Leave empty when using <c>-run</c> with a per-application profile.
    ///
    /// Either way the profile must be KOREAN (ko-KR). Locale Emulator ships Japanese
    /// profiles only, and a Japanese one makes the client read its CP949 text as
    /// Shift-JIS - mojibake, not a fix.
    /// </summary>
    public string LocaleProfile { get; set; } = string.Empty;

    /// <summary>
    /// The ANSI codepage the client's text should be read as, substituted for
    /// <c>{codepage}</c> in <see cref="LocaleEmulatorArgs"/>. 949 is Korean (CP949 /
    /// EUC-KR extended), which is what the Korean client's strings are encoded in.
    /// </summary>
    public int LocaleCodePage { get; set; } = 949;

    /// <summary>
    /// The locale name, substituted for <c>{locale}</c>. Tools spell this differently -
    /// "ko-KR" and "Korean" both appear - so it is a plain string rather than parsed.
    /// </summary>
    public string LocaleName { get; set; } = "ko-KR";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static LauncherConfig Load(string path)
    {
        LauncherConfig config = new();
        if (!File.Exists(path))
        {
            return config;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..];
            switch (key.ToLowerInvariant())
            {
                case "savecredentials":
                    config.SaveCredentials = bool.TryParse(value, out bool save) && save;
                    break;
                case "accountid":
                    config.AccountId = value;
                    break;
                case "password":
                    config.Password = value;
                    break;
                case "gamepath":
                    config.GamePath = value;
                    break;
                case "loginurl":
                    // Blank keeps the default rather than disabling login.
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        config.LoginUrl = value.Trim();
                    }
                    break;
                case "updateurl":
                    // Blank keeps the default rather than disabling updates: an older
                    // launcher.cfg written before this had a default would otherwise
                    // leave the updater switched off with no obvious reason.
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        config.UpdateUrl = value.Trim();
                    }
                    break;
                case "localeemulator":
                    config.LocaleEmulator = value.Trim();
                    break;
                case "localecodepage":
                    if (int.TryParse(value.Trim(), out int codePage) && codePage > 0)
                    {
                        config.LocaleCodePage = codePage;
                    }
                    break;
                case "localename":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        config.LocaleName = value.Trim();
                    }
                    break;
                case "localeemulatorargs":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        config.LocaleEmulatorArgs = value.Trim();
                    }
                    break;
                case "localeprofile":
                    config.LocaleProfile = value.Trim();
                    break;
                case "updatemanifest":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        config.UpdateManifest = value.Trim();
                    }
                    break;
                case "updatenews":
                    config.UpdateNews = value.Trim();
                    break;
            }
        }

        if (!config.SaveCredentials)
        {
            config.AccountId = string.Empty;
            config.Password = string.Empty;
        }
        return config;
    }

    public void Save(string path)
    {
        ValidateSingleLine(GamePath, nameof(GamePath));
        ValidateSingleLine(LoginUrl, nameof(LoginUrl));
        ValidateSingleLine(UpdateUrl, nameof(UpdateUrl));
        ValidateSingleLine(UpdateManifest, nameof(UpdateManifest));
        ValidateSingleLine(UpdateNews, nameof(UpdateNews));
        ValidateSingleLine(LocaleEmulator, nameof(LocaleEmulator));
        ValidateSingleLine(LocaleEmulatorArgs, nameof(LocaleEmulatorArgs));
        ValidateSingleLine(LocaleProfile, nameof(LocaleProfile));
        ValidateSingleLine(LocaleName, nameof(LocaleName));
        if (SaveCredentials)
        {
            ValidateSingleLine(AccountId, nameof(AccountId));
            ValidateSingleLine(Password, nameof(Password));
        }

        List<string> lines =
        [
            "# Arrowgene DJMAX Online launcher settings",
            "# Credentials are stored as plain text when saveCredentials=true.",
            $"saveCredentials={SaveCredentials.ToString().ToLowerInvariant()}",
            $"gamePath={GamePath}",
            string.Empty,
            "# Where the launcher logs in. The same address works for a server on this",
            "# machine and a remote one - only the host differs:",
            "#   loginUrl=http://127.0.0.1:8091/login      (this machine)",
            "#   loginUrl=https://play.example.com/login   (remote)",
            "# The server needs LoginApiEnabled = true. Prefer https off this machine:",
            "# the request carries your password.",
            $"loginUrl={LoginUrl}",
            string.Empty,
            "# Update site. Must be an http:// or https:// address - updates are served",
            "# by the web server so every player verifies the same bytes.",
            "#   updateUrl=http://your-server:8080/patch/",
            $"# The checksum list is read from <updateUrl>/{UpdateManifest}",
            $"# The news shown on this launcher is read from <updateUrl>/{UpdateNews}",
            $"updateUrl={UpdateUrl}",
            $"updateManifest={UpdateManifest}",
            $"updateNews={UpdateNews}",
            string.Empty,
            "# Optional locale emulator (Locale Emulator's LEProc.exe, ntleas.exe, ...).",
            "# The Korean client reads its CP949 text with the SYSTEM ANSI codepage, so on a",
            "# non-Korean Windows it needs one of these. Empty launches the game directly.",
            "# Placeholders: {game} = quoted path to DJMax.exe, {args} = the ticket",
            "# arguments, {profile} = localeProfile, {codepage} = localeCodePage,",
            "# {locale} = localeName.",
            "#",
            "# Korean (CP949) examples - uncomment ONE and set the path to your copy:",
            "#   Locale Emulator, per-application profile (the usual case):",
            @"#     localeEmulator=C:\calocalemu\LEProc.exe",
            "#     localeEmulatorArgs=-run {game} {args}",
            "#   Locale Emulator, global profile from LEConfig.xml:",
            @"#     localeEmulator=C:\calocalemu\LEProc.exe",
            "#     localeEmulatorArgs=-runas {profile} {game} {args}",
            "#     localeProfile=<Guid attribute from LEConfig.xml>",
            "#   ntleas:",
            @"#     localeEmulator=C:\Tools\ntleas\ntleas.exe",
            "#     localeEmulatorArgs={game} {args} -cp:{codepage} -loc:{locale}",
            "#",
            "# LOCALE EMULATOR - THE PROFILE MUST EXIST BEFORE THIS WORKS. With -run and no",
            "# saved profile for the game, LEProc does not launch anything: it opens the",
            "# LEGUI editor instead, so the game never starts and the login ticket is lost.",
            "# Right-click DJMax.exe once and save a profile (it writes DJMax.exe.le.config",
            "# beside the game), and -run then launches it and forwards the arguments.",
            "# -runas takes a GLOBAL profile GUID from LEConfig.xml only; a per-application",
            "# GUID silently does nothing.",
            "#",
            "# The profile must be KOREAN (ko-KR). Locale Emulator ships Japanese profiles",
            "# only, and a Japanese one makes the client read its CP949 text as Shift-JIS -",
            "# mojibake, not a fix.",
            "#",
            "# Leave localeEmulator empty to launch DJMax.exe directly.",
            $"localeEmulator={LocaleEmulator}",
            $"localeEmulatorArgs={LocaleEmulatorArgs}",
            $"localeProfile={LocaleProfile}",
            $"localeCodePage={LocaleCodePage}",
            $"localeName={LocaleName}"
        ];
        if (SaveCredentials)
        {
            lines.Add($"accountId={AccountId}");
            lines.Add($"password={Password}");
        }

        string directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void ValidateSingleLine(string value, string name)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new InvalidDataException($"{name} cannot contain a line break.");
        }
    }
}
