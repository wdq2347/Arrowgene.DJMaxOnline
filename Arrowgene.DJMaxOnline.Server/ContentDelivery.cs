namespace Arrowgene.DJMaxOnline.Server;

/// <summary>How the client fetches song archives and client paks.</summary>
public enum ContentDeliveryMode
{
    /// <summary>The embedded read-only FTP server, the original scheme.</summary>
    Ftp,

    /// <summary>A web site - either the built-in HTTP server or a real host.</summary>
    Http
}

/// <summary>
/// Works out the URLs handed to the client and used by the launcher.
///
/// ONE site, two routes - there is no reason for more:
/// <code>
///   https://example.com/song/    - song archives, advertised to the client as DOWNLOADURL
///   https://example.com/patch/   - everything the launcher installs into the game folder,
///                                  including crc.pak and the system paks, plus the
///                                  checksum list and the news
/// </code>
/// The patch route mirrors the game folder, so a client pak needs no route of its own:
/// it is just a file that lives in the game folder like any other.
/// </summary>
public static class ContentDelivery
{
    /// <summary>The DOWNLOADURL sent to the client. Always ends in a slash.</summary>
    public static string SongUrl(Setting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting.ContentDelivery == ContentDeliveryMode.Http
            ? Combine(setting.ContentBaseUrl, setting.ContentSongPath)
            : WithTrailingSlash(setting.DownloadUrl);
    }

    /// <summary>
    /// The launcher's update route: patch files, the checksum list and the news. Only
    /// meaningful over HTTP; FTP mode has no launcher updates.
    /// </summary>
    public static string? PatchUrl(Setting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting.ContentDelivery == ContentDeliveryMode.Http
            ? Combine(setting.ContentBaseUrl, setting.ContentPatchPath)
            : null;
    }

    /// <summary>
    /// The base the built-in server should advertise when it is the one hosting. Uses the
    /// configured base when it already points somewhere sensible, so pointing at a real
    /// site is just a matter of setting ContentBaseUrl.
    /// </summary>
    public static string LocalBaseUrl(Setting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return $"http://{setting.HttpContentListenIpAddress}:{setting.HttpContentPort}/";
    }

    public static string Combine(string baseUrl, string path)
    {
        string root = WithTrailingSlash(baseUrl);
        string relative = (path ?? string.Empty).Trim().TrimStart('/');
        return relative.Length == 0 ? root : WithTrailingSlash(root + relative);
    }

    private static string WithTrailingSlash(string value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 || trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }
}
