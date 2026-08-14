using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// Eight-byte messenger entry, stored by the client as a flat array of sixty at
/// net+893463 (sub_437AA0 memcpy's all 480 bytes of OnMsgRegUserInf over it) and searched
/// by user id at that same base by sub_4378C0. OnMsgNotifyInf updates the 16-bit status
/// at offset six, confirming the field boundary used here.
/// </summary>
/// <param name="Status">
/// Presence. <see cref="MessengerPresence.Online"/> is the only value that renders as
/// present, and it has to be right HERE as well as in the notify - the panel is drawn
/// from whatever this array holds.
/// </param>
public sealed record MessengerContact(
    uint UserId,
    ushort GroupIndex,
    ushort Status);

public sealed record MessengerSnapshot(
    IReadOnlyList<MessengerContact> Contacts,
    IReadOnlyList<uint> BlockedUserIds,
    IReadOnlyList<string> GroupNames)
{
    public static MessengerSnapshot Empty { get; } = new(
        Enumerable.Repeat(
            new MessengerContact(0, 0, 0),
            OnMessengerInfoInfPacket.ContactCount).ToArray(),
        new uint[OnMessengerInfoInfPacket.BlockedUserCount],
        Enumerable.Repeat(
            string.Empty,
            OnMessengerInfoInfPacket.GroupCount).ToArray());
}

public static class OnMessengerInfoInfPacket
{
    // The 950-byte snapshot is the concatenation of the same regions updated
    // by OnMsgRegUserInf (480), OnMsgBlkUserInf (240), and OnMsgGroupInf (230).
    public const int ContactCount = 60;
    public const int BlockedUserCount = 60;
    public const int GroupCount = 10;
    public const int GroupNameSize = 23;

    public static Packet Build(
        MessengerSnapshot messenger,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        ValidateCount(messenger.Contacts, ContactCount, nameof(messenger.Contacts));
        ValidateCount(
            messenger.BlockedUserIds,
            BlockedUserCount,
            nameof(messenger.BlockedUserIds));
        ValidateCount(messenger.GroupNames, GroupCount, nameof(messenger.GroupNames));

        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
            PacketMeta.OnMessengerInfoInf, control);
        foreach (MessengerContact contact in messenger.Contacts)
        {
            builder
                .WriteUInt32(contact.UserId)
                .WriteUInt16(contact.GroupIndex)
                .WriteUInt16(contact.Status);
        }
        foreach (uint userId in messenger.BlockedUserIds)
        {
            builder.WriteUInt32(userId);
        }
        foreach (string groupName in messenger.GroupNames)
        {
            builder.WriteFixedAscii(groupName, GroupNameSize);
        }

        return builder.Build();
    }

    public static MessengerSnapshot Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        MessengerContact[] contacts = new MessengerContact[ContactCount];
        for (int i = 0; i < contacts.Length; i++)
        {
            contacts[i] = new MessengerContact(
                reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt16());
        }

        uint[] blockedUserIds = new uint[BlockedUserCount];
        for (int i = 0; i < blockedUserIds.Length; i++)
        {
            blockedUserIds[i] = reader.ReadUInt32();
        }

        string[] groupNames = new string[GroupCount];
        for (int i = 0; i < groupNames.Length; i++)
        {
            groupNames[i] = reader.ReadFixedAscii(GroupNameSize);
        }

        reader.EnsureComplete();
        return new MessengerSnapshot(contacts, blockedUserIds, groupNames);
    }

    private static void ValidateCount<T>(
        IReadOnlyCollection<T> values,
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
