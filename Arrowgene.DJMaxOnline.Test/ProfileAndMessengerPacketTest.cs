using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Test;

public class ProfileAndMessengerPacketTest
{
    [Test]
    public void NicknameUpdateUsesRetailRequestAndAckSizes()
    {
        UpdateUserAccountNickRequest request = new("NEWNAME");
        Packet requestPacket = UpdateUserAccountNickReqPacket.Build(request);
        Packet ackPacket = OnUpdateUserAccountNickAckPacket.Build(
            UpdateUserAccountNickResult.Success);

        Assert.Multiple(() =>
        {
            Assert.That(new PacketFactory().Write(requestPacket), Has.Length.EqualTo(28));
            Assert.That(
                UpdateUserAccountNickReqPacket.Parse(requestPacket),
                Is.EqualTo(request));
            Assert.That(new PacketFactory().Write(ackPacket), Has.Length.EqualTo(5));
            Assert.That(
                OnUpdateUserAccountNickAckPacket.Parse(ackPacket),
                Is.EqualTo(UpdateUserAccountNickResult.Success));
        });
    }

    [Test]
    public void ProfileUpdateUsesResultSixtyForSuccess()
    {
        // Korean layout: 10-byte request (byte@3, u16@4, u32@6 sent one-based) and a
        // 5-byte ack carrying just the result word.
        UpdateUserProfileRequest request = new(
            State: 1,
            ProfileCode: 27,
            Value: 1);
        Packet requestPacket = UpdateUserProfileReqPacket.Build(request);
        Packet ackPacket = OnUpdateUserProfileAckPacket.Build(
            UpdateUserProfileResult.Success);
        UpdateUserProfileRequest parsed =
            UpdateUserProfileReqPacket.Parse(requestPacket);

        Assert.Multiple(() =>
        {
            Assert.That(new PacketFactory().Write(requestPacket), Has.Length.EqualTo(10));
            Assert.That(parsed.State, Is.EqualTo(request.State));
            Assert.That(parsed.ProfileCode, Is.EqualTo(request.ProfileCode));
            Assert.That(parsed.Value, Is.EqualTo(request.Value));
            Assert.That(new PacketFactory().Write(ackPacket), Has.Length.EqualTo(5));
            Assert.That(
                (ushort)OnUpdateUserProfileAckPacket.Parse(ackPacket),
                Is.EqualTo(60));
        });
    }

    [Test]
    public void MessengerRequestAndAckUseRecoveredRetailFields()
    {
        MsgRegisterUserRequest request = new(
            "FRIEND",
            GroupIndex: 2,
            UserId: 123,
            MessengerOperation.BlockUser);
        Packet requestPacket = MsgRegisterUserReqPacket.Build(request);
        Packet ackPacket = OnMsgRegisterUserAckPacket.Build(
            MsgRegisterUserResult.UserNotFound);

        Assert.Multiple(() =>
        {
            Assert.That(new PacketFactory().Write(requestPacket), Has.Length.EqualTo(36));
            Assert.That(MsgRegisterUserReqPacket.Parse(requestPacket), Is.EqualTo(request));
            Assert.That(new PacketFactory().Write(ackPacket), Has.Length.EqualTo(5));
            Assert.That(
                OnMsgRegisterUserAckPacket.Parse(ackPacket),
                Is.EqualTo(MsgRegisterUserResult.UserNotFound));
        });
    }

