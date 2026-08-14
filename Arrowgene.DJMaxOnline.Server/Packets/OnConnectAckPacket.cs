using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

public enum ConnectResult : ushort
{
    Accepted = 5
}

public static class OnConnectAckPacket
{
    public const int SeedSize = 32;
    private const int TrailingPaddingSize = 8;

    public static Packet Build(
        ReadOnlySpan<byte> cipherSeed,
        ushort assignedUserId,
        ConnectResult result = ConnectResult.Accepted,
        byte control = ProtocolPadding.Unused)
    {
        if (cipherSeed.Length != SeedSize)
        {
            throw new ArgumentException($"Cipher seed must be {SeedSize} bytes.", nameof(cipherSeed));
        }

        byte[] transmittedSeed = cipherSeed.ToArray();
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(transmittedSeed);
        uint second = BinaryPrimitives.ReadUInt32LittleEndian(transmittedSeed.AsSpan(sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(transmittedSeed, ~first);
        BinaryPrimitives.WriteUInt32LittleEndian(transmittedSeed.AsSpan(sizeof(uint)), ~second);

        return DjMaxPacketBuilder.Fixed(PacketMeta.OnConnectAck, control)
            .WriteUInt16((ushort)result)
            .WriteUInt16(assignedUserId)
            .WriteBytes(transmittedSeed)
            .WritePadding(TrailingPaddingSize, ProtocolPadding.Unused)
            .Build();
    }

    public static (ConnectResult Result, ushort AssignedUserId, byte[] CipherSeed) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        ConnectResult result = (ConnectResult)reader.ReadUInt16();
        ushort assignedUserId = reader.ReadUInt16();
        byte[] seed = reader.ReadBytes(SeedSize);
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(seed);
        uint second = BinaryPrimitives.ReadUInt32LittleEndian(seed.AsSpan(sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(seed, ~first);
        BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(sizeof(uint)), ~second);
        reader.Skip(TrailingPaddingSize);
        reader.EnsureComplete();
        return (result, assignedUserId, seed);
    }
}
