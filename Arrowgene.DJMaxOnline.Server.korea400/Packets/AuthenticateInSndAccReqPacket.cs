using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

public sealed record AuthenticationRequest(
    byte[] EncodedAccountId,
    byte[] EncodedPassword,
    byte[] CipherSeed);

/// <summary>
/// Account authentication request as laid out from raw wire offset 3. The
/// account field begins in the clear header and continues into encrypted data,
/// which is why this must be read through <see cref="DjMaxPacketReader"/>.
/// </summary>
public static class AuthenticateInSndAccReqPacket
{
    public const int EncodedAccountIdSize = 21;
    public const int EncodedPasswordSize = 11;

    public static Packet Build(
        AuthenticationRequest request,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLength(request.EncodedAccountId, EncodedAccountIdSize, nameof(request.EncodedAccountId));
        ValidateLength(request.EncodedPassword, EncodedPasswordSize, nameof(request.EncodedPassword));
        ValidateLength(request.CipherSeed, OnConnectAckPacket.SeedSize, nameof(request.CipherSeed));

        return DjMaxPacketBuilder.Fixed(PacketMeta.AuthenticateInSndAccReq, control)
            .WriteBytes(request.EncodedAccountId)
            .WriteBytes(request.EncodedPassword)
            .WriteBytes(request.CipherSeed)
            .Build();
    }

    public static AuthenticationRequest Parse(Packet packet)
    {
        if (packet.Id != PacketId.AuthenticateInSndAccReq)
        {
            throw new ArgumentException(
                $"Expected {PacketId.AuthenticateInSndAccReq}, received {packet.Id}.",
                nameof(packet));
        }

        DjMaxPacketReader reader = new(packet);
        byte[] accountId = reader.ReadBytes(EncodedAccountIdSize);
        byte[] password = reader.ReadBytes(EncodedPasswordSize);
        byte[] seed = reader.ReadBytes(OnConnectAckPacket.SeedSize);
        reader.EnsureComplete();
        return new AuthenticationRequest(accountId, password, seed);
    }

    private static void ValidateLength(byte[] value, int expected, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != expected)
        {
            throw new ArgumentException(
                $"Field must be exactly {expected} bytes, received {value.Length}.",
                parameterName);
        }
    }
}
