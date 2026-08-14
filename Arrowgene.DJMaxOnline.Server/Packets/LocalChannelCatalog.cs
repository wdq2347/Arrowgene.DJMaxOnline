using System.Net;
using System.Text;

namespace Arrowgene.DJMaxOnline.Server.Packets;

public static class LocalChannelCatalog
{
    private const uint LocalChannelNamespace = 0x00010000;

    public static IReadOnlyList<ChannelInfo> Create(Setting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.ServerPort == setting.SevenKeyServerPort)
        {
            throw new ArgumentException(
                "The 5-key and 7-key channels require distinct TCP ports.",
                nameof(setting));
        }

        IPAddress address = setting.AdvertisedIpAddress.MapToIPv4();
        if (address.Equals(IPAddress.Any))
        {
            throw new ArgumentException(
                "AdvertisedIpAddress must be a reachable IPv4 address, not 0.0.0.0.",
                nameof(setting));
        }

        string serverName = setting.Name.Trim();
        if (serverName.Length == 0 || serverName.Any(character =>
                character is < (char)0x20 or > (char)0x7E))
        {
            throw new ArgumentException(
                "Name must contain printable ASCII text.", nameof(setting));
        }

        return new[]
        {
            CreateChannel(
                setting.ServerPort, "SEOUL", SongKeyMode.FiveKey, serverName, address),
            CreateChannel(
                setting.SevenKeyServerPort, "TOKYO", SongKeyMode.SevenKey, serverName, address)
        };
    }

    private static ChannelInfo CreateChannel(
        ushort port,
        string channelKey,
        SongKeyMode keyMode,
        string serverName,
        IPAddress address)
    {
        string keyLabel = $"{(int)keyMode}KEY";
        string description = $".[{keyLabel}] {serverName}";
        if (Encoding.ASCII.GetByteCount(description) >= OnChannelInfoInfPacket.DescriptionSize)
        {
            throw new ArgumentException(
                $"Name is too long for the retail channel description; " +
                $"'{serverName}' must fit in " +
                $"{OnChannelInfoInfPacket.DescriptionSize - keyLabel.Length - 5} ASCII characters.",
                nameof(serverName));
        }

        return new ChannelInfo(
            ChannelId: ChannelIdForPort(port),
            Name: channelKey,
            Description: description,
            Address: address,
            Port: port,
            KeyMode: keyMode,
            DisplayName: $"[{keyLabel}] {serverName}");
    }

    public static uint ChannelIdForPort(ushort port) => LocalChannelNamespace | port;
}
