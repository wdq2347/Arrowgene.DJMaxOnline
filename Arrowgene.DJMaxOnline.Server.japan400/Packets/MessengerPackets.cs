using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

public enum MessengerOperation : ushort
{
    AddFriend = 0,
    DeleteFriend = 1,
    ChangeGroup = 2,
    BlockUser = 3,
    UnblockUser = 4
}

public sealed record MsgRegisterUserRequest(
    string Nickname,
    ushort GroupIndex,
    uint UserId,
    MessengerOperation Operation);

public enum MsgRegisterUserResult : ushort
{
    Success = 194,
    OperationFailed195 = 195,
    OperationFailed196 = 196,
    UserNotFound = 197,
    FriendListFull = 198,
    OperationFailed199 = 199,
    AlreadyFriend = 200,
    OperationFailed201 = 201,
    OperationFailed202 = 202,
    AlreadyBlocked = 203
}

public static class MsgRegisterUserReqPacket
{
    public const int NicknameSize = 25;

    public static Packet Build(
        MsgRegisterUserRequest request,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DjMaxPacketBuilder.Fixed(PacketMeta.MsgRegisterUserReq, control)
            .WriteFixedAscii(request.Nickname, NicknameSize)
            .WriteUInt16(request.GroupIndex)
            .WriteUInt32(request.UserId)
            .WriteUInt16((ushort)request.Operation)
            .Build();
    }

    public static MsgRegisterUserRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        MsgRegisterUserRequest request = new(
            reader.ReadFixedAscii(NicknameSize),
            reader.ReadUInt16(),
            reader.ReadUInt32(),
            (MessengerOperation)reader.ReadUInt16());
        reader.EnsureComplete();
        return request;
    }
}

public static class OnMsgRegisterUserAckPacket
{
    public static Packet Build(
        MsgRegisterUserResult result,
        byte control = ProtocolPadding.Unused) =>
        // Korean OnMsgRegisterUserAck is 5 wire bytes: result u16@3, no reserved tail.
        DjMaxPacketBuilder.Fixed(PacketMeta.OnMsgRegisterUserAck, control)
            .WriteUInt16((ushort)result)
            .Build();

    public static MsgRegisterUserResult Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        MsgRegisterUserResult result =
            (MsgRegisterUserResult)reader.ReadUInt16();
        reader.EnsureComplete();
        return result;
    }
}

/// <param name="Status">
/// The presence word the client keeps at contact record +6. See <see cref="Online"/>:
/// exactly one value renders as present, and everything else is OFFLINE.
/// </param>
public sealed record MessengerPresence(uint UserId, ushort Status)
{
    /// <summary>
    /// The ONLY value the client treats as online. Three separate places agree on the
    /// same compound test - <c>(BYTE)status == 3 &amp;&amp; (status &amp; 0xFF00) == 0x100</c>:
    /// sub_48B621/sub_48AFEC pick the literal "ONLINE"/"OFFLINE" caption with it,
    /// sub_489E8A picks white text over grey with it, and sub_489EDA refuses to open a
    /// conversation unless it passes. Both bytes are checked, so a plain 1 - which is
    /// what the server used to send - fails on both halves and reads as offline.
    /// </summary>
    public const ushort Online = 0x0103;

    /// <summary>Anything that is not <see cref="Online"/> renders offline; use zero.</summary>
    public const ushort Offline = 0;
}

public static class OnMsgNotifyInfPacket
{
    public static Packet Build(
        MessengerPresence presence,
        byte control = ProtocolPadding.Unused)
    {
        // Korean OnMsgNotifyInf is 9 wire bytes: userId u32@3, status u16@7, and NO
        // China reserved tail. sub_437950 looks the contact up by the id at +3 and writes
        // the word at +7 into contact record +6. The old 8-byte pad could not fit the
        // 9-byte meta, so every presence send threw before reaching the client - which is
        // why contacts always rendered offline.
        ArgumentNullException.ThrowIfNull(presence);
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnMsgNotifyInf, control)
            .WriteUInt32(presence.UserId)
            .WriteUInt16(presence.Status)
            .Build();
    }

    public static MessengerPresence Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        MessengerPresence presence = new(
            reader.ReadUInt32(), reader.ReadUInt16());
        reader.EnsureComplete();
        return presence;
    }
}

