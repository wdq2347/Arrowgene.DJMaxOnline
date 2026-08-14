using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// Korean LogInReq (id 0x1B), 43 wire bytes. Decrypted layout (reader starts at
/// offset 3): RequestCode u16 @3, UserId u32 @5, SessionValue u32 @9, and the
/// 30-byte session seed @13 echoed back from OnConnectAck. China was 53 bytes
/// with a 32-byte seed plus an 8-byte ClientToken.
/// </summary>
public sealed record LoginRequest(
    ushort RequestCode,
    uint UserId,
    uint SessionValue,
    byte[] CipherSeed);

public static class LogInReqPacket
{
    public const int SeedSize = 30;

    public static LoginRequest Parse(Packet packet)
    {
        if (packet.Id != PacketId.LogInReq)
        {
            throw new ArgumentException(
                $"Expected {PacketId.LogInReq}, received {packet.Id}.", nameof(packet));
        }

        DjMaxPacketReader reader = new(packet);
        LoginRequest request = new(
            reader.ReadUInt16(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadBytes(SeedSize));
        reader.EnsureComplete();
        return request;
    }
}