    [Test]
    public void MessengerPartialSnapshotsMatchClientRegionSizes()
    {
        MessengerContact[] contacts = Enumerable.Repeat(
            new MessengerContact(0, 0, 0),
            OnMessengerInfoInfPacket.ContactCount).ToArray();
        // 30 eight-byte records, matching sub_4378C0's second loop - not packed ids.
        MessengerContact[] blocked = Enumerable
            .Repeat(new MessengerContact(0, 0, 0), OnMsgBlkUserInfPacket.RecordCount)
            .ToArray();
        string[] groups = Enumerable.Repeat(
            string.Empty,
            OnMessengerInfoInfPacket.GroupCount).ToArray();

        Packet contactsPacket = OnMsgRegUserInfPacket.Build(contacts);
        Packet blockedPacket = OnMsgBlkUserInfPacket.Build(blocked);
        Packet groupsPacket = OnMsgGroupInfPacket.Build(groups);

        Assert.Multiple(() =>
        {
            Assert.That(new PacketFactory().Write(contactsPacket), Has.Length.EqualTo(483));
            Assert.That(new PacketFactory().Write(blockedPacket), Has.Length.EqualTo(243));
            Assert.That(new PacketFactory().Write(groupsPacket), Has.Length.EqualTo(233));
            Assert.That(OnMsgRegUserInfPacket.Parse(contactsPacket), Is.EqualTo(contacts));
            Assert.That(OnMsgBlkUserInfPacket.Parse(blockedPacket), Is.EqualTo(blocked));
            Assert.That(OnMsgGroupInfPacket.Parse(groupsPacket), Is.EqualTo(groups));
        });
    }

    [Test]
    public void MessengerOperationsPersistAndReportTheClientsResultCodes()
    {
        // OnMsgRegisterUserAck's u16 is what the client acts on: only 194 is success, and
        // anything else leaves its friends panel unchanged. A local server hosts exactly
        // one account, so that account is the only resolvable target.
        LocalPlayerProfile profile = new() { UserId = 7, Nickname = "Blade" };
        string directory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        LocalPlayerStore store = new(profile, ShopCatalog.Load(directory));

        MsgRegisterUserRequest Add(string nickname, uint id, ushort group = 0) =>
            new(nickname, group, id, MessengerOperation.AddFriend);

        Assert.Multiple(() =>
        {
            // Resolvable by nickname or by id; unknown targets must not create a contact.
            Assert.That(store.Messenger(Add("Blade", 0)),
                Is.EqualTo(MsgRegisterUserResult.Success));
            Assert.That(store.Messenger(Add("Blade", 0)),
                Is.EqualTo(MsgRegisterUserResult.AlreadyFriend));
            Assert.That(store.Messenger(Add("Nobody", 0)),
                Is.EqualTo(MsgRegisterUserResult.UserNotFound));
            Assert.That(profile.Messenger.Contacts, Has.Count.EqualTo(1));

            Assert.That(
                store.Messenger(new MsgRegisterUserRequest("", 3, 7,
                    MessengerOperation.ChangeGroup)),
                Is.EqualTo(MsgRegisterUserResult.Success));
            Assert.That(profile.Messenger.Contacts[0].GroupIndex, Is.EqualTo(3));

            Assert.That(
                store.Messenger(new MsgRegisterUserRequest("", 0, 7,
                    MessengerOperation.BlockUser)),
                Is.EqualTo(MsgRegisterUserResult.Success));
            Assert.That(
                store.Messenger(new MsgRegisterUserRequest("", 0, 7,
                    MessengerOperation.BlockUser)),
                Is.EqualTo(MsgRegisterUserResult.AlreadyBlocked));

            Assert.That(
                store.Messenger(new MsgRegisterUserRequest("", 0, 7,
                    MessengerOperation.DeleteFriend)),
                Is.EqualTo(MsgRegisterUserResult.Success));
            Assert.That(profile.Messenger.Contacts, Is.Empty);
        });
    }