public static class OnMsgRegUserInfPacket
{
    public static Packet Build(
        IReadOnlyList<MessengerContact> contacts,
        byte control = ProtocolPadding.Unused)
    {
        ValidateCount(
            contacts,
            OnMessengerInfoInfPacket.ContactCount,
            nameof(contacts));
        DjMaxPacketBuilder builder =
            DjMaxPacketBuilder.Fixed(PacketMeta.OnMsgRegUserInf, control);
        foreach (MessengerContact contact in contacts)
        {
            builder
                .WriteUInt32(contact.UserId)
                .WriteUInt16(contact.GroupIndex)
                .WriteUInt16(contact.Status);
        }
        return builder.Build();
    }

    public static IReadOnlyList<MessengerContact> Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        MessengerContact[] contacts =
            new MessengerContact[OnMessengerInfoInfPacket.ContactCount];
        for (int i = 0; i < contacts.Length; i++)
        {
            contacts[i] = new MessengerContact(
                reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt16());
        }
        reader.EnsureComplete();
        return contacts;
    }

    private static void ValidateCount<T>(
        IReadOnlyCollection<T>? values,
        int expected,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count != expected)
        {
            throw new ArgumentException(
                $"Section must contain {expected} entries, received {values.Count}.",
                parameterName);
        }
    }
}

/// <summary>
/// The second messenger roster (0xF5). sub_437B00 copies its 240 bytes to net+893943, and
/// sub_4378C0 - the client's contact lookup - walks that region as **30 records of 8 bytes**
/// with the user id at +0, exactly like the 60-entry contact list at net+893463. It is not
/// a packed array of 60 ids: written that way the ids land on 4-byte spacing, so the client
/// sees only every other one and reads neighbouring entries as their trailing fields.
/// </summary>
public static class OnMsgBlkUserInfPacket
{
    /// <summary>Records the client indexes, from sub_4378C0's second loop bound.</summary>
    public const int RecordCount = 30;

    public static Packet Build(
        IReadOnlyList<MessengerContact> entries,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count != RecordCount)
        {
            throw new ArgumentException(
                $"Section must contain {RecordCount} records.", nameof(entries));
        }

        DjMaxPacketBuilder builder =
            DjMaxPacketBuilder.Fixed(PacketMeta.OnMsgBlkUserInf, control);
        foreach (MessengerContact entry in entries)
        {
            builder
                .WriteUInt32(entry.UserId)
                .WriteUInt16(entry.GroupIndex)
                .WriteUInt16(entry.Status);
        }
        return builder.Build();
    }

    public static IReadOnlyList<MessengerContact> Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        MessengerContact[] entries = new MessengerContact[RecordCount];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new MessengerContact(
                reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt16());
        }
        reader.EnsureComplete();
        return entries;
    }
}

public static class OnMsgGroupInfPacket
{
    public static Packet Build(
        IReadOnlyList<string> groupNames,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(groupNames);
        if (groupNames.Count != OnMessengerInfoInfPacket.GroupCount)
        {
            throw new ArgumentException(
                $"Section must contain {OnMessengerInfoInfPacket.GroupCount} entries.",
                nameof(groupNames));
        }

        DjMaxPacketBuilder builder =
            DjMaxPacketBuilder.Fixed(PacketMeta.OnMsgGroupInf, control);
        foreach (string groupName in groupNames)
        {
            builder.WriteFixedAscii(
                groupName,
                OnMessengerInfoInfPacket.GroupNameSize);
        }
        return builder.Build();
    }

    public static IReadOnlyList<string> Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        string[] groupNames =
            new string[OnMessengerInfoInfPacket.GroupCount];
        for (int i = 0; i < groupNames.Length; i++)
        {
            groupNames[i] = reader.ReadFixedAscii(
                OnMessengerInfoInfPacket.GroupNameSize);
        }
        reader.EnsureComplete();
        return groupNames;
    }
}
