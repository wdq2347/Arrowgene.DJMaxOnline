namespace Arrowgene.DJMaxOnline.Server.Japan400;

using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

public class PacketMeta
{
    private const int DynamicPacketMinimumWireSize = 7;

    public string Name { get; }
    public PacketId Id { get; }
    public int Size { get; }
    public PacketSource Source { get; }
    public bool IsDynamicSize { get; }

    public PacketMeta(PacketId id, int size, PacketSource source, bool isDynamicSize = false)
    {
        Name = id.ToString();
        Id = id;
        Size = size;
        Source = source;
        IsDynamicSize = isDynamicSize;
    }

    public PacketMeta(PacketId id, PacketSource source)
        : this(id, DynamicPacketMinimumWireSize, source, isDynamicSize: true)
    {
    }

    public string ToLog()
    {
        string size = IsDynamicSize ? "dynamic" : Size.ToString();
        return $"{Name}: [Id:{Id}(0x{(uint)Id:X})] [Size:{size}] [Source:{Source}]";
    }

    public static PacketMeta Get(PacketId packetId)
    {
        return Lookup[packetId];
    }

    public static bool TryGet(PacketId packetId, out PacketMeta packetMeta)
    {
        if (Lookup.TryGetValue(packetId, out PacketMeta? found))
        {
            packetMeta = found;
            return true;
        }

        packetMeta = null!;
        return false;
    }

    public static readonly PacketMeta OnPingTestInf = new(
        PacketId.OnPingTestInf,
        0x03,
        PacketSource.Server
    );

    // 23, not Korea's 15. This client appends the same 8-byte reserved tail to what it
    // SENDS as it expects on what it receives - observed on the wire as
    // 0A00 75 <12 bytes> 7681F94200F8D051, where reading only 15 left those 8 bytes to be
    // parsed as a packet id of their own (0x8176) and killed the connection.
    public static readonly PacketMeta ConnectReq = new(
        PacketId.ConnectReq,
        23,
        PacketSource.Client
    );

    public static readonly PacketMeta JpConnectConfirmReq = new(
        PacketId.JpConnectConfirmReq,
        // seed[32]@3 + account[21]@35 + password[17]@56 = 73, per sub_431760's stack.
        73,
        PacketSource.Client
    );

    public static readonly PacketMeta NetmarbleAuthenticateReq = new(
        PacketId.NetmarbleAuthenticateReq,
        PacketSource.Client
    );

    public static readonly PacketMeta OnConnectAck = new(
        PacketId.OnConnectAck,
        47,
        PacketSource.Server
    );

    public static readonly PacketMeta AuthenticateInSndAccReq = new(
        PacketId.AuthenticateInSndAccReq,
        0x43,
        PacketSource.Client
    );


    // JP CLIENT-TO-SERVER SIZES ARE PER-PACKET. THERE IS NO RULE.
    //
    // The 8-byte reserved tail is NOT reliable in this direction. Measured on the wire:
    //   WITH tail:    ConnectReq 15->23, LogInReq 45->53, KeepAuthenticateInReq 7->15,
    //                 InviteRejectReq 4->12
    //   WITHOUT tail: AliveAck 3, PingTestInf 3, ProbeObfuscated 144, CreateRoomReq 59
    // InviteRejectReq and CreateRoomReq are both ordinary lobby packets and they disagree,
    // so there is no split by size, direction or phase to key off.
    //
    // Deriving these from the rule was tried and reverted: it broke ProbeObfuscated and
    // CreateRoomReq, and a too-LARGE size is the worse failure - it stalls silently, then
    // absorbs following packets to reach its declared length, desyncing everything after.
    //
    // So: everything below stays at Korea's size until MEASURED against a RAW RECV byte
    // count. When one is wrong, PacketFactory.ReadPacket names the last packet it framed.
    public static readonly PacketMeta KeepAuthenticateInReq = new(
        PacketId.KeepAuthenticateInReq,
        // 15, NOT Korea's 7. Observed directly on the wire: the client sends
        // "1700 B703E845F7 4B5B C4D5CC559790" - id 0x17 then 13 more bytes. At 7 the
        // factory consumed 2+5 and read its next id from body offset 7 (4B 5B = 0x5B4B),
        // desyncing the stream permanently. JP keeps the China size here.
        15,
        PacketSource.Client
    );

    public static readonly PacketMeta OnKeepAuthenticateInAck = new(
        PacketId.OnKeepAuthenticateInAck,
        15, // Korean keepalive ack is 7 bytes (client size table id 22 = 7); China was 0xF=15
        PacketSource.Server
    );

    public static readonly PacketMeta OnAuthenticateInAck = new(
        PacketId.OnAuthenticateInAck,
        71, // Korean layout (client sub_431020 reads to offset 67); China was 0x5C=92
        PacketSource.Server
    );

    public static readonly PacketMeta VerifyCodeInf = new(
        PacketId.VerifyCodeInf,
        0x23,
        PacketSource.Client
    );

    public static readonly PacketMeta OnGameStartInf = new(
        PacketId.OnGameStartInf,
        33,
        PacketSource.Server
    );


    public static readonly PacketMeta OnChannelInfoInf = new(
        PacketId.OnChannelInfoInf,
        PacketSource.Server
    );

    public static readonly PacketMeta LogInReq = new(
        PacketId.LogInReq,
        // 53: id + control + 2 + two dwords + the 32-byte seed echo + the 8-byte tail.
        // Read off the client's own sender sub_431B20, which ends in sub_439530(53, ..).
        // Korea is 43 (30-byte seed, no tail).
        53,
        PacketSource.Client
    );

    public static readonly PacketMeta LogOutReq = new(
        PacketId.LogOutReq,
        13,
        PacketSource.Client
    );

    // Korean 0x1D is 7 wire bytes: id, one uninitialised byte, then a u32 target user id
    // (sender sub_431A30). It is what the client sends when the host clicks another player
    // in a room - a kick in the battle club. The China size of 15 made the server over-read
    // by 8 and desync. The parser only needs the leading u32.
    public static readonly PacketMeta UserInfoReq = new(
        PacketId.UserInfoReq,
        15,
        PacketSource.Client
    );

