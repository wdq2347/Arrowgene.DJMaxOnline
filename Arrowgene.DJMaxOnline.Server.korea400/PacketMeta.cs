namespace Arrowgene.DJMaxOnline.Server.Korea400;

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

    public static readonly PacketMeta ConnectReq = new(
        PacketId.ConnectReq,
        15,
        PacketSource.Client
    );

    public static readonly PacketMeta JpConnectConfirmReq = new(
        PacketId.JpConnectConfirmReq,
        60,
        PacketSource.Client
    );

    public static readonly PacketMeta NetmarbleAuthenticateReq = new(
        PacketId.NetmarbleAuthenticateReq,
        PacketSource.Client
    );

    public static readonly PacketMeta OnConnectAck = new(
        PacketId.OnConnectAck,
        37,
        PacketSource.Server
    );

    public static readonly PacketMeta AuthenticateInSndAccReq = new(
        PacketId.AuthenticateInSndAccReq,
        0x43,
        PacketSource.Client
    );

    public static readonly PacketMeta KeepAuthenticateInReq = new(
        PacketId.KeepAuthenticateInReq,
        7, // Korean keepalive is 7 bytes (id + 5-byte header, no data); China was 0xF=15
        PacketSource.Client
    );

    public static readonly PacketMeta OnKeepAuthenticateInAck = new(
        PacketId.OnKeepAuthenticateInAck,
        7, // Korean keepalive ack is 7 bytes (client size table id 22 = 7); China was 0xF=15
        PacketSource.Server
    );

    public static readonly PacketMeta OnAuthenticateInAck = new(
        PacketId.OnAuthenticateInAck,
        67, // Korean layout (client sub_431020 reads to offset 67); China was 0x5C=92
        PacketSource.Server
    );

    public static readonly PacketMeta VerifyCodeInf = new(
        PacketId.VerifyCodeInf,
        0x23,
        PacketSource.Client
    );

    public static readonly PacketMeta OnGameStartInf = new(
        PacketId.OnGameStartInf,
        25, // Korean layout (client sub_436A50 reads to offset 25); China had 33 (+8 pad)
        PacketSource.Server
    );


    public static readonly PacketMeta OnChannelInfoInf = new(
        PacketId.OnChannelInfoInf,
        PacketSource.Server
    );

    public static readonly PacketMeta LogInReq = new(
        PacketId.LogInReq,
        43, // Korean (30-byte seed, no ClientToken); China was 0x35=53
        PacketSource.Client
    );

    public static readonly PacketMeta LogOutReq = new(
        PacketId.LogOutReq,
        5,
        PacketSource.Client
    );

    // Korean 0x1D is 7 wire bytes: id, one uninitialised byte, then a u32 target user id
    // (sender sub_431A30). It is what the client sends when the host clicks another player
    // in a room - a kick in the battle club. The China size of 15 made the server over-read
    // by 8 and desync. The parser only needs the leading u32.
    public static readonly PacketMeta UserInfoReq = new(
        PacketId.UserInfoReq,
        7,
        PacketSource.Client
    );

    public static readonly PacketMeta OnUserInfoAck = new(
        PacketId.OnUserInfoAck,
        892,
        PacketSource.Server
    );

    public static readonly PacketMeta InviteRejectReq = new(
        PacketId.InviteRejectReq,
        4, // Korean client sends id 0xA6 as 4 bytes on lobby entry; China was 0xC=12
        PacketSource.Client
    );

    public static readonly PacketMeta OnInviteReq = new(
        PacketId.OnInviteReq,
        16,
        PacketSource.Server
    );

    public static readonly PacketMeta OnDisconnectPeerInf = new(
        PacketId.OnDisconnectPeerInf,
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUserInfoResNotFound = new(
        PacketId.OnUserInfoResNotFound,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnJoinerListStart = new(
        PacketId.OnJoinerListStart,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnJoinerListEnt = new(
        PacketId.OnJoinerListEnt,
        135,
        PacketSource.Server
    );

    public static readonly PacketMeta OnJoinerListEnd = new(
        PacketId.OnJoinerListEnd,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnQuickInviteAck = new(
        PacketId.OnQuickInviteAck,
        4,
        PacketSource.Server
    );

    public static readonly PacketMeta TeamControlReq = new(
        PacketId.TeamControlReq,
        4,
        PacketSource.Client
    );

    public static readonly PacketMeta ReadyReq = new(
        PacketId.ReadyReq,
        3,
        PacketSource.Client
    );

    // Korean StartReq is 5 wire bytes (2 opaque param bytes @3), not China's 13. The
    // old 0xD made the server wait for 8 bytes that never arrive, so the host's "start"
    // never framed and the chart could not begin.
    public static readonly PacketMeta StartReq = new(
        PacketId.StartReq,
        5,
        PacketSource.Client
    );

    // Korean PlayStartReq (sent when the chart finished loading, → CompleteLoad) is just
    // id + control = 3 bytes, no body. China's 0xB (11) over-read the following packets.
    public static readonly PacketMeta PlayStartReq = new(
        PacketId.PlayStartReq,
        3,
        PacketSource.Client
    );

    public static readonly PacketMeta PlaySkipReq = new(
        PacketId.PlaySkipReq,
        3,
        PacketSource.Client
    );

    // Korean PlayOverReq (sent after the result screen to leave play) is a bare 3-byte
    // signal, not China's 0xB (11). The wrong size over-read the trailing pings and
    // desynced the post-result stream.
    public static readonly PacketMeta PlayOverReq = new(
        PacketId.PlayOverReq,
        3,
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
        3,
        PacketSource.Client
    );

    // Korean ChangeDiscReq is 9 wire bytes: discId u32@3 + 2 settings bytes@7 (the first
    // is difficulty). China's 0x11 (17) over-read 8 bytes into the next packet and stalled
    // chart selection.
    public static readonly PacketMeta ChangeDiscReq = new(
        PacketId.ChangeDiscReq,
        9,
        PacketSource.Client
    );

    public static readonly PacketMeta UpdateUserAccountNickReq = new(
        PacketId.UpdateUserAccountNickReq,
        0x1C,
        PacketSource.Client
    );

    // Korean UpdateUserProfileReq is 10 wire bytes (China's was 18): byte@3, u16@4,
    // u32@6 — see the client sender sub_435990.
    public static readonly PacketMeta UpdateUserProfileReq = new(
        PacketId.UpdateUserProfileReq,
        10,
        PacketSource.Client
    );

    // DJMaxNet::OnRegister (sub_4300A0) registers both profile ACKs as
    // fixed 13-byte packets. Their handlers consume a 16-bit result at raw+3.
    // Both Korean profile acks are 5 wire bytes (result u16@3), not China's 13. The
    // client's handlers only clear their pending flag and forward to the active scene.
    public static readonly PacketMeta OnUpdateUserAccountNickAck = new(
        PacketId.OnUpdateUserAccountNickAck,
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserProfileAck = new(
        PacketId.OnUpdateUserProfileAck,
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta OnReserved34Inf = new(
        PacketId.OnReserved34Inf,
        4,
        PacketSource.Server
    );

    /// <summary>Room slot open/close broadcast; sub_434650 forwards it to the room scene.</summary>
    public static readonly PacketMeta OnSlotControlInf = new(
        PacketId.OnSlotControlInf,
        4,
        PacketSource.Server
    );

    /// <summary>Pre-match "good luck" ping; sub_436620 is an empty handler. Fixed 7.</summary>
    public static readonly PacketMeta OnGoodLuckInf = new(
        PacketId.OnGoodLuckInf,
        7,
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
        27,
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
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta OnContinueCourseAck = new(
        PacketId.OnContinueCourseAck,
        5,
        PacketSource.Server
    );

    // Named "Req" in the enum but the CLIENT receives it (registered at 11 bytes).
    public static readonly PacketMeta OnPostCourseItemReq = new(
        PacketId.OnPostCourseItemReq,
        11,
        PacketSource.Server
    );

    public static readonly PacketMeta OnAwardItemInf = new(
        PacketId.OnAwardItemInf,
        7,
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
        3,
        PacketSource.Client
    );

    // Korean ItemLevelUpReq is likewise 3 bytes with no body (sub_436400, id 183,
    // pending flag net+894524).
    public static readonly PacketMeta ItemLevelUpReq = new(
        PacketId.ItemLevelUpReq,
        3,
        PacketSource.Client
    );

    // Korean UseItemReq is 4 bytes: one byte at +3 (sub_436510 writes v4 = a2 there,
    // sends size 4, pending flag net+894528). Not China's {itemId,value} pair.
    public static readonly PacketMeta UseItemReq = new(
        PacketId.UseItemReq,
        4,
        PacketSource.Client
    );

    // Korean UseEffectorInf (client's own effector config, sent during load) is 27 wire
    // bytes: config[24]@3, no reserved tail. China's 35 over-read into the next packets.
    public static readonly PacketMeta UseEffectorInf = new(
        PacketId.UseEffectorInf,
        27,
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
        11,
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
        11,
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
        1874, // Korean lobby payload (client sub_4314F0); China was 47
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
    public static readonly PacketMeta OnRoomInfoInf = new(
        PacketId.OnRoomInfoInf,
        51,
        PacketSource.Server
    );

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
        4,
        PacketSource.Server
    );

    public static readonly PacketMeta OnTeamControlInf = new(
        PacketId.OnTeamControlInf,
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta OnWaiterInfoUpdateInf = new(
        PacketId.OnWaiterInfoUpdateInf,
        0x4C,
        PacketSource.Server
    );

    public static readonly PacketMeta OnRoomInfoUpdateInf = new(
        PacketId.OnRoomInfoUpdateInf,
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta OnInviteRejectAck = new(
        PacketId.OnInviteRejectAck,
        4,
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
        3, // Korean (client size table id 72 = 3); China guess was 0xB=11
        PacketSource.Server
    );


    public static readonly PacketMeta OnGameInfoInf = new(
        PacketId.OnGameInfoInf,
        PacketSource.Server
    );

    public static readonly PacketMeta OnJoinEventInf = new(
        PacketId.OnJoinEventInf,
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUseEffectorInf = new(
        PacketId.OnUseEffectorInf,
        28,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUseMountItemInf = new(
        PacketId.OnUseMountItemInf,
        70,
        PacketSource.Server
    );

    public static readonly PacketMeta OnStartParameterInf = new(
        PacketId.OnStartParameterInf,
        12,
        PacketSource.Server
    );

    public static readonly PacketMeta OnStartInf = new(
        PacketId.OnStartInf,
        6,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPlayStartInf = new(
        PacketId.OnPlayStartInf,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPlaySkipInf = new(
        PacketId.OnPlaySkipInf,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnLoadCompleteInf = new(
        PacketId.OnLoadCompleteInf,
        4,
        PacketSource.Server
    );

    public static readonly PacketMeta OnCheckDataReq = new(
        PacketId.OnCheckDataReq,
        7,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserInventoryDefaultItemInf = new(
        PacketId.OnUpdateUserInventoryDefaultItemInf,
        199,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserIconInf = new(
        PacketId.OnUpdateUserIconInf,
        17,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyInf = new(
        PacketId.OnUpdateUserPropertyInf,
        73,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyLevelInf = new(
        PacketId.OnUpdateUserPropertyLevelInf,
        17,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyMoneyInf = new(
        PacketId.OnUpdateUserPropertyMoneyInf,
        13,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserPropertyRecordInf = new(
        PacketId.OnUpdateUserPropertyRecordInf,
        21,
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
        4,
        PacketSource.Server
    );

    /// <summary>Chat flood control; state byte at raw+3. Client handler sub_4327E0.</summary>
    public static readonly PacketMeta OnChatControlInf = new(
        PacketId.OnChatControlInf,
        4,
        PacketSource.Server
    );

    /// <summary>Event/room indicator; command u16@3 and value u16@5. sub_435110.</summary>
    public static readonly PacketMeta OnEventInfoInf = new(
        PacketId.OnEventInfoInf,
        7,
        PacketSource.Server
    );

    public static readonly PacketMeta OnPlayStateInf = new(
        PacketId.OnPlayStateInf,
        15,
        PacketSource.Server
    );

    public static readonly PacketMeta OnReadyInf = new(
        PacketId.OnReadyInf,
        10,
        PacketSource.Server
    );

    public static readonly PacketMeta OnLeaveRoomAck = new(
        PacketId.OnLeaveRoomAck,
        4,
        PacketSource.Server
    );


    public static readonly PacketMeta OnLogOutAck = new(
        PacketId.OnLogOutAck,
        5,
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
        9,
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
        5,
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
        28,
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
        11,
        PacketSource.Client
    );

    public static readonly PacketMeta OnUseEffectorSetInf = new(
        PacketId.OnUseEffectorSetInf,
        12,
        PacketSource.Server
    );

    public static readonly PacketMeta OnAlertCreditInf = new(
        PacketId.OnAlertCreditInf,
        7,
        PacketSource.Server
    );

    public static readonly PacketMeta OnCrItemInf = new(
        PacketId.OnCrItemInf,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnMissionStandItemInf = new(
        PacketId.OnMissionStandItemInf,
        14,
        PacketSource.Server
    );

    public static readonly PacketMeta OnGetItemAck = new(
        PacketId.OnGetItemAck,
        7,
        PacketSource.Server
    );

    public static readonly PacketMeta OnItemLevelUpAck = new(
        PacketId.OnItemLevelUpAck,
        6,
        PacketSource.Server
    );

    public static readonly PacketMeta OnGetItemFail = new(
        PacketId.OnGetItemFail,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnItemLevelUpFail = new(
        PacketId.OnItemLevelUpFail,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUseItemFail = new(
        PacketId.OnUseItemFail,
        3,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUseItemAck = new(
        PacketId.OnUseItemAck,
        16,
        PacketSource.Server
    );

    public static readonly PacketMeta OnUpdateUserAccountClassInf = new(
        PacketId.OnUpdateUserAccountClassInf,
        11,
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

    // Korean 빠른초대 is a bare 3-byte signal (observed on the wire: A0 00 AD), not
    // China's 11. The extra 8 bytes were eaten out of whatever followed: a quick invite
    // immediately followed by a chat line framed as A0 00 AD | 37 00 7E 0D 00 | 00 00 and
    // resumed mid-text at 0xD895, desynchronising the whole stream.
    public static readonly PacketMeta QuickInviteReq = new(
        PacketId.QuickInviteReq,
        3,
        PacketSource.Client
    );

    public static readonly PacketMeta RoomChangeInfoReq = new(
        PacketId.RoomChangeInfoReq,
        0x32,
        PacketSource.Client
    );

    public static readonly PacketMeta OnJoinRoomAck = new(
        PacketId.OnJoinRoomAck,
        7,
        PacketSource.Server
    );

    // Korean JoinRoomReq is 17 wire bytes (roomIndex u16@3 + a 12-byte credential tail),
    // not China's 0x19=25. The oversized meta meant the request never framed, so a join
    // attempt got no reply at all and the client sat waiting.
    public static readonly PacketMeta JoinRoomReq = new(
        PacketId.JoinRoomReq,
        17,
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
        9,
        PacketSource.Server
    );

    public static readonly PacketMeta UserIdInfoReq = new(
        PacketId.UserIdInfoReq,
        PacketSource.Client
    );

    public static readonly PacketMeta OnChangeDiscInf = new(
        PacketId.OnChangeDiscInf,
        9,
        PacketSource.Server
    );

    // Korean CourseRankReq: courseId u16@3; the 0x84 ack clears its pending flag (client sender proves 5 wire bytes).
    public static readonly PacketMeta sub_434390 = new(
        PacketId.sub_434390,
        5,
        PacketSource.Client
    );

    // Korean ChangeCourseReq: courseId u16@3; the 0x86 ack clears its pending flag (client sender proves 5 wire bytes).
    public static readonly PacketMeta sub_434450 = new(
        PacketId.sub_434450,
        5,
        PacketSource.Client
    );

    // Korean ContinueCourseReq: value u16@3; the 0x88 ack clears its pending flag (client sender proves 5 wire bytes).
    public static readonly PacketMeta sub_434510 = new(
        PacketId.sub_434510,
        5,
        PacketSource.Client
    );

    // Korean PostCourseItemReq: bare signal, no body (client sender proves 3 wire bytes).
    public static readonly PacketMeta sub_434620 = new(
        PacketId.sub_434620,
        3,
        PacketSource.Client
    );

    public static readonly PacketMeta sub_434B40 = new(
        PacketId.sub_434B40,
        5, // Korean client sends id 0xFD as 5 bytes; China was 0xD=13
        PacketSource.Client
    );

    // Korean client sends id 0x53 as 4 wire bytes: control at raw+2, slot at raw+3
    // (captured `53 00 7E 02` = lock slot 2; kick sender sub_4342F0 also sends 4). The old
    // China size of 12 made the server over-read by 8 bytes and desync on every slot lock.
    public static readonly PacketMeta SlotControlReq = new(
        PacketId.SlotControlReq,
        4,
        PacketSource.Client
    );

    public static readonly PacketMeta OnSlotControlAck = new(
        PacketId.OnSlotControlAck,
        5,
        PacketSource.Server
    );

    public static readonly PacketMeta sub_436120 = new(
        PacketId.sub_436120,
        3,
        PacketSource.Client
    );

    public static readonly PacketMeta sub_4362D0 = new(
        PacketId.sub_4362D0,
        4,
        PacketSource.Client
    );

    public static readonly PacketMeta sub_437370 = new(
        PacketId.sub_437370,
        0x103,
        PacketSource.Client
    );

    // Korean update-user-icon (0x23): client sends 7 bytes (control + iconId u32@3),
    // NOT the China 15-byte form. Parsing 15 over-reads by 8 and desyncs the stream.
    public static readonly PacketMeta sub_437900 = new(
        PacketId.sub_437900,
        7,
        PacketSource.Client
    );

    public static readonly PacketMeta ProbeObfuscated = new(
        PacketId.ProbeObfuscated,
        0x90,
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
        { PacketId.OnRoomInfoInf, OnRoomInfoInf },
        { PacketId.OnBigNewsInf, OnBigNewsInf },
        { PacketId.OnCipherCommandInf, OnCipherCommandInf },
        { PacketId.MsgChatReq, MsgChatReq },
        { PacketId.OnMsgChatInf, OnMsgChatInf },
        { PacketId.OnWaiterInfoUpdateInf, OnWaiterInfoUpdateInf },
        { PacketId.OnRoomInfoUpdateInf, OnRoomInfoUpdateInf },
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
}
