using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

/// <summary>
/// The fixed-size BigNewsInf announcement consumed by sub_433960. The first
/// 16 structured bytes are ignored by that handler; the title and body begin
/// at raw wire offsets 19 and 100 respectively.
/// </summary>
public sealed record BigNewsMessage(string Title, string Body);

public static class OnBigNewsInfPacket
{
    public const int ReservedSize = 16;
    public const int TitleSize = 81;
    public const int BodySize = 257;

    public static Packet Build(
        BigNewsMessage message,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(message);
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnBigNewsInf, control)
            .WritePadding(ReservedSize)
            .WriteFixedAscii(message.Title, TitleSize)
            .WriteFixedAscii(message.Body, BodySize)
            .Build();
    }

    public static Packet Build(
        string body,
        string title = "",
        byte control = ProtocolPadding.Unused) =>
        Build(new BigNewsMessage(title ?? string.Empty, body ?? string.Empty), control);

    public static BigNewsMessage Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        reader.Skip(ReservedSize);
        string title = reader.ReadFixedAscii(TitleSize);
        string body = reader.ReadFixedAscii(BodySize);
        reader.EnsureComplete();
        return new BigNewsMessage(title, body);
    }
}
