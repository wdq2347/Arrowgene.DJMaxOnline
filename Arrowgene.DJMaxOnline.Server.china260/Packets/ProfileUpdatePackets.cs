using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

public sealed record UpdateUserAccountNickRequest(string Nickname);

public enum UpdateUserAccountNickResult : ushort
{
    Success = 0,
    Unavailable = 83,
    Rejected = 84
}

public static class UpdateUserAccountNickReqPacket
{
    public const int NicknameSize = 25;

    public static Packet Build(
        UpdateUserAccountNickRequest request,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DjMaxPacketBuilder.Fixed(PacketMeta.UpdateUserAccountNickReq, control)
            .WriteFixedAscii(request.Nickname, NicknameSize)
            .Build();
    }

    public static UpdateUserAccountNickRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UpdateUserAccountNickRequest request = new(
            reader.ReadFixedAscii(NicknameSize));
        reader.EnsureComplete();
        return request;
    }
}

// JPmax OnRegister declares OnUpdateUserAccountNickAck (0x31) as 13 wire bytes:
// result u16@3 followed by the registered eight-byte tail.
public static class OnUpdateUserAccountNickAckPacket
{
    public static Packet Build(
        UpdateUserAccountNickResult result,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnUpdateUserAccountNickAck, control)
            .WriteUInt16((ushort)result)
            .Build();

    public static UpdateUserAccountNickResult Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UpdateUserAccountNickResult result =
            (UpdateUserAccountNickResult)reader.ReadUInt16();
        reader.EnsureComplete();
        return result;
    }
}

/// <summary>
/// The JP profile form (client sender sub_436BA0, 18 wire bytes): a state byte@3, a
/// u16@4, then a u32@6 followed by the eight-byte tail. The value is sent one-based — the
/// same +1 encoding used by the icon field — so <see cref="Value"/> holds the decoded
/// zero-based value.
/// </summary>
public sealed record UpdateUserProfileRequest(
    byte State,
    ushort ProfileCode,
    uint Value);

public enum UpdateUserProfileResult : ushort
{
    Rejected = 0,
    Success = 60
}

public static class UpdateUserProfileReqPacket
{
    public static Packet Build(
        UpdateUserProfileRequest request,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DjMaxPacketBuilder.Fixed(PacketMeta.UpdateUserProfileReq, control)
            .WriteByte(request.State)
            .WriteUInt16(request.ProfileCode)
            .WriteUInt32(request.Value + 1)
            .Build();
    }

    public static UpdateUserProfileRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        byte state = reader.ReadByte();
        ushort profileCode = reader.ReadUInt16();
        uint oneBased = reader.ReadUInt32();
        reader.EnsureComplete();
        return new UpdateUserProfileRequest(
            state, profileCode, oneBased == 0 ? 0 : oneBased - 1);
    }
}

// JPmax OnRegister declares OnUpdateUserProfileAck (0x33) as 13 wire bytes:
// result u16@3 followed by the registered eight-byte tail.
public static class OnUpdateUserProfileAckPacket
{
    public static Packet Build(
        UpdateUserProfileResult result,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnUpdateUserProfileAck, control)
            .WriteUInt16((ushort)result)
            .Build();

    public static UpdateUserProfileResult Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UpdateUserProfileResult result =
            (UpdateUserProfileResult)reader.ReadUInt16();
        reader.EnsureComplete();
        return result;
    }
}
