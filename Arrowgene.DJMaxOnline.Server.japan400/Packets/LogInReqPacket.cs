using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

/// <summary>
/// JP LogInReq (id 0x1B), 53 wire bytes - the CHINA layout, not Korea's 43.
///
/// Decrypted, reader starting at offset 3: RequestCode u16 @3, UserId u32 @5,
/// SessionValue u32 @9, a 32-byte session seed @13 echoed back from OnConnectAck, then an
/// 8-byte ClientToken. 2+4+4+32+8 = 50 structured bytes, which is 53 less the 3-byte lead.
///
/// Korea shortened this to a 30-byte seed and dropped the token, giving 40; parsing a JP
/// packet with that layout leaves exactly the 10 unread bytes EnsureComplete reported.
/// The 32-byte seed also agrees with DjMaxCrypto.JapaneseSeedSize.
///
/// Confirmed against the wire:
///   header 40 01 48 4D 01 | 00 00 00 00 00 00 | B0 BC 7B 8F ... 0E 72 | FA 1A 00 D5 13 43 00 30
///   RequestCode 0x4801, UserId 0x0000014D, SessionValue 0, then 32 seed and 8 token bytes.
/// </summary>
public sealed record LoginRequest(
    ushort RequestCode,
    uint UserId,
    uint SessionValue,
    byte[] CipherSeed,
    byte[] ClientToken);

public static class LogInReqPacket
{
    public const int SeedSize = 32;

    /// <summary>Trailing token Korea dropped and this client kept from China.</summary>
    public const int ClientTokenSize = 8;

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
            reader.ReadBytes(SeedSize),
            reader.ReadBytes(ClientTokenSize));
        reader.EnsureComplete();
        return request;
    }
}
