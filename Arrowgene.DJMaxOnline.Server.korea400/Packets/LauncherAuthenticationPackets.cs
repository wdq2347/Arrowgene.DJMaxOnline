using System.Text;
using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

public sealed record LauncherAuthenticationRequest(
    string Ticket,
    byte[] CipherSeed);

/// <summary>
/// ConnectFromNM=0 request built by client sub_430C50. The third command-line
/// argument is copied to raw+3 and the connection seed is copied to raw+30.
/// </summary>
public static class JpConnectConfirmReqPacket
{
    public const int AccountFieldSize = 27;
    public const int SeedSize = 30;

    public static LauncherAuthenticationRequest Parse(Packet packet)
    {
        if (packet.Id != PacketId.JpConnectConfirmReq)
        {
            throw new ArgumentException(
                $"Expected {PacketId.JpConnectConfirmReq}, received {packet.Id}.",
                nameof(packet));
        }

        DjMaxPacketReader reader = new(packet);
        string account = reader.ReadFixedAscii(AccountFieldSize);
        byte[] seed = reader.ReadBytes(SeedSize);
        reader.EnsureComplete();
        return new LauncherAuthenticationRequest(account, seed);
    }
}

/// <summary>
/// ConnectFromNM=1 request built by client sub_430F50. The launcher decrypts the
/// Netmarble_ClipFormat clipboard payload, takes its fifth comma-delimited field,
/// and sends that ticket after the connection seed.
/// </summary>
public static class NetmarbleAuthenticateReqPacket
{
    public const int SeedSize = JpConnectConfirmReqPacket.SeedSize;

    public static LauncherAuthenticationRequest Parse(Packet packet)
    {
        if (packet.Id != PacketId.NetmarbleAuthenticateReq)
        {
            throw new ArgumentException(
                $"Expected {PacketId.NetmarbleAuthenticateReq}, received {packet.Id}.",
                nameof(packet));
        }

        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             (packet.Header?.Length ?? 0) + packet.Data.Length;
        if (declaredWireSize != actualWireSize)
        {
            throw new InvalidDataException(
                $"Netmarble auth declares {declaredWireSize} bytes, received {actualWireSize}.");
        }

        byte[] seed = reader.ReadBytes(SeedSize);
        byte[] encodedTicket = reader.ReadBytes(reader.Remaining);
        int terminator = Array.IndexOf(encodedTicket, (byte)0);
        int length = terminator >= 0 ? terminator : encodedTicket.Length;
        string ticket = Encoding.ASCII.GetString(encodedTicket, 0, length);
        reader.EnsureComplete();
        return new LauncherAuthenticationRequest(ticket, seed);
    }
}
