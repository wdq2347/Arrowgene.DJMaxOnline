using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

/// <summary>A client environment key/value pair handled by sub_434AE0.</summary>
public sealed record EnvironmentSetting(string Name, string Value);

public static class OnEnvironmentInfPacket
{
    // sub_434AE0 passes raw+3 and raw+63 to the environment parser.
    public const int NameSize = 60;
    public const int ValueSize = 256;

    // Client sub_42A392 compares this name against "DIFFMIX_FILTER" (0x523DB0)
    // and only then stores the value and reloads DiscStock.csv (sub_428FDC).
    // The per-difficulty enable flags at dword_6946C0 start zeroed, which hides
    // every chart (level forced to 98) until this setting arrives intact.
    public static EnvironmentSetting DifficultyMixFilter { get; } = new(
        "DIFFMIX_FILTER",
        "EZ;NM;HD;MX;SC");

    /// <summary>
    /// The client's downloader is WinINet (InternetOpenUrlA), and it probes the size with
    /// HttpQueryInfo(CONTENT_LENGTH) before falling back to FtpGetFileSize (sub_482AEF).
    /// It is therefore scheme-agnostic: ftp, http and https are all valid here.
    /// </summary>
    public static EnvironmentSetting DownloadUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !(string.Equals(uri.Scheme, Uri.UriSchemeFtp, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "DownloadURL must be an absolute ftp://, http:// or https:// URL.",
                nameof(value));
        }

        // The client compares this name literally in sub_434AE0. The setting
        // stored in DJMax.ini is called DownloadURL, but the network key is LOADURL.
        return new EnvironmentSetting("DOWNLOADURL", value);
    }

    public static Packet Build(
        EnvironmentSetting setting,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnEnvironmentInf, control)
            .WriteFixedAscii(
                setting.Name, NameSize, ProtocolPadding.Unused)
            .WriteFixedAscii(
                setting.Value,
                ValueSize,
                ProtocolPadding.Unused)
            .Build();
    }

    public static EnvironmentSetting Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        EnvironmentSetting setting = new(
            reader.ReadFixedAscii(NameSize),
            reader.ReadFixedAscii(ValueSize));
        reader.EnsureComplete();
        return setting;
    }
}
