namespace Arrowgene.DJMaxOnline.Server.Korea400;

public enum PacketId : ushort
{
    // OnInitialize = 0x00, - Not a Packet
    // OnRegister = 0x00, - Not a Packet
    // Resume = 0x00, - Not a Packet
    // OnConnect = 0x00, - Not a Packet
    // ClearChannelInfo = 0x00, not a packet
    // InitReq = 0x00, not a packet
    // Suspend = 0x00, not a packet
    // OnDestroy = 0x00, not a packet
    // OnDisconnect = 0x00, not a packet
    // ClearWaiterInfo = 0x00,  not a packet
    // ClearJoinerInfo = 0x00,not a packet
    // ClearCourseList = 0x00, not a packet
    // ClearUserIdInfo = 0x00,not a packet
    // ClearRoomInfo = 0x00, not a packet
    //EraseRoomInfo = 0x00, not a packet
    //EraseJoinerInfo = 0x00, not a packet
    //EraseWaiterInfo = 0x00, not a packet
    //UpdateWaiterInfo = 0x00, not a packet
    //AuthenticateInReq = 0x00,  // maybe only for korea and japanese client?
    //UpdateRoomInfo = 0x00, not a packet
    //Report = 0x00, // what is this? sub_438FD0

    Test00 = 0x6f, // client's end-of-song StageResultInf (sub_437460), FIXED 59
    ProbeObfuscated = 0xF0, // FIXED  144 
    sub_434390 = 0x83, //0xD 
    sub_434450 = 0x85, //0xD
    sub_434510 = 0x87, //0xD
    sub_434620 = 0x8A, //0xB
    sub_434B40 = 0xFD, //0xD
    SlotControlReq = 0x53, // fixed 12; host toggles an empty room slot
    OnSlotControlAck = 0x54, // fixed 13; slot index and enabled state
    sub_436120 = 0xA3, // Korean bare 3-byte lobby/invite transition request
    sub_4362D0 = 0x56, // Korean 4-byte peer-state request
    sub_437370 = 0x72, //0x103
    sub_437900 = 0x23, //0xF
    UbsAccountAuthenticationReq = 0x12C, // fixed 11; shop-entry credit authentication
    sub_4323D0 = 0x12F, //0x26
    OnUpdateJoinerInfoInf = 0x51, //FIXED 135
    OnSlotControlInf = 0x55, // sub_436310 forwards the packet unchanged
    ConnectReq = 0x0A, // JP client sends this FIXED 15 bytes (China was 23)
    NetmarbleAuthenticateReq = 0x0D, // dynamic launcher-ticket auth when ConnectFromNM=1
    JpConnectConfirmReq = 0x0F, // 60-byte launcher account/ticket + OnConnectAck seed
    OnConnectAck = 0x09, //0x2F
    AuthenticateInSndAccReq = 0x11, //FIXED 67
    AuthenticateInSndeKeyAck = 0x13, //NOT CAPTURED
    OnAuthenticateInAck = 0x10,  // FIXED 92
    OnAuthenticateInSndeKeyReq = 0x12, //NOT CAPTURED
    OnAuthenticateInSndrPwdReq = 0x14, //NOT CAPTURED
    AuthenticateInSndrPwdAck = 0x15,  // NOT CAPTURED
    OnUbsAccountAuthenResAck = 0x12D, // fixed 8; registered but ignored by this client
    OnUbsAwardInfoInf = 0x12E, // not registered or dispatched by this client
    OnUbsAwardAuthenAck = 0x130, // not registered or dispatched by this client
    KeepAuthenticateInReq = 0x17, // FIXED 15
    OnKeepAuthenticateInAck = 0x16, // FIXED 16
    LogInReq = 0x1B, //FIXED 53
    OnUserInfoInf = 0x43, // not captured
    OnInventoryInfoInf = 0x44, // not captured
    OnMessengerInfoInf = 0x45, // not captured
    OnLogInAck = 0x1A, // FIXED 100
    LogOutReq = 0x19, // fixed 5; return to server list
    OnLogOutAck = 0x18, // fixed 5; u16 status
    OnChannelInfoInf = 0x0B, // DYNAMIC
    OnPeerCountInf = 0x0C, //DYNAMIC 
    OnDisconnectPeerInf = 0x08, // not captured
    AliveAck = 0x06, // Fixed Size 0 
    OnAliveReq = 0x07,  // FIXED Size 0, has padding 00 00 00 
    OnPingTestInf = 0x03,  // FIXED Size 0, has padding 00 00 00
    PingTestInf = 0x04, /// FIXED, Size 0
    UserInfoReq = 0x1D, //FIXED 15
    OnUserInfoAck = 0x1F, //FIXED 892
    OnUserInfoResNotFound = 0x1E, // not captured
    OnBigNewsInf = 0x97, // server -> client, fixed 357-byte title/body announcement
    // Chat flood control, fixed 4: one state byte at raw+3 (sub_4327E0). 0 warns the user
    // they are spamming, 1 disables their chat box, 2 re-enables it.
    OnChatControlInf = 0x9A,
    WChatReq = 0x35, // client -> server whisper request
    OnWChatInf = 0x36, // server -> client whisper result/display
    // Korean chat ids are shifted from China. Observed: client sends chat on 0x37
    // (text at packet+7). The client displays chat/whisper via 0x38 (sub_4321D0,
    // type byte @+7: 0/1 normal, 6/7 whisper-with-sender). 0x39 is room-grid-add.
    ChatInf = 0x37, // client -> server chat send (text @packet+7)
    OnChatInf = 0x38, // server -> client chat echo (len@3, type@7, text@8); sub_4321D0
    OnRoomInfoInf = 0x39, // server -> client add room to lobby grid (48B); sub_433870
    OnCourseListInf = 0x82, // dynamic: sequence of ushort course IDs
    OnCourseRankAck = 0x84, //??
    OnChangeCourseAck = 0x86, //??
    OnContinueCourseAck = 0x88, //??
    OnPostCourseItemReq = 0x89, // not captured
    OnAwardItemInf = 0x8C, // not captured
    OnCipherCommandInf = 0x10E, // encrypted payload appended to dated cc-YYMMDD.csv
    // NOT a "server command" - sub_432830 word-filters the text and hands the packet to
    // the scene, whose dispatcher (e.g. sub_45EFCC case 251) routes it to sub_48BD17, the
    // DJ메신저 conversation-window append. Dynamic; size@3, user id@7, user id@11, text@15.
    OnMsgChatInf = 0xFB,
    OnEnvironmentInf = 0xFC, //FIXED VERY LARGE.
    OnSystemInfoAck = 0xFE, //FIXED 69
    OnWaiterInfoUpdateInf = 0x3C, // FIXED 76
    OnWaiterInfoEraseInf = 0x3D, // FIXED 17
    OnRoomInfoUpdateInf = 0x3A, // fixed 5; REMOVES a room from the grid (roomIndex@3)
    // NOTE: China's OnRoomInfoEraseInf (0x3B) is gone — 59 is absent from the Korean
    // receive table sub_42F440, so the client cannot receive it. Removing a room from
    // the grid is OnRoomInfoUpdateInf (0x3A).
    UserIdInfoReq = 0x21, //DYNAMIC
    OnUserIdInfoAck = 0x22, // DYNAMIC
    OnUserIdInfoInf = 0x20, // DYNAMIC
    OnJoinerListStart = 0x40, // not captured
    OnJoinerListEnt = 0x41, // not captured
    OnJoinerListEnd = 0x42, // not captured
    CreateRoomReq = 0x4C, // fixed 59
    OnCreateRoomAck = 0x4D, // fixed 52
    JoinRoomReq = 0x46, // fixed 25
    OnJoinRoomAck = 0x47, // fixed 15
    OnPostJoinRoomInf = 0x48,// not captured
    InviteRejectReq = 0xA6, // fixed 12
    OnInviteRejectAck = 0xA7, // fixed 12
    OnRoomDescInf = 0x50, // Fixed 48
    OnQuickInviteAck = 0xA1, // not captured
    OnInviteReq = 0xA2, // not captured
    QuickInviteReq = 0xA0, // not captured
    RoomChangeInfoReq = 0x9C, //FIXED 50
    OnRoomChangeInfoAck = 0x9D, //FIXED 51
    TeamControlReq = 0x58, // Korean fixed 4: control + team
    OnTeamControlInf = 0x59, // Korean fixed 5: control + slot + team
    OnGameTypeInf = 0x5B, // Korean fixed 4: control + game type
    ReadyReq = 0x5D,  // Korean fixed 3; bare ready toggle signal
    OnReadyInf = 0x5E, // FIXED 10
    StartReq = 0x5F, // not captured
    OnStartInf = 0x60, // Fixed 14
    OnJoinEventInf = 0x61, // GO BACK TO THIS.
    OnEventInfoInf = 0x63, // not captured
    PlayStartReq = 0x64, // Fixed 11
    OnPlayStartInf = 0x65, // Fixed 11
    PlaySkipReq = 0x67, // fixed 3; bare client signal
    OnPlaySkipInf = 0x68, // unregistered; client fallback framing is exactly 3 bytes
    PlayOverReq = 0x6A, // not captured
    OnPlayOverInf = 0x6B, // not captured
    PlayStateInf = 0x6C, // Korean: 22 bytes (8-byte wrapper + 11-byte state)
    OnPlayStateInf = 0x6D,// Korean: 15 bytes (slot + 11-byte state)
    OnCheckDataReq = 0x71, //FIXED 15
    OnLoadCompleteInf = 0x7C, //FIXED 12
    StageResultInf = 0x49, // not captured, Maybe wrong ID.
    OnStageResultExInf = 0x70, //FIXED 51
    LeaveRoomReq = 0x73, // fixed 3; bare client signal
    OnLeaveRoomAck = 0x74, // fixed 4; one-byte result
    ChangeDiscReq = 0x76, // FIXED 17
    OnChangeDiscInf = 0x77,//FIXED 17
    OnAwardInfoInf = 0x78, // not captured
    OnGameInfoInf = 0x7A, //Very Large Fixed.
    // Korean ids, one BELOW the China ones (reversed from the client's senders and their
    // pending-flag acks: sub_4358D0/sub_435940 nickname, sub_435990/sub_4359F0 profile).
    // 47 and 49 are absent from the client's receive table sub_42F440, which is what marks
    // them as request ids; 48 and 50 are the acks (5 bytes each).
    UpdateUserAccountNickReq = 0x2F, // fixed 28; raw+3 25-byte nickname
    OnUpdateUserAccountNickAck = 0x30, // fixed 5; raw+3 result
    UpdateUserProfileReq = 0x31, // fixed 10; byte@3, u16@4, u32@6 (sent +1)
    OnUpdateUserProfileAck = 0x32, // fixed 5; raw+3 result
    OnReserved34Inf = 0x34, // fixed 4; dispatched, but all discovered scenes ignore it
    OnUpdateUserIconInf = 0x24, // fixed 13; sub_437B20
    OnUpdateUserPropertyInf = 0x25, //Fixed 73
    OnUpdateUserPropertyRecordInf = 0x28, // fixed 21; sub_437D60
    OnUpdateUserPropertyMiscInf = 0x29, // fixed 49; sub_437E30
    OnUpdateUserPropertyLevelInf = 0x26, // fixed 17; sub_437EE0
    OnUpdateUserPropertyMoneyInf = 0x27, // fixed 13; sub_437FC0
    OnUpdateUserInventoryDefaultItemInf = 0x2A, // fixed 199; sub_438060
    OnUpdateUserInventoryEventItemInf = 0x2B, // fixed 135; sub_4380F0
    OnUpdateUserInventoryShopItemInf = 0x2C, // fixed 247; sub_438180
    OnUpdateUserInventoryMountItemInf = 0x2D, // fixed 71; sub_438210
    OnUpdateUserAccountClassInf = 0x2E, // fixed 11; userId@3, accountClass@7
    OnCrItemInf = 0xB3, // Korean fixed 3; bare room-wide create-item signal
    GetItemReq = 0xB4, // Korean fixed 3; bare pickup request
    OnGetItemFail = 0xB5, // Korean fixed 3
    OnGetItemAck = 0xB6, // Korean fixed 7; slot + itemId + level
    ItemLevelUpReq = 0xB7, // Korean fixed 3; bare level-up request
    OnItemLevelUpFail = 0xB8, // Korean fixed 3
    OnItemLevelUpAck = 0xB9, // Korean fixed 6; slot + queueIndex + level
    UseItemReq = 0xBA, // Korean fixed 4; target slot
    OnUseItemFail = 0xBB, // Korean fixed 3
    OnUseItemAck = 0xBC, // Korean fixed 16; source/target/effect parameters
    OnGoodLuckInf = 0xBD, // not captured
    OnGoodLuckListInf = 0xBE, // not captured
    OnMissionStandItemInf = 0xC0, //fixed 22
    UseEffectorInf = 0xC3,  //fixed 35
    UseEffectorSetInf = 0xC5, // Korean fixed 11; four signed 16-bit fields
    OnUseEffectorInf = 0xC4, //fixed 36
    OnUseEffectorSetInf = 0xC6, // Korean fixed 12; request fields + player slot
    UseMountItemInf = 0xC8, // not captured
    OnUseMountItemInf = 0xC9, // not captured
    OnStartParameterInf = 0xCA, // not captured
    OnBillingAuthInf = 0xD2, // not captured
    OnGameStartInf = 0xD3, // not captured
    OnUserAlertInf = 0xD4, // not captured
    MountItemReq = 0xD7, // not captured
    OnMountItemAck = 0xD8, // not captured
    GetPresentItemReq = 0xD9, // not captured
    OnGetPresentItemAck = 0xDA, // not captured
    DeleteItemReq = 0xDB, // not captured
    OnDeleteItemAck = 0xDC,// not captured
    OnExpiredMountItemInf = 0xE4, // not captured
    OnExpiredShopItemInf = 0xE5, // not captured
    OnAlertCreditInf = 0xE6, //DYNAMIC
    OnMsgNotifyInf = 0xF1, // fixed 9; userId@3, status@7
    MsgRegisterUserReq = 0xF2, // fixed 36; nickname/group/user/operation
    OnMsgRegisterUserAck = 0xF3, // fixed 5; result u16@3 (194 succeeds)
    OnMsgRegUserInf = 0xF4, // fixed 483; 60 eight-byte contacts
    OnMsgBlkUserInf = 0xF5, // fixed 243; 60 four-byte user ids
    OnMsgGroupInf = 0xF6, // fixed 233; 10 23-byte group names
    // Send-only: 250 is absent from the client's receive table sub_42F440, so the server
    // has to translate it into OnMsgChatInf (0xFB) for the recipient. Built by sub_431FC0
    // as size@3 = 15+strlen(text), senderId@7, targetId@11, text@15 (no terminator).
    MsgChatReq = 0xFA,
    PurchaseItemReq = 0xDD,  // not captured
    OnPurchaseItemAck = 0xDE, // not captured
    ResaleItemReq = 0xDF, // not captured
    OnResaleItemAck = 0xE0, // not captured.
    // NOTE: China's OnUpdateUserAccountClassInf (0x2F) is gone. 47 is not in the Korean
    // receive table sub_42F440 — the client cannot receive it — and 0x2F is the Korean
    // UpdateUserAccountNickReq. The account class reaches the client through the login
    // block (+129) and the waiter record (+67) instead.
    VerifyCodeInf = 0xE7, //fixed 35
}
