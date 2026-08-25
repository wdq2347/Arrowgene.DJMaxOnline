using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

public enum ConnectResult : ushort
{
    /// <summary>
    /// The value the clean China 2.60 id-9 handler (sub_4319D0) tests for. The supplied
    /// capture's modified client uses server packet id 0x0A, but its successful body still
    /// carries result 5.
    /// </summary>
    Accepted = 5
}

public static class OnConnectAckPacket
{
    /// <summary>
    /// Bytes of seed on the wire. The client copies exactly this many from offset 7
    /// (qmemcpy .. 0x20 in sub_4312C0); the first 30 are the cipher seed proper - 28 for
    /// the Mersenne Twister and an int16 XTEA sum seed - and the last two are unused by
    /// DjMaxCrypto.InitChinese.
    /// </summary>
    public const int SeedSize = 32;

    /// <summary>
    /// The reserved tail this client still expects. Korea dropped it; JP kept it, which is
    /// what makes its fixed packets 8 bytes longer than Korea's throughout. 47 total =
    /// 3 header + 2 result + 2 assigned id + 32 seed + 8 here.
    /// </summary>
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
