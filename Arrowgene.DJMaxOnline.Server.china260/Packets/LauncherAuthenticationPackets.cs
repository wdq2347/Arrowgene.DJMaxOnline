using System.Text;
using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

public sealed record LauncherAuthenticationRequest(
    string Ticket,
    byte[] CipherSeed,
    string Password = "");

/// <summary>
/// ConnectFromNM=0 request built by client sub_430C50. The third command-line
/// argument is copied to raw+3 and the connection seed is copied to raw+30.
/// </summary>
public static class CnConnectConfirmReqPacket
{
    /// <summary>Account id, at +35 - after the echoed seed.</summary>
    public const int AccountFieldSize = 21;

    /// <summary>Password/token, at +56, filling the packet to 73.</summary>
    public const int PasswordFieldSize = 17;

    /// <summary>The OnConnectAck seed echoed straight back at +3.</summary>
    public const int SeedSize = 32;

    public static LauncherAuthenticationRequest Parse(Packet packet)
    {
        if (packet.Id != PacketId.CnConnectConfirmReq)
        {
            throw new ArgumentException(
                $"Expected {PacketId.CnConnectConfirmReq}, received {packet.Id}.",
                nameof(packet));
        }

        // Order matters: the seed comes FIRST here (sub_431760 memcpys it to +3 and only
        // then appends the credentials it parsed out of CommandLine).
        DjMaxPacketReader reader = new(packet);
        byte[] seed = reader.ReadBytes(SeedSize);
        string account = reader.ReadFixedAscii(AccountFieldSize);
        string password = reader.ReadFixedAscii(PasswordFieldSize);
        reader.EnsureComplete();
        return new LauncherAuthenticationRequest(account, seed, password);
    }
}

/// <summary>
/// ConnectFromNM=1 request built by client sub_430F50. The launcher decrypts the
/// Netmarble_ClipFormat clipboard payload, takes its fifth comma-delimited field,
/// and sends that ticket after the connection seed.
/// </summary>
public static class NetmarbleAuthenticateReqPacket
{
    public const int SeedSize = CnConnectConfirmReqPacket.SeedSize;

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
