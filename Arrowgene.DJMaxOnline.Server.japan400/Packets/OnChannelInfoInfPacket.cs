using System.Net;
using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

public sealed record ChannelInfo(
    uint ChannelId,
    string Name,
    string Description,
    IPAddress Address,
    ushort Port,
    ushort UserCount = 0,
    ushort Capacity = 300,
    ushort MinimumLevel = 0,
    ushort MaximumLevel = 98,
    ushort SubChannelCount = 1,
    ushort EntryVersion = 1,
    // Which chart variant this channel serves. Set explicitly by LocalChannelCatalog —
    // do NOT infer it from Name (the channels are SEOUL/TOKYO, not MANIA/LIGHT).
    SongKeyMode KeyMode = SongKeyMode.FiveKey,
    // Friendly server-owned label used in greetings. SEOUL/TOKYO remain the retail
    // channel keys on the wire; players should see "7KEY My Server", not those keys.
    string? DisplayName = null)
{
    public string FullName => string.IsNullOrWhiteSpace(DisplayName)
        ? Description.TrimStart('.')
        : DisplayName;
}

public static class OnChannelInfoInfPacket
{
    public const int EntrySize = 90;
    public const int NameSize = 33;
    public const int DescriptionSize = 33;
    private const int EntryPaddingSize = 2;

    public static Packet Build(
        IReadOnlyList<ChannelInfo> channels,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(channels);
        int wireSize = checked(
            DjMaxPacketBuilder.PacketIdSize + DjMaxPacketBuilder.HeaderSize +
            channels.Count * EntrySize);

        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Dynamic(PacketMeta.OnChannelInfoInf, wireSize, control);

        foreach (ChannelInfo channel in channels)
        {
            byte[] address = channel.Address.MapToIPv4().GetAddressBytes();
            builder
                .WriteUInt16(channel.SubChannelCount)
                .WriteUInt32(channel.ChannelId)
                .WriteUInt16(channel.EntryVersion)
                .WriteUInt16(channel.MinimumLevel)
                .WriteUInt16(channel.MaximumLevel)
                .WriteFixedAscii(channel.Name, NameSize)
                .WriteFixedAscii(channel.Description, DescriptionSize)
                .WriteUInt16(channel.UserCount)
                .WriteUInt16(channel.Capacity)
                .WriteBytes(address)
                .WriteUInt16(channel.Port)
                .WritePadding(EntryPaddingSize);
        }

        return builder.Build();
    }

    public static IReadOnlyList<ChannelInfo> Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint wireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (wireSize != actualWireSize || reader.Remaining % EntrySize != 0)
        {
            throw new InvalidDataException("Invalid channel-list framing.");
        }

        List<ChannelInfo> channels = new(reader.Remaining / EntrySize);
        while (reader.Remaining != 0)
        {
            ushort subChannelCount = reader.ReadUInt16();
            uint channelId = reader.ReadUInt32();
            ushort entryVersion = reader.ReadUInt16();
            ushort minimumLevel = reader.ReadUInt16();
            ushort maximumLevel = reader.ReadUInt16();
            string name = reader.ReadFixedAscii(NameSize);
            string description = reader.ReadFixedAscii(DescriptionSize);
            ushort userCount = reader.ReadUInt16();
            ushort capacity = reader.ReadUInt16();
            IPAddress address = reader.ReadIPv4();
            ushort port = reader.ReadUInt16();
            reader.Skip(EntryPaddingSize);

            channels.Add(new ChannelInfo(channelId, name, description, address, port,
                userCount, capacity, minimumLevel, maximumLevel, subChannelCount, entryVersion));
        }

        return channels;
    }
}