    [Test]
    public void MessengerSectionsPadToTheClientsFixedSlotCounts()
    {
        // The client's sections are fixed at 60/60/10; the builders reject any other
        // count, so the book has to pad rather than send what it happens to hold.
        MessengerBook book = new();
        book.Contacts.Add(new MessengerContactEntry { UserId = 9, GroupIndex = 2 });
        book.BlockedUserIds.Add(11);
        book.GroupNames.Add("Friends");

        Assert.Multiple(() =>
        {
            Assert.That(book.ContactSlots(), Has.Length.EqualTo(60));
            // 30 records here, not 60: sub_4378C0 walks net+893943 as 30 x 8 bytes.
            Assert.That(book.BlockedSlots(),
                Has.Length.EqualTo(OnMsgBlkUserInfPacket.RecordCount));
            Assert.That(book.BlockedSlots()[0].UserId, Is.EqualTo(11u));
            Assert.That(book.GroupSlots(), Has.Length.EqualTo(10));
            Assert.That(book.ContactSlots()[0].UserId, Is.EqualTo(9u));
            Assert.That(book.ContactSlots()[0].GroupIndex, Is.EqualTo(2));
            Assert.That(book.ContactSlots()[1].UserId, Is.EqualTo(0u), "unused slots empty");
            Assert.That(book.GroupSlots()[9], Is.EqualTo(string.Empty));
            Assert.DoesNotThrow(() => OnMsgRegUserInfPacket.Build(book.ContactSlots()));
            Assert.DoesNotThrow(() => OnMsgBlkUserInfPacket.Build(book.BlockedSlots()));
            Assert.DoesNotThrow(() => OnMsgGroupInfPacket.Build(book.GroupSlots()));
        });
    }

