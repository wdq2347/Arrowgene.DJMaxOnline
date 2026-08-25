using System.Text;
using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

public enum ChatMessageType : byte
{
    Lobby = 0,
    Room = 1,
    // The retail client displays types 2 and 5 as 30-second on-screen
    // announcements as well as colored chat-history entries (sub_42B83D).
    Notice = 2,
    // NOT DISPLAYABLE ON THIS CLIENT. sub_432D20 renders only 0, 1, 6 and 7:
    //   if ( *(_BYTE *)(a2 + 7) == 0 || == 1 || == 6 || == 7 )
    // anything else falls to the else branch, which builds an EMPTY string and throws the
    // decoded text away. Server announcements defaulted to this and so were invisible.
    // Use Lobby (0) for anything the player must actually see.
    System = 3,
    Alternate = 4,
    Alert = 5,
    Styled6 = 6,
    Styled7 = 7,
    Styled8 = 8
}

/// <summary>
/// EncodedText excludes an optional terminating zero. Retail welcome messages
/// are terminated; relayed player-chat messages in the captures are not.
/// </summary>
public sealed record ChatMessage(
    ChatMessageType Type,
    byte[] EncodedText,
    bool IsNullTerminated)
{
    public const string AdminDisplayName = "<GM>";

    /// <summary>
    /// The client's text encoding: CP949 (Korean ANSI). It is a pure ANSI application and
    /// reads chat bytes with the system code page, which is what the Korean client expects.
    ///
    /// This matters for anything the SERVER composes. Player chat is relayed as the bytes
    /// the client sent, so it was always correct; a server-authored line encoded as ASCII
    /// turned every Korean character - and the music note in a song title - into '?'.
    /// CP949 is a superset of ASCII, so plain English lines encode byte-for-byte as before.
    /// </summary>
    private static readonly Encoding ClientText = ResolveClientEncoding();

    private static Encoding ResolveClientEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch (Exception)
        {
            // No code-page provider on this platform. ASCII is what the server did before
            // and still renders English correctly, so degrade rather than fail to chat.
            return Encoding.ASCII;
        }
    }

    public string AsciiText => ClientText.GetString(EncodedText);

    /// <summary>
    /// Encodes a server-authored line for the client. Named for its callers' intent rather
    /// than the encoding - see <see cref="ClientText"/> for why it is not ASCII.
    /// </summary>
    public static ChatMessage FromAscii(
        string text,
        ChatMessageType type = ChatMessageType.Lobby)
    {
        return new ChatMessage(
            type, ClientText.GetBytes(text ?? string.Empty), IsNullTerminated: true);
    }

    /// <summary>
    /// Builds an ordinary lobby/room chat line without changing the player's real
    /// nickname. Accounts carrying the retail Admin bit are presented as &lt;GM&gt;
    /// in chat only; waiter, room, friend, and messenger records keep the nickname.
    /// </summary>
    public static ChatMessage FromPlayer(
        string nickname,
        uint accountClass,
        byte[] encodedText,
        ChatMessageType type)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        ArgumentNullException.ThrowIfNull(encodedText);

        // The tag PREFIXES the nickname, it does not replace it: replacing produced
        // "<GM> > message" with the speaker's name missing entirely. A nickname that
        // already carries the tag is left alone so it cannot double up.
        bool isAdmin = (accountClass & (uint)AccountClassFlags.Admin) != 0;
        string displayName =
            isAdmin && !nickname.StartsWith(AdminDisplayName, StringComparison.Ordinal)
                ? $"{AdminDisplayName} {nickname}"
                : nickname;
        // Same encoding as the relayed message body that follows it, so a Korean nickname
        // survives the prefix the server builds around it.
        byte[] prefix = ClientText.GetBytes($"{displayName} > ");
        byte[] text = new byte[prefix.Length + encodedText.Length];
        prefix.CopyTo(text, 0);
        encodedText.CopyTo(text, prefix.Length);
        return new ChatMessage(type, text, IsNullTerminated: false);
    }
}

public static class OnChatInfPacket
{
    private const int MessageTypeSize = 1;
    private const int TerminatorSize = 1;

    public static Packet Build(
        ChatMessage message,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.EncodedText);
        if (message.EncodedText.Contains((byte)0))
        {
            throw new ArgumentException(
                "Encoded chat text cannot contain an embedded terminator.",
                nameof(message));
        }

        int wireSize = checked(
            DjMaxPacketBuilder.PacketIdSize + DjMaxPacketBuilder.HeaderSize +
            MessageTypeSize + message.EncodedText.Length +
            (message.IsNullTerminated ? TerminatorSize : 0));
        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Dynamic(PacketMeta.OnChatInf, wireSize, control)
            .WriteByte((byte)message.Type)
            .WriteBytes(message.EncodedText);
        if (message.IsNullTerminated)
        {
            builder.WriteByte(0);
        }
        return builder.Build();
    }

    public static Packet BuildAscii(
        string text,
        ChatMessageType type = ChatMessageType.Lobby,
        byte control = ProtocolPadding.Unused)
    {
        return Build(ChatMessage.FromAscii(text, type), control);
    }

    public static ChatMessage Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (declaredWireSize != actualWireSize ||
            reader.Remaining < MessageTypeSize)
        {
            throw new InvalidDataException("Invalid chat packet framing.");
        }

        ChatMessageType type = (ChatMessageType)reader.ReadByte();
        byte[] text = reader.ReadBytes(reader.Remaining);
        bool isNullTerminated = text.Length != 0 && text[^1] == 0;
        if (isNullTerminated)
        {
            text = text[..^1];
        }

        return new ChatMessage(type, text, isNullTerminated);
    }
}