    public static readonly PacketMeta OnUserInfoAck = new(
        PacketId.OnUserInfoAck,
        892,
        PacketSource.Server
    );

    public static readonly PacketMeta InviteRejectReq = new(
        PacketId.InviteRejectReq,
        12, // Korean client sends id 0xA6 as 4 bytes on lobby entry; China was 0xC=12
        PacketSource.Client
    );

    public static readonly PacketMeta OnInviteReq = new(
        PacketId.OnInviteReq,
        24,
        PacketSource.Server
    );

    public static readonly PacketMeta OnDisconnectPeerInf = new(
        PacketId.OnDisconnectPeerInf,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUserInfoResNotFound = new(
        PacketId.OnUserInfoResNotFound,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnJoinerListStart = new(
        PacketId.OnJoinerListStart,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnJoinerListEnt = new(
        PacketId.OnJoinerListEnt,
        135,
        PacketSource.Server
    );

    public static readonly PacketMeta OnJoinerListEnd = new(
        PacketId.OnJoinerListEnd,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnQuickInviteAck = new(
        PacketId.OnQuickInviteAck,
        12,
        PacketSource.Server
    );

    public static readonly PacketMeta TeamControlReq = new(
        PacketId.TeamControlReq,
        12,
        PacketSource.Client
    );

    public static readonly PacketMeta ReadyReq = new(
        PacketId.ReadyReq,
        11,
        PacketSource.Client
    );

    // JPmax sends StartReq as 13 wire bytes: two opaque parameters at raw+3 followed by
    // the same eight-byte tail visible in the live start request capture.
    public static readonly PacketMeta StartReq = new(
        PacketId.StartReq,
        13,
        PacketSource.Client
    );

    // Korean PlayStartReq (sent when the chart finished loading, → CompleteLoad) is just
    // id + control = 3 bytes, no body. China's 0xB (11) over-read the following packets.
    public static readonly PacketMeta PlayStartReq = new(
        PacketId.PlayStartReq,
        11,
        PacketSource.Client
    );

    public static readonly PacketMeta PlaySkipReq = new(
        PacketId.PlaySkipReq,
        11,
        PacketSource.Client
    );

    // Korean PlayOverReq (sent after the result screen to leave play) is a bare 3-byte
    // signal, not China's 0xB (11). The wrong size over-read the trailing pings and
    // desynced the post-result stream.
    public static readonly PacketMeta PlayOverReq = new(
        PacketId.PlayOverReq,
        11,
        PacketSource.Client
    );

    // Korean PlayStateInf (live score/combo, streamed during play) is 22 wire bytes:
    // request/auth wrapper[8]@3 + state[11]@11. China's 0x1E (30) over-read 8 bytes
    // every tick and desynced the whole song.
    public static readonly PacketMeta PlayStateInf = new(
        PacketId.PlayStateInf,
        22,
        PacketSource.Client
    );

    public static readonly PacketMeta StageResultInf = new(
        PacketId.StageResultInf,
        0x3B,
        PacketSource.Client
    );

    // Korean LeaveRoomReq is a bare 3-byte signal (id + control), not China's 0xB=11. The
    // oversized meta meant "back to lobby" never framed, so leaving a room did nothing.
    public static readonly PacketMeta LeaveRoomReq = new(
        PacketId.LeaveRoomReq,
        11,
        PacketSource.Client
    );

    // JPmax sends ChangeDiscReq as 17 wire bytes: discId u32@3 + two settings bytes@7
    // (the first is difficulty), followed by an eight-byte tail. A live frame from the
    // client proved the 9-byte Korean declaration leaves the tail in the stream.
    public static readonly PacketMeta ChangeDiscReq = new(
        PacketId.ChangeDiscReq,
        17,
        PacketSource.Client
    );

    public static readonly PacketMeta UpdateUserAccountNickReq = new(
        PacketId.UpdateUserAccountNickReq,
        0x1C,
        PacketSource.Client
    );

    // JP sub_436BA0 sends state@3, profile code@4, value@6, then the eight-byte tail.
    // Declaring 13 consumed only three of those tail bytes and desynchronized the next
    // frame whenever the profile form was saved.
    public static readonly PacketMeta UpdateUserProfileReq = new(
        PacketId.UpdateUserProfileReq,
        18,
        PacketSource.Client
    );

    // DJMaxNet::OnRegister (sub_4300A0) registers both profile ACKs as
    // fixed 13-byte packets. Their handlers consume a 16-bit result at raw+3.
    // Both Korean profile acks are 5 wire bytes (result u16@3), not China's 13. The
    // client's handlers only clear their pending flag and forward to the active scene.
    public static readonly PacketMeta OnUpdateUserAccountNickAck = new(
        PacketId.OnUpdateUserAccountNickAck,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserProfileAck = new(
        PacketId.OnUpdateUserProfileAck,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnReserved34Inf = new(
        PacketId.OnReserved34Inf,
        12,
        PacketSource.Server
    );

    /// <summary>Room slot open/close broadcast; sub_434650 forwards it to the room scene.</summary>
    public static readonly PacketMeta OnSlotControlInf = new(
        PacketId.OnSlotControlInf,
        12,
        PacketSource.Server
    );

    /// <summary>Pre-match "good luck" ping; sub_436620 is an empty handler. Fixed 7.</summary>
    public static readonly PacketMeta OnGoodLuckInf = new(
        PacketId.OnGoodLuckInf,
        15,
        PacketSource.Server
    );

    /// <summary>Pre-match "good luck" list; sub_436630 is an empty handler. Fixed 103.</summary>
    public static readonly PacketMeta OnGoodLuckListInf = new(
        PacketId.OnGoodLuckListInf,
        103,
        PacketSource.Server
    );

    /// <summary>Billing/session auth result; sub_4369D0 stores six u32 at net+895136. Fixed 27.</summary>
    public static readonly PacketMeta OnBillingAuthInf = new(
        PacketId.OnBillingAuthInf,
        35,
        PacketSource.Server
    );

    public static readonly PacketMeta WChatReq = new(
        PacketId.WChatReq,
        PacketSource.Client
    );

    // sub_4342D0 reads the dynamic wire size at raw+3 and consumes one
    // two-byte course ID for every remaining entry.
    // Course acks. Sizes are the client's own registrations (sub_42F440). Each request
    // is gated behind a pending flag that ONLY its ack clears, so a missing or wrongly
    // sized ack permanently blocks that request for the rest of the session.
    public static readonly PacketMeta OnCourseRankAck = new(
        PacketId.OnCourseRankAck,
        2155,
        PacketSource.Server
    );

    public static readonly PacketMeta OnChangeCourseAck = new(
        PacketId.OnChangeCourseAck,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnContinueCourseAck = new(
        PacketId.OnContinueCourseAck,
        13,
        PacketSource.Server
    );

    // Named "Req" in the enum but the CLIENT receives it (registered at 11 bytes).
    public static readonly PacketMeta OnPostCourseItemReq = new(
        PacketId.OnPostCourseItemReq,
        19,
        PacketSource.Server
    );

    public static readonly PacketMeta OnAwardItemInf = new(
        PacketId.OnAwardItemInf,
        15,
        PacketSource.Server
    );

    public static readonly PacketMeta OnCourseListInf = new(
        PacketId.OnCourseListInf,
        PacketSource.Server
    );

    // Korean GetItemReq is a bare 3-byte signal: sub_4362A0 sends id 180 with no body and
    // raises a pending flag at net+894520 that only its ack clears. China's 11 made the
    // server eat eight bytes of whatever followed.
    public static readonly PacketMeta GetItemReq = new(
        PacketId.GetItemReq,
        11,
        PacketSource.Client
    );

    // Korean ItemLevelUpReq is likewise 3 bytes with no body (sub_436400, id 183,
    // pending flag net+894524).
    public static readonly PacketMeta ItemLevelUpReq = new(
        PacketId.ItemLevelUpReq,
        11,
        PacketSource.Client
    );

    // Korean UseItemReq is 4 bytes: one byte at +3 (sub_436510 writes v4 = a2 there,
    // sends size 4, pending flag net+894528). Not China's {itemId,value} pair.
    public static readonly PacketMeta UseItemReq = new(
        PacketId.UseItemReq,
        12,
        PacketSource.Client
    );

    // Korean UseEffectorInf (client's own effector config, sent during load) is 27 wire
    // bytes: config[24]@3, no reserved tail. China's 35 over-read into the next packets.
    public static readonly PacketMeta UseEffectorInf = new(
        PacketId.UseEffectorInf,
        35,
        PacketSource.Client
    );

    public static readonly PacketMeta MountItemReq = new(
        PacketId.MountItemReq,
        0x43,
        PacketSource.Client
    );

    public static readonly PacketMeta GetPresentItemReq = new(
        PacketId.GetPresentItemReq,
        0xF,
        PacketSource.Client
    );

    // The Korean client sends DeleteItemReq as 11 bytes: {itemId u32@3, value u32@7}.
    // The old 0x13 (19) over-read 8 bytes into the next packet and desynced the whole
    // stream — deleting an item froze the client.
    public static readonly PacketMeta DeleteItemReq = new(
        PacketId.DeleteItemReq,
        19,
        PacketSource.Client
    );

    public static readonly PacketMeta MsgRegisterUserReq = new(
        PacketId.MsgRegisterUserReq,
        0x24,
        PacketSource.Client
    );

    public static readonly PacketMeta PurchaseItemReq = new(
        PacketId.PurchaseItemReq,
        0x23,
        PacketSource.Client
    );

    public static readonly PacketMeta ResaleItemReq = new(
        PacketId.ResaleItemReq,
        19,
        PacketSource.Client
    );

    public static readonly PacketMeta OnUserIdInfoInf = new(
        PacketId.OnUserIdInfoInf,
        PacketSource.Server
    );

    public static readonly PacketMeta OnEnvironmentInf = new(
        PacketId.OnEnvironmentInf,
        0x13F,
        PacketSource.Server
    );

    public static readonly PacketMeta OnSystemInfoAck = new(
        PacketId.OnSystemInfoAck,
        69,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUserInfoInf = new(
        PacketId.OnUserInfoInf,
        0x8A,
        PacketSource.Server
    );

    public static readonly PacketMeta OnInventoryInfoInf = new(
        PacketId.OnInventoryInfoInf,
        0x2EF,
        PacketSource.Server
    );

    public static readonly PacketMeta OnMessengerInfoInf = new(
        PacketId.OnMessengerInfoInf,
        0x3B9,
        PacketSource.Server
    );

    public static readonly PacketMeta OnLogInAck = new(
        PacketId.OnLogInAck,
        47, // Korean lobby payload (client sub_4314F0); China was 47
        PacketSource.Server
    );

    public static readonly PacketMeta ChatInf = new(
        PacketId.ChatInf,
        PacketSource.Client
    );

    public static readonly PacketMeta OnWChatInf = new(
        PacketId.OnWChatInf,
        PacketSource.Server
    );

    public static readonly PacketMeta OnChatInf = new(
        PacketId.OnChatInf,
        PacketSource.Server
    );

    // Korean lobby room-grid add (id 0x39). Client sub_4336D0 copies a 48-byte
    // record from packet+3, so wire = id(2)+control(1)+record(48) = 51.

    public static readonly PacketMeta OnBigNewsInf = new(
        PacketId.OnBigNewsInf,
        0x165,
        PacketSource.Server
    );

    public static readonly PacketMeta OnCipherCommandInf = new(
        PacketId.OnCipherCommandInf,
        PacketSource.Server
    );

    // The DJ메신저 conversation line, in both directions. Dynamic on both halves: the
    // declared size is 15 + the text length, and the text carries no terminator - the
    // client writes its own at packet+size, into a 100-byte buffer.
    public static readonly PacketMeta MsgChatReq = new(
        PacketId.MsgChatReq,
        PacketSource.Client
    );

    public static readonly PacketMeta OnMsgChatInf = new(
        PacketId.OnMsgChatInf,
        PacketSource.Server
    );

    public static readonly PacketMeta OnGameTypeInf = new(
        PacketId.OnGameTypeInf,
        12,
        PacketSource.Server
    );

    public static readonly PacketMeta OnTeamControlInf = new(
        PacketId.OnTeamControlInf,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnWaiterInfoUpdateInf = new(
        PacketId.OnWaiterInfoUpdateInf,
        0x4C,
        PacketSource.Server
    );

    // 51 = 3 + the 48-byte room record sub_434390 memcpy's. This is the grid ADD on this
    // client, not Korea's 5-byte remove.
    public static readonly PacketMeta OnRoomInfoUpdateInf = new(
        PacketId.OnRoomInfoUpdateInf,
        51,
        PacketSource.Server
    );

    // The grid REMOVE. sub_4345D0 reads a single int16 at wire+3; JPmax registers
    // an additional eight-byte tail, so the complete wire packet is 13 bytes.
    public static readonly PacketMeta OnRoomInfoEraseInf = new(
        PacketId.OnRoomInfoEraseInf,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnInviteRejectAck = new(
        PacketId.OnInviteRejectAck,
        12,
        PacketSource.Server
    );

    public static readonly PacketMeta PingTestInf = new(
        PacketId.PingTestInf,
        0x3,
        PacketSource.Client
    );

    public static readonly PacketMeta CreateRoomReq = new(
        PacketId.CreateRoomReq,
        0x3B,
        PacketSource.Client
    );

    public static readonly PacketMeta OnRoomDescInf = new(
        PacketId.OnRoomDescInf,
        0x30,
        PacketSource.Server
    );

    public static readonly PacketMeta OnCreateRoomAck = new(
        PacketId.OnCreateRoomAck,
        0x34,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateJoinerInfoInf = new(
        PacketId.OnUpdateJoinerInfoInf,
        135,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPostJoinRoomInf = new(
        PacketId.OnPostJoinRoomInf,
        11,
        PacketSource.Server
    );


    public static readonly PacketMeta OnGameInfoInf = new(
        PacketId.OnGameInfoInf,
        PacketSource.Server
    );

    public static readonly PacketMeta OnJoinEventInf = new(
        PacketId.OnJoinEventInf,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUseEffectorInf = new(
        PacketId.OnUseEffectorInf,
        36,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUseMountItemInf = new(
        PacketId.OnUseMountItemInf,
        70,
        PacketSource.Server
    );

    public static readonly PacketMeta OnStartParameterInf = new(
        PacketId.OnStartParameterInf,
        20,
        PacketSource.Server
    );

    public static readonly PacketMeta OnStartInf = new(
        PacketId.OnStartInf,
        14,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPlayStartInf = new(
        PacketId.OnPlayStartInf,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPlaySkipInf = new(
        PacketId.OnPlaySkipInf,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnLoadCompleteInf = new(
        PacketId.OnLoadCompleteInf,
        12,
        PacketSource.Server
    );

    public static readonly PacketMeta OnCheckDataReq = new(
        PacketId.OnCheckDataReq,
        15,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserInventoryDefaultItemInf = new(
        PacketId.OnUpdateUserInventoryDefaultItemInf,
        199,
        PacketSource.Server
    );

    // JPmax registers 0x2E as a 127-byte present/gift-box refresh.  The payload is
    // userId@3 followed by ten {itemId, expiration, senderUserId} records at @7.
    public static readonly PacketMeta OnUpdateUserInventoryPresentItemInf = new(
        PacketId.OnUpdateUserInventoryPresentItemInf,
        127,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserIconInf = new(
        PacketId.OnUpdateUserIconInf,
        25,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyInf = new(
        PacketId.OnUpdateUserPropertyInf,
        73,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyLevelInf = new(
        PacketId.OnUpdateUserPropertyLevelInf,
        25,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyMoneyInf = new(
        PacketId.OnUpdateUserPropertyMoneyInf,
        21,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyRecordInf = new(
        PacketId.OnUpdateUserPropertyRecordInf,
        29,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyMiscInf = new(
        PacketId.OnUpdateUserPropertyMiscInf,
        49,
        PacketSource.Server
    );

    public static readonly PacketMeta OnStageResultExInf = new(
        PacketId.OnStageResultExInf,
        51,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPlayOverInf = new(
        PacketId.OnPlayOverInf,
        12,
        PacketSource.Server
    );

    /// <summary>Chat flood control; state byte at raw+3. Client handler sub_4327E0.</summary>
    public static readonly PacketMeta OnChatControlInf = new(
        PacketId.OnChatControlInf,
        12,
        PacketSource.Server
    );

    /// <summary>Event/room indicator; command u16@3 and value u16@5. sub_435110.</summary>
    public static readonly PacketMeta OnEventInfoInf = new(
        PacketId.OnEventInfoInf,
        15,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPlayStateInf = new(
        PacketId.OnPlayStateInf,
        15,
        PacketSource.Server
    );

    public static readonly PacketMeta OnReadyInf = new(
        PacketId.OnReadyInf,
        18,
        PacketSource.Server
    );

    public static readonly PacketMeta OnLeaveRoomAck = new(
        PacketId.OnLeaveRoomAck,
        12,
        PacketSource.Server
    );


    public static readonly PacketMeta OnLogOutAck = new(
        PacketId.OnLogOutAck,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta AuthenticateInSndeKeyAck = new(
        PacketId.AuthenticateInSndeKeyAck,
        20,
        PacketSource.Client
    );

    public static readonly PacketMeta OnResaleItemAck = new(
        PacketId.OnResaleItemAck,
        249,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPurchaseItemAck = new(
        PacketId.OnPurchaseItemAck,
        249,
        PacketSource.Server
    );

    public static readonly PacketMeta OnMsgGroupInf = new(
        PacketId.OnMsgGroupInf,
        233,
        PacketSource.Server
    );

    public static readonly PacketMeta OnMsgNotifyInf = new(
        PacketId.OnMsgNotifyInf,
        17,
        PacketSource.Server
    );

    public static readonly PacketMeta OnMsgBlkUserInf = new(
        PacketId.OnMsgBlkUserInf,
        243,
        PacketSource.Server
    );

    public static readonly PacketMeta OnMsgRegUserInf = new(
        PacketId.OnMsgRegUserInf,
        483,
        PacketSource.Server
    );

    // OnRegister fixes this ACK at 13 bytes; the client passes its raw+3
    // result word to the messenger UI.
    public static readonly PacketMeta OnMsgRegisterUserAck = new(
        PacketId.OnMsgRegisterUserAck,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnExpiredShopItemInf = new(
        PacketId.OnExpiredShopItemInf,
        483,
        PacketSource.Server
    );

    // Korean OnUserAlertInf (0xD4): result u32@3, flag u8@7, five u32 @8..27.
    // A zero result clears account-class bits 0x10000/0x20000 (sub_436B00).
    public static readonly PacketMeta OnUserAlertInf = new(
        PacketId.OnUserAlertInf,
        36,
        PacketSource.Server
    );

    public static readonly PacketMeta OnExpiredMountItemInf = new(
        PacketId.OnExpiredMountItemInf,
        131,
        PacketSource.Server
    );

    public static readonly PacketMeta OnDeleteItemAck = new(
        PacketId.OnDeleteItemAck,
        245,
        PacketSource.Server
    );

    public static readonly PacketMeta OnGetPresentItemAck = new(
        PacketId.OnGetPresentItemAck,
        365,
        PacketSource.Server
    );

    public static readonly PacketMeta OnMountItemAck = new(
        // id(2) + control(1) + result u16(2) + 64-byte equipped loadout. The client
        // (sub_439640) reads the result at wire+3 and, on success (167), copies 64
        // bytes from wire+5 — so the packet is 69 bytes, not 64.
        PacketId.OnMountItemAck,
        69,
        PacketSource.Server
    );

    public static readonly PacketMeta UseMountItemInf = new(
        PacketId.UseMountItemInf,
        67,
        PacketSource.Client
    );

    // Korean UseEffectorSetInf (mid-song effector change, streamed during play) is 11
    // wire bytes, not China's 19. The wrong size over-read 8 bytes and desynced the
    // whole song.
    public static readonly PacketMeta UseEffectorSetInf = new(
        PacketId.UseEffectorSetInf,
        19,
        PacketSource.Client
    );

    public static readonly PacketMeta OnUseEffectorSetInf = new(
        PacketId.OnUseEffectorSetInf,
        20,
        PacketSource.Server
    );

    public static readonly PacketMeta OnAlertCreditInf = new(
        PacketId.OnAlertCreditInf,
        15,
        PacketSource.Server
    );

    public static readonly PacketMeta OnCrItemInf = new(
        PacketId.OnCrItemInf,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnMissionStandItemInf = new(
        PacketId.OnMissionStandItemInf,
        22,
        PacketSource.Server
    );

    public static readonly PacketMeta OnGetItemAck = new(
        PacketId.OnGetItemAck,
        15,
        PacketSource.Server
    );

    public static readonly PacketMeta OnItemLevelUpAck = new(
        PacketId.OnItemLevelUpAck,
        14,
        PacketSource.Server
    );

    public static readonly PacketMeta OnGetItemFail = new(
        PacketId.OnGetItemFail,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnItemLevelUpFail = new(
        PacketId.OnItemLevelUpFail,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUseItemFail = new(
        PacketId.OnUseItemFail,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUseItemAck = new(
        PacketId.OnUseItemAck,
        24,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserAccountClassInf = new(
        PacketId.OnUpdateUserAccountClassInf,
        19,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserInventoryMountItemInf = new(
        PacketId.OnUpdateUserInventoryMountItemInf,
        71,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserInventoryShopItemInf = new(
        PacketId.OnUpdateUserInventoryShopItemInf,
        247,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserInventoryEventItemInf = new(
        PacketId.OnUpdateUserInventoryEventItemInf,
        135,
        PacketSource.Server
    );

    public static readonly PacketMeta OnRoomChangeInfoAck = new(
        PacketId.OnRoomChangeInfoAck,
        51,
        PacketSource.Server
    );

    // JP sub_4352C0 sends the quick-invite signal as 11 bytes: id/control plus the
    // client's normal eight-byte tail. The request has no structured body, but framing
    // it as the Korean three-byte form leaves that tail to be parsed as another packet.
    public static readonly PacketMeta QuickInviteReq = new(
        PacketId.QuickInviteReq,
        11,
        PacketSource.Client
    );

    public static readonly PacketMeta RoomChangeInfoReq = new(
        PacketId.RoomChangeInfoReq,
        0x32,
        PacketSource.Client
    );

    public static readonly PacketMeta OnJoinRoomAck = new(
        PacketId.OnJoinRoomAck,
        15,
        PacketSource.Server
    );

    // Korean JoinRoomReq is 17 wire bytes (roomIndex u16@3 + a 12-byte credential tail),
    // not China's 0x19=25. The oversized meta meant the request never framed, so a join
    // attempt got no reply at all and the client sat waiting.
    public static readonly PacketMeta JoinRoomReq = new(
        PacketId.JoinRoomReq,
        25,
        PacketSource.Client
    );

    public static readonly PacketMeta OnUserIdInfoAck = new(
        PacketId.OnUserIdInfoAck,
        PacketSource.Server
    );

    public static readonly PacketMeta OnAliveReq = new(
        PacketId.OnAliveReq,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta AliveAck = new(
        PacketId.AliveAck,
        0x3,
        PacketSource.Client
    );

    public static readonly PacketMeta OnPeerCountInf = new(
        PacketId.OnPeerCountInf,
        PacketSource.Server
    );

    // Dynamic UBS status: byte@7 followed by an optional text body at byte@8.
    public static readonly PacketMeta OnUbsAwardInfoInf = new(
        PacketId.OnUbsAwardInfoInf,
        PacketSource.Server
    );

    public static readonly PacketMeta OnWaiterInfoEraseInf = new(
        PacketId.OnWaiterInfoEraseInf,
        17,
        PacketSource.Server
    );

    public static readonly PacketMeta UserIdInfoReq = new(
        PacketId.UserIdInfoReq,
        PacketSource.Client
    );

    public static readonly PacketMeta OnChangeDiscInf = new(
        PacketId.OnChangeDiscInf,
        17,
        PacketSource.Server
    );

    // JP course requests carry their u16 selector at raw+3 and the eight-byte tail.
    // The matching acks clear their per-request pending flags, so a short meta both
    // desyncs the stream and makes the course scene appear to hang.
    public static readonly PacketMeta sub_434390 = new(
        PacketId.sub_434390,
        13,
        PacketSource.Client
    );

    public static readonly PacketMeta sub_434450 = new(
        PacketId.sub_434450,
        13,
        PacketSource.Client
    );

    public static readonly PacketMeta sub_434510 = new(
        PacketId.sub_434510,
        13,
        PacketSource.Client
    );

    // JP 0x8A has no structured body but is still an 11-byte frame.
    public static readonly PacketMeta sub_434620 = new(
        PacketId.sub_434620,
        11,
        PacketSource.Client
    );

    public static readonly PacketMeta sub_434B40 = new(
        PacketId.sub_434B40,
        // The JP equipment-room request carries its u16 selector at raw+3 plus tail.
        13,
        PacketSource.Client
    );

    // JP sub_435120 sends slot index at raw+3 in a 12-byte frame. The remaining eight
    // bytes are tail padding; using the Korean four-byte meta left them in the stream.
    public static readonly PacketMeta SlotControlReq = new(
        PacketId.SlotControlReq,
        12,
        PacketSource.Client
    );

    public static readonly PacketMeta OnSlotControlAck = new(
        PacketId.OnSlotControlAck,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta sub_436120 = new(
        PacketId.sub_436120,
        11,
        PacketSource.Client
    );

    public static readonly PacketMeta sub_4362D0 = new(
        PacketId.sub_4362D0,
        12,
        PacketSource.Client
    );

    public static readonly PacketMeta sub_437370 = new(
        PacketId.sub_437370,
        0x103,
        PacketSource.Client
    );

    // JP sub_436A60 sends iconId@3 followed by the eight-byte tail. The Korean seven-byte
    // declaration leaves that tail in the receive stream after an icon update.
    public static readonly PacketMeta sub_437900 = new(
        PacketId.sub_437900,
        15,
        PacketSource.Client
    );

    public static readonly PacketMeta ProbeObfuscated = new(
        PacketId.ProbeObfuscated,
        // 144, NOT Korea+8. CONFIRMED EXCEPTION to the tail rule.
        //
        // Measured: "RAW RECV 144 bytes ... F000005F70726F..." - id 0xF0 then 142 bytes,
        // and the body is partly ASCII ("_pro"), so this is not a normally framed
        // structured packet and carries no reserved tail.
        //
        // At 152 the factory waited for 8 more bytes, absorbed the following 3-byte
        // keepalives to make up the length, and desynced the stream permanently - every
        // later AliveAck then surfaced as an undefined packet id.
        144,
        PacketSource.Client
    );

    public static readonly PacketMeta UbsAccountAuthenticationReq = new(
        PacketId.UbsAccountAuthenticationReq,
        0xB,
        PacketSource.Client
    );

    // sub_42F440 registers 0x12D as eight bytes, but sub_42FB20 has no semantic
    // case for it. Keep the meta for diagnostics/capture parsing; it is not proof
    // of a shop-entry response for this executable.
    public static readonly PacketMeta OnUbsAccountAuthenResAck = new(
        PacketId.OnUbsAccountAuthenResAck,
        8,
        PacketSource.Server
    );

    // Legacy metadata from another client build. This executable neither registers
    // nor dispatches 0x130, and the local server does not currently send it.
    public static readonly PacketMeta OnUbsAwardAuthenAck = new(
        PacketId.OnUbsAwardAuthenAck,
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta sub_4323D0 = new(
        PacketId.sub_4323D0,
        0x26,
        PacketSource.Client
    );

    // Id 0x6F is the client's end-of-song StageResultInf (DJMaxNet::StageResultInf,
    // client send sub_437460), which is 59 bytes on the wire — NOT 70. Framing it as
    // 70 makes the server over-read 11 bytes into the following packet, desyncing the
    // receive stream post-song (it surfaces when leaving the room).
    // Korean end-of-song StageResultInf (0x6F, → SongCompleteHandler → FinishPlay) is 59
    // wire bytes. (An earlier 62-byte RAW RECV was a coalesced 59 + the separate 3-byte
    // PlayOverReq(0x6A); when they arrive split, a 62 meta eats the PlayOverReq.) The
    // handler only frames it — no body parse — so only the size matters. Both 0x6F and
    // the following 0x6A call FinishPlay, which is idempotent (phase already Waiting).
    public static readonly PacketMeta Test00 = new(
        PacketId.Test00,
        59,
        PacketSource.Client
    );

    private static readonly Dictionary<PacketId, PacketMeta> Lookup = new()
    {
        { PacketId.OnPingTestInf, OnPingTestInf },
        { PacketId.ConnectReq, ConnectReq },
        { PacketId.NetmarbleAuthenticateReq, NetmarbleAuthenticateReq },
        { PacketId.JpConnectConfirmReq, JpConnectConfirmReq },
        { PacketId.OnConnectAck, OnConnectAck },
        { PacketId.AuthenticateInSndAccReq, AuthenticateInSndAccReq },
        { PacketId.OnAuthenticateInAck, OnAuthenticateInAck },
        { PacketId.KeepAuthenticateInReq, KeepAuthenticateInReq },
        { PacketId.VerifyCodeInf, VerifyCodeInf },
        { PacketId.OnGameStartInf, OnGameStartInf },
        { PacketId.OnChannelInfoInf, OnChannelInfoInf },
        { PacketId.LogOutReq, LogOutReq },
        { PacketId.UserInfoReq, UserInfoReq },
        { PacketId.OnUserInfoAck, OnUserInfoAck },
        { PacketId.OnUserInfoResNotFound, OnUserInfoResNotFound },
        { PacketId.InviteRejectReq, InviteRejectReq },
        { PacketId.OnInviteReq, OnInviteReq },
        { PacketId.OnDisconnectPeerInf, OnDisconnectPeerInf },
        { PacketId.OnJoinerListStart, OnJoinerListStart },
        { PacketId.OnJoinerListEnt, OnJoinerListEnt },
        { PacketId.OnJoinerListEnd, OnJoinerListEnd },
        { PacketId.OnQuickInviteAck, OnQuickInviteAck },
        { PacketId.TeamControlReq, TeamControlReq },
        { PacketId.ReadyReq, ReadyReq },
        { PacketId.StartReq, StartReq },
        { PacketId.PlayStartReq, PlayStartReq },
        { PacketId.PlaySkipReq, PlaySkipReq },
        { PacketId.PlayOverReq, PlayOverReq },
        { PacketId.PlayStateInf, PlayStateInf },
        { PacketId.StageResultInf, StageResultInf },
        { PacketId.LeaveRoomReq, LeaveRoomReq },
        { PacketId.ChangeDiscReq, ChangeDiscReq },
        { PacketId.UpdateUserAccountNickReq, UpdateUserAccountNickReq },
        { PacketId.UpdateUserProfileReq, UpdateUserProfileReq },
        { PacketId.OnUpdateUserAccountNickAck, OnUpdateUserAccountNickAck },
        { PacketId.OnUpdateUserProfileAck, OnUpdateUserProfileAck },
        { PacketId.OnReserved34Inf, OnReserved34Inf },
        { PacketId.OnSlotControlInf, OnSlotControlInf },
        { PacketId.OnGoodLuckInf, OnGoodLuckInf },
        { PacketId.OnGoodLuckListInf, OnGoodLuckListInf },
        { PacketId.OnBillingAuthInf, OnBillingAuthInf },
        { PacketId.WChatReq, WChatReq },
        { PacketId.OnCourseRankAck, OnCourseRankAck },
        { PacketId.OnChangeCourseAck, OnChangeCourseAck },
        { PacketId.OnContinueCourseAck, OnContinueCourseAck },
        { PacketId.OnPostCourseItemReq, OnPostCourseItemReq },
        { PacketId.OnAwardItemInf, OnAwardItemInf },
        { PacketId.OnCourseListInf, OnCourseListInf },
        { PacketId.GetItemReq, GetItemReq },
        { PacketId.ItemLevelUpReq, ItemLevelUpReq },
        { PacketId.UseItemReq, UseItemReq },
        { PacketId.UseEffectorInf, UseEffectorInf },
        { PacketId.MountItemReq, MountItemReq },
        { PacketId.GetPresentItemReq, GetPresentItemReq },
        { PacketId.DeleteItemReq, DeleteItemReq },
        { PacketId.MsgRegisterUserReq, MsgRegisterUserReq },
        { PacketId.PurchaseItemReq, PurchaseItemReq },
        { PacketId.ResaleItemReq, ResaleItemReq },
        { PacketId.OnKeepAuthenticateInAck, OnKeepAuthenticateInAck },
        { PacketId.LogInReq, LogInReq },
        { PacketId.OnUserIdInfoInf, OnUserIdInfoInf },
        { PacketId.OnEnvironmentInf, OnEnvironmentInf },
        { PacketId.OnSystemInfoAck, OnSystemInfoAck },
        { PacketId.OnUserInfoInf, OnUserInfoInf },
        { PacketId.OnInventoryInfoInf, OnInventoryInfoInf },
        { PacketId.OnMessengerInfoInf, OnMessengerInfoInf },
        { PacketId.OnLogInAck, OnLogInAck },
        { PacketId.ChatInf, ChatInf },
        { PacketId.OnWChatInf, OnWChatInf },
        { PacketId.OnChatInf, OnChatInf },
        { PacketId.OnBigNewsInf, OnBigNewsInf },
        { PacketId.OnCipherCommandInf, OnCipherCommandInf },
        { PacketId.MsgChatReq, MsgChatReq },
        { PacketId.OnMsgChatInf, OnMsgChatInf },
        { PacketId.OnWaiterInfoUpdateInf, OnWaiterInfoUpdateInf },
        { PacketId.OnRoomInfoUpdateInf, OnRoomInfoUpdateInf },
        { PacketId.OnRoomInfoEraseInf, OnRoomInfoEraseInf },
        { PacketId.OnInviteRejectAck, OnInviteRejectAck },
        { PacketId.PingTestInf, PingTestInf },
        { PacketId.CreateRoomReq, CreateRoomReq },
        { PacketId.OnRoomDescInf, OnRoomDescInf },
        { PacketId.OnCreateRoomAck, OnCreateRoomAck },
        { PacketId.OnUpdateJoinerInfoInf, OnUpdateJoinerInfoInf },
        { PacketId.OnPostJoinRoomInf, OnPostJoinRoomInf },
        { PacketId.OnTeamControlInf, OnTeamControlInf },
        { PacketId.OnGameTypeInf, OnGameTypeInf },
        { PacketId.OnGameInfoInf, OnGameInfoInf },
        { PacketId.OnJoinEventInf, OnJoinEventInf },
        { PacketId.OnUseEffectorInf, OnUseEffectorInf },
        { PacketId.OnUseMountItemInf, OnUseMountItemInf },
        { PacketId.OnStartParameterInf, OnStartParameterInf },
        { PacketId.OnStartInf, OnStartInf },
        { PacketId.OnPlayStartInf, OnPlayStartInf },
        { PacketId.OnPlaySkipInf, OnPlaySkipInf },
        { PacketId.OnLoadCompleteInf, OnLoadCompleteInf },
        { PacketId.OnCheckDataReq, OnCheckDataReq },
        { PacketId.OnUpdateUserInventoryDefaultItemInf, OnUpdateUserInventoryDefaultItemInf },
        { PacketId.OnUpdateUserInventoryPresentItemInf, OnUpdateUserInventoryPresentItemInf },
        { PacketId.OnUpdateUserIconInf, OnUpdateUserIconInf },
        { PacketId.OnUpdateUserPropertyInf, OnUpdateUserPropertyInf },
        { PacketId.OnUpdateUserPropertyLevelInf, OnUpdateUserPropertyLevelInf },
        { PacketId.OnUpdateUserPropertyMoneyInf, OnUpdateUserPropertyMoneyInf },
        { PacketId.OnUpdateUserPropertyRecordInf, OnUpdateUserPropertyRecordInf },
        { PacketId.OnUpdateUserPropertyMiscInf, OnUpdateUserPropertyMiscInf },
        { PacketId.OnStageResultExInf, OnStageResultExInf },
        { PacketId.OnPlayOverInf, OnPlayOverInf },
        { PacketId.OnChatControlInf, OnChatControlInf },
        { PacketId.OnEventInfoInf, OnEventInfoInf },
        { PacketId.OnPlayStateInf, OnPlayStateInf },
        { PacketId.OnLeaveRoomAck, OnLeaveRoomAck },
        { PacketId.OnReadyInf, OnReadyInf },
        { PacketId.OnLogOutAck, OnLogOutAck },
        { PacketId.AuthenticateInSndeKeyAck, AuthenticateInSndeKeyAck },
        { PacketId.OnResaleItemAck, OnResaleItemAck },
        { PacketId.OnPurchaseItemAck, OnPurchaseItemAck },
        { PacketId.OnMsgGroupInf, OnMsgGroupInf },
        { PacketId.OnMsgNotifyInf, OnMsgNotifyInf },
        { PacketId.OnMsgBlkUserInf, OnMsgBlkUserInf },
        { PacketId.OnMsgRegUserInf, OnMsgRegUserInf },
        { PacketId.OnMsgRegisterUserAck, OnMsgRegisterUserAck },
        { PacketId.OnExpiredShopItemInf, OnExpiredShopItemInf },
        { PacketId.OnUserAlertInf, OnUserAlertInf },
        { PacketId.OnExpiredMountItemInf, OnExpiredMountItemInf },
        { PacketId.OnDeleteItemAck, OnDeleteItemAck },
        { PacketId.OnGetPresentItemAck, OnGetPresentItemAck },
        { PacketId.OnMountItemAck, OnMountItemAck },
        { PacketId.UseMountItemInf, UseMountItemInf },
        { PacketId.UseEffectorSetInf, UseEffectorSetInf },
        { PacketId.OnUseEffectorSetInf, OnUseEffectorSetInf },
        { PacketId.OnAlertCreditInf, OnAlertCreditInf },
        { PacketId.OnCrItemInf, OnCrItemInf },
        { PacketId.OnMissionStandItemInf, OnMissionStandItemInf },
        { PacketId.OnGetItemAck, OnGetItemAck },
        { PacketId.OnItemLevelUpAck, OnItemLevelUpAck },
        { PacketId.OnGetItemFail, OnGetItemFail },
        { PacketId.OnItemLevelUpFail, OnItemLevelUpFail },
        { PacketId.OnUseItemFail, OnUseItemFail },
        { PacketId.OnUseItemAck, OnUseItemAck },
        { PacketId.OnUpdateUserAccountClassInf, OnUpdateUserAccountClassInf },
        { PacketId.OnUpdateUserInventoryMountItemInf, OnUpdateUserInventoryMountItemInf },
        { PacketId.OnUpdateUserInventoryShopItemInf, OnUpdateUserInventoryShopItemInf },
        { PacketId.OnUpdateUserInventoryEventItemInf, OnUpdateUserInventoryEventItemInf },
        { PacketId.OnRoomChangeInfoAck, OnRoomChangeInfoAck },
        { PacketId.QuickInviteReq, QuickInviteReq },
        { PacketId.RoomChangeInfoReq, RoomChangeInfoReq },
        { PacketId.OnJoinRoomAck, OnJoinRoomAck },
        { PacketId.JoinRoomReq, JoinRoomReq },
        { PacketId.OnUserIdInfoAck, OnUserIdInfoAck },
        { PacketId.OnAliveReq, OnAliveReq },
        { PacketId.AliveAck, AliveAck },
        { PacketId.OnPeerCountInf, OnPeerCountInf },
        { PacketId.OnUbsAwardInfoInf, OnUbsAwardInfoInf },
        { PacketId.UserIdInfoReq, UserIdInfoReq },
        { PacketId.OnWaiterInfoEraseInf, OnWaiterInfoEraseInf },
        { PacketId.OnChangeDiscInf, OnChangeDiscInf },
        { PacketId.ProbeObfuscated, ProbeObfuscated },
        { PacketId.sub_434390, sub_434390 },
        { PacketId.sub_434450, sub_434450 },
        { PacketId.sub_434510, sub_434510 },
        { PacketId.sub_434620, sub_434620 },
        { PacketId.sub_434B40, sub_434B40 },
        { PacketId.SlotControlReq, SlotControlReq },
        { PacketId.OnSlotControlAck, OnSlotControlAck },
        { PacketId.sub_436120, sub_436120 },
        { PacketId.sub_4362D0, sub_4362D0 },
        { PacketId.sub_437370, sub_437370 },
        { PacketId.sub_437900, sub_437900 },
        { PacketId.UbsAccountAuthenticationReq, UbsAccountAuthenticationReq },
        { PacketId.OnUbsAccountAuthenResAck, OnUbsAccountAuthenResAck },
        { PacketId.OnUbsAwardAuthenAck, OnUbsAwardAuthenAck },
        { PacketId.sub_4323D0, sub_4323D0 },
        { PacketId.Test00, Test00 }
    };

    static PacketMeta()
    {
        ClientSendPacketCatalog.ValidatePacketMetadata();
        ReceivePacketCatalog.ValidateServerPacketMetadata();
    }
}