    [Test]
    public void PresenceNotificationIsNineBytesWithNoReservedTail()
    {
        // sub_437950 reads the contact id at +3 and writes the word at +7 into contact
        // record +6. The China build carried an extra 8-byte tail, which cannot fit the
        // 9-byte Korean meta - so every presence send threw and contacts stayed offline.
        byte[] wire = new PacketFactory().Write(
            OnMsgNotifyInfPacket.Build(new MessengerPresence(7, 2)));

        Assert.That(wire, Has.Length.EqualTo(9));
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(7u), "contact id");
            Assert.That(BitConverter.ToUInt16(wire, 7), Is.EqualTo(2), "status -> record+6");
        });
    }

    [Test]
    public void OnlyOnePresenceValuePassesTheClientsOnlineTest()
    {
        // The client applies one compound test to the status word in three places -
        // sub_48B621/sub_48AFEC ("ONLINE" vs "OFFLINE"), sub_489E8A (white vs grey), and
        // sub_489EDA, which refuses to open a conversation when it fails:
        //     (BYTE)status == 3 && (status & 0xFF00) == 0x100
        // Both halves are checked, so the obvious 1 fails twice over. Nothing else in the
        // client reads this word, which is why a contact that is genuinely present still
        // rendered offline and could not be messaged.
        static bool ClientReadsAsOnline(ushort status) =>
            (status & 0xFF) == 3 && (status & 0xFF00) == 0x100;

        Assert.That(ClientReadsAsOnline(MessengerPresence.Online), Is.True);
        Assert.That(ClientReadsAsOnline(MessengerPresence.Offline), Is.False);
        Assert.That(
            Enumerable.Range(0, ushort.MaxValue + 1)
                .Where(value => ClientReadsAsOnline((ushort)value)),
            Is.EqualTo(new[] { (int)MessengerPresence.Online }),
            "the test admits exactly one value out of the whole 16-bit range");
    }

    [Test]
    public void MessengerChatSizeMatchesTheClientsOwnArithmetic()
    {
        // sub_431FC0 sets the declared size to strlen(text) + 15 - it does NOT count a
        // terminator - and sub_432830 then writes its own zero at packet + size. Declaring
        // one byte more or less either truncates the line or desyncs the stream.
        byte[] text = System.Text.Encoding.ASCII.GetBytes("<GM> Blade > hi");
        byte[] wire = new PacketFactory().Write(MsgChatReqPacket.Build(
            new MessengerChatMessage(SenderUserId: 1, TargetUserId: 1, text)));

        Assert.That(wire, Has.Length.EqualTo(MessengerChatProtocol.TextOffset + text.Length));
        Assert.Multiple(() =>
        {
            // 30 bytes is exactly what the live client sent for this line.
            Assert.That(wire, Has.Length.EqualTo(30));
            Assert.That(BitConverter.ToUInt16(wire, 0), Is.EqualTo(0xFA));
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(30u), "size @3");
            Assert.That(BitConverter.ToUInt32(wire, 7), Is.EqualTo(1u), "sender @7");
            Assert.That(BitConverter.ToUInt32(wire, 11), Is.EqualTo(1u), "target @11");
            Assert.That(
                System.Text.Encoding.ASCII.GetString(wire, 15, text.Length),
                Is.EqualTo("<GM> Blade > hi"));
            Assert.That(
                wire[^1], Is.Not.EqualTo(0), "the text is unterminated on the wire");
        });
    }

    [Test]
    public void MessengerChatRoundTripsThroughTheReceiveFraming()
    {
        MessengerChatMessage message = new(
            SenderUserId: 9,
            TargetUserId: 4,
            System.Text.Encoding.ASCII.GetBytes("hello"));
        PacketFactory factory = new();
        byte[] wire = factory.Write(MsgChatReqPacket.Build(message));

        // Feed it back the way a socket would, so the dynamic size at +3 is what drives
        // the framing rather than any meta constant.
        factory.FillReadBuffer(wire);
        List<Packet> packets = factory.ReadPackets();

        Assert.That(packets, Has.Count.EqualTo(1));
        MessengerChatMessage parsed = MsgChatReqPacket.Parse(packets[0]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.SenderUserId, Is.EqualTo(9u));
            Assert.That(parsed.TargetUserId, Is.EqualTo(4u));
            Assert.That(parsed.EncodedText, Is.EqualTo(message.EncodedText));
        });
    }

    [Test]
    public void MessengerChatNeverOverrunsTheClientsHundredByteBuffer()
    {
        // sub_432830 memcpy's `size` bytes into a 100-byte stack buffer, so an
        // over-long line would smash the client's stack rather than just look wrong.
        Assert.That(
            MessengerChatProtocol.TextOffset + MessengerChatProtocol.MaximumTextLength,
            Is.LessThanOrEqualTo(MessengerChatProtocol.MaximumWireSize));

        Assert.Throws<ArgumentOutOfRangeException>(() => OnMsgChatInfPacket.Build(
            new MessengerChatMessage(
                1, 1, new byte[MessengerChatProtocol.MaximumTextLength + 1])));

        // Clamping must not split a code page 949 character in half.
        byte[] korean = OnMsgChatInfPacket.TextEncoding.GetBytes(new string('가', 60));
        byte[] clamped = OnMsgChatInfPacket.Clamp(korean);
        Assert.That(clamped, Has.Length.EqualTo(78), "an odd cut would split a character");
        Assert.That(
            OnMsgChatInfPacket.TextEncoding.GetString(clamped),
            Is.EqualTo(new string('가', 39)));
    }

    [Test]
    public void ContactListCarriesPresenceSoThePanelDrawsItOnLoad()
    {
        // sub_437AA0 memcpy's all 480 bytes of this packet straight over the contact
        // array, so record+6 comes from the list itself. A list that says zero draws every
        // row offline no matter what a later OnMsgNotifyInf says.
        byte[] wire = new PacketFactory().Write(OnMsgRegUserInfPacket.Build(
            [
                new MessengerContact(7, 1, MessengerPresence.Online),
                .. Enumerable.Repeat(
                    new MessengerContact(0, 0, 0),
                    OnMessengerInfoInfPacket.ContactCount - 1)
            ]));

        const int table = 3;
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt32(wire, table), Is.EqualTo(7u));
            Assert.That(BitConverter.ToUInt16(wire, table + 4), Is.EqualTo(1), "group");
            Assert.That(
                BitConverter.ToUInt16(wire, table + 6),
                Is.EqualTo(MessengerPresence.Online),
                "status must survive into record+6");
            Assert.That(
                wire.Length - table, Is.EqualTo(480),
                "the client copies exactly 480 bytes, i.e. 60 eight-byte records");
        });
    }
}
