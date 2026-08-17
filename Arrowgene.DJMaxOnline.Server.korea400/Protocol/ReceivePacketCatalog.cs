namespace Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

/// <summary>An explicit semantic case in the client dispatcher sub_42FB20.</summary>
public readonly record struct ReceivePacketDefinition(PacketId Id, uint HandlerAddress);

/// <summary>A wire-framing registration made by the client routine sub_42F440.</summary>
public readonly record struct ReceivePacketRegistration(ushort Id, int WireSize)
{
    public const int DynamicWireSize = -1;

    public bool IsDynamic => WireSize == DynamicWireSize;
}

public enum ReceiveFramingEvidence
{
    CaptureVerified,
    DeclaredButUncaptured,
    HandlerOnly
}

/// <summary>
/// Authoritative receive-side model for this DJMax.exe. Registrations describe how
/// sub_496FE0 frames bytes; dispatcher cases describe which framed IDs have semantic
/// handlers. The two sets intentionally differ.
/// </summary>
public static class ReceivePacketCatalog
{
    public const int ExpectedCaseCount = 106;
    public const int ExpectedRegistrationCount = 121;

    private static readonly HashSet<PacketId> CaptureVerifiedSet = new()
    {
        PacketId.OnPingTestInf,
        PacketId.OnAliveReq,
        PacketId.OnConnectAck,
        PacketId.OnChannelInfoInf,
        PacketId.OnAuthenticateInAck,
        PacketId.OnKeepAuthenticateInAck,
        PacketId.OnLogOutAck,
        PacketId.OnLogInAck,
        PacketId.OnUserInfoAck,
        PacketId.OnUserIdInfoInf,
        PacketId.OnUserIdInfoAck,
        PacketId.OnUpdateUserPropertyInf,
        PacketId.OnUpdateUserPropertyMiscInf,
        PacketId.OnUpdateUserInventoryDefaultItemInf,
        PacketId.OnChatInf,
        PacketId.OnRoomInfoUpdateInf,
        PacketId.OnWaiterInfoUpdateInf,
        PacketId.OnWaiterInfoEraseInf,
        PacketId.OnJoinRoomAck,
        PacketId.OnPostJoinRoomInf,
        PacketId.OnCreateRoomAck,
        PacketId.OnRoomDescInf,
        PacketId.OnUpdateJoinerInfoInf,
        PacketId.OnSlotControlAck,
        PacketId.OnTeamControlInf,
        PacketId.OnGameTypeInf,
        PacketId.OnReadyInf,
        PacketId.OnStartInf,
        PacketId.OnJoinEventInf,
        PacketId.OnPlayStartInf,
        PacketId.OnPlayOverInf,
        PacketId.OnPlayStateInf,
        PacketId.OnStageResultExInf,
        PacketId.OnCheckDataReq,
        PacketId.OnLeaveRoomAck,
        PacketId.OnChangeDiscInf,
        PacketId.OnGameInfoInf,
        PacketId.OnLoadCompleteInf,
        PacketId.OnRoomChangeInfoAck,
        PacketId.OnInviteRejectAck,
        PacketId.OnCrItemInf,
        PacketId.OnGetItemAck,
        PacketId.OnItemLevelUpAck,
        PacketId.OnMissionStandItemInf,
        PacketId.OnUseEffectorInf,
        PacketId.OnUseMountItemInf,
        PacketId.OnStartParameterInf,
        PacketId.OnGameStartInf,
        PacketId.OnAlertCreditInf,
        PacketId.OnMsgNotifyInf,
        PacketId.OnEnvironmentInf,
        PacketId.OnSystemInfoAck
    };

    public static IReadOnlySet<PacketId> CaptureVerifiedIds => CaptureVerifiedSet;

    public static IReadOnlyList<ReceivePacketDefinition> All { get; } =
        new ReceivePacketDefinition[]
        {
            new(PacketId.OnPingTestInf, 0x4319E0),
            new(PacketId.OnAliveReq, 0x4319B0),
            new(PacketId.OnDisconnectPeerInf, 0x4318E0),
            new(PacketId.OnConnectAck, 0x430AB0),
            new(PacketId.OnChannelInfoInf, 0x4317C0),
            new(PacketId.OnPeerCountInf, 0x431850),
            new(PacketId.OnAuthenticateInAck, 0x431020),
            new(PacketId.OnKeepAuthenticateInAck, 0x431280),
            new(PacketId.OnLogOutAck, 0x431720),
            new(PacketId.OnLogInAck, 0x4314F0),
            new(PacketId.OnUserInfoResNotFound, 0x431AE0),
            new(PacketId.OnUserInfoAck, 0x431A80),
            new(PacketId.OnUserIdInfoInf, 0x433AC0),
            new(PacketId.OnUserIdInfoAck, 0x4339F0),
            new(PacketId.OnUpdateUserIconInf, 0x435A40),
            new(PacketId.OnUpdateUserPropertyInf, 0x435AE0),
            new(PacketId.OnUpdateUserPropertyLevelInf, 0x435DC0),
            new(PacketId.OnUpdateUserPropertyMoneyInf, 0x435EA0),
            new(PacketId.OnUpdateUserPropertyRecordInf, 0x435C50),
            new(PacketId.OnUpdateUserPropertyMiscInf, 0x435D10),
            new(PacketId.OnUpdateUserInventoryDefaultItemInf, 0x435F30),
            new(PacketId.OnUpdateUserInventoryEventItemInf, 0x435F90),
            new(PacketId.OnUpdateUserInventoryShopItemInf, 0x4361E0),
            new(PacketId.OnUpdateUserInventoryMountItemInf, 0x436240),
            new(PacketId.OnUpdateUserAccountClassInf, 0x437D90),
            new(PacketId.OnUpdateUserAccountNickAck, 0x435940),
            new(PacketId.OnUpdateUserProfileAck, 0x4359F0),
            new(PacketId.OnReserved34Inf, 0x432790),
            new(PacketId.OnWChatInf, 0x4324D0),
            new(PacketId.OnChatInf, 0x4321D0),
            new(PacketId.OnRoomInfoInf, 0x433870),
            new(PacketId.OnRoomInfoUpdateInf, 0x4338D0),
            new(PacketId.OnWaiterInfoUpdateInf, 0x433600),
            new(PacketId.OnWaiterInfoEraseInf, 0x433660),
            new(PacketId.OnJoinerListStart, 0x433D10),
            new(PacketId.OnJoinerListEnt, 0x433D60),
            new(PacketId.OnJoinerListEnd, 0x433DC0),
            new(PacketId.OnJoinRoomAck, 0x434030),
            new(PacketId.OnPostJoinRoomInf, 0x434120),
            new(PacketId.OnCreateRoomAck, 0x433F00),
            new(PacketId.OnRoomDescInf, 0x434260),
            new(PacketId.OnUpdateJoinerInfoInf, 0x4348B0),
            new(PacketId.OnSlotControlAck, 0x434350),
            new(PacketId.OnSlotControlInf, 0x434650),
            new(PacketId.OnTeamControlInf, 0x434DA0),
            new(PacketId.OnGameTypeInf, 0x434E40),
            new(PacketId.OnReadyInf, 0x434EF0),
            new(PacketId.OnStartInf, 0x435000),
            new(PacketId.OnJoinEventInf, 0x4350C0),
            new(PacketId.OnEventInfoInf, 0x435110),
            new(PacketId.OnPlayStartInf, 0x435190),
            new(PacketId.OnPlaySkipInf, 0x435250),
            new(PacketId.OnPlayOverInf, 0x435300),
            new(PacketId.OnPlayStateInf, 0x4353F0),
            new(PacketId.OnStageResultExInf, 0x435600),
            new(PacketId.OnCheckDataReq, 0x435450),
            new(PacketId.OnLeaveRoomAck, 0x4356B0),
            new(PacketId.OnChangeDiscInf, 0x435790),
            new(PacketId.OnGameInfoInf, 0x4357F0),
            new(PacketId.OnLoadCompleteInf, 0x435520),
            new(PacketId.OnCourseListInf, 0x4328E0),
            new(PacketId.OnCourseRankAck, 0x4329F0),
            new(PacketId.OnChangeCourseAck, 0x432AA0),
            new(PacketId.OnContinueCourseAck, 0x432B40),
            new(PacketId.OnPostCourseItemReq, 0x432B90),
            new(PacketId.OnAwardItemInf, 0x432C90),
            new(PacketId.OnBigNewsInf, 0x432030),
            new(PacketId.OnChatControlInf, 0x4327E0),
            new(PacketId.OnRoomChangeInfoAck, 0x434570),
            new(PacketId.OnQuickInviteAck, 0x4343C0),
            new(PacketId.OnInviteReq, 0x434420),
            new(PacketId.OnInviteRejectAck, 0x4341F0),
            new(PacketId.OnCrItemInf, 0x4362F0),
            new(PacketId.OnGetItemFail, 0x436340),
            new(PacketId.OnGetItemAck, 0x4363A0),
            new(PacketId.OnItemLevelUpFail, 0x436450),
            new(PacketId.OnItemLevelUpAck, 0x4364B0),
            new(PacketId.OnUseItemFail, 0x436560),
            new(PacketId.OnUseItemAck, 0x4365C0),
            new(PacketId.OnGoodLuckInf, 0x436620),
            new(PacketId.OnGoodLuckListInf, 0x436630),
            new(PacketId.OnMissionStandItemInf, 0x436640),
            new(PacketId.OnUseEffectorInf, 0x4367D0),
            new(PacketId.OnUseEffectorSetInf, 0x436830),
            new(PacketId.OnUseMountItemInf, 0x4368E0),
            new(PacketId.OnStartParameterInf, 0x436960),
            new(PacketId.OnBillingAuthInf, 0x4369D0),
            new(PacketId.OnGameStartInf, 0x436A50),
            new(PacketId.OnUserAlertInf, 0x436B00),
            new(PacketId.OnMountItemAck, 0x437430),
            new(PacketId.OnGetPresentItemAck, 0x437510),
            new(PacketId.OnDeleteItemAck, 0x437600),
            new(PacketId.OnPurchaseItemAck, 0x437C20),
            new(PacketId.OnResaleItemAck, 0x437D00),
            new(PacketId.OnExpiredMountItemInf, 0x437680),
            new(PacketId.OnExpiredShopItemInf, 0x437730),
            new(PacketId.OnAlertCreditInf, 0x4377E0),
            new(PacketId.OnMsgNotifyInf, 0x437950),
            new(PacketId.OnMsgRegisterUserAck, 0x437A40),
            new(PacketId.OnMsgRegUserInf, 0x437AA0),
            new(PacketId.OnMsgBlkUserInf, 0x437B00),
            new(PacketId.OnMsgGroupInf, 0x437B60),
            new(PacketId.OnMsgChatInf, 0x432830),
            new(PacketId.OnEnvironmentInf, 0x433080),
            new(PacketId.OnSystemInfoAck, 0x433120),
            new(PacketId.OnCipherCommandInf, 0x432CE0)
        };

    public static IReadOnlyList<ReceivePacketRegistration> Registrations { get; } =
        new ReceivePacketRegistration[]
        {
            Dynamic(0x01),
            Fixed(0x03, 3), Fixed(0x07, 3), Fixed(0x08, 5), Fixed(0x09, 37),
            Dynamic(0x0B), Dynamic(0x0C), Fixed(0x10, 67), Fixed(0x12, 12),
            Fixed(0x14, 3), Fixed(0x16, 7), Fixed(0x18, 5), Fixed(0x1A, 1874),
            Fixed(0x1E, 3), Fixed(0x1F, 892), Dynamic(0x20), Dynamic(0x22),
            Fixed(0x24, 17), Fixed(0x25, 73), Fixed(0x26, 17), Fixed(0x27, 13),
            Fixed(0x28, 21), Fixed(0x29, 49), Fixed(0x2A, 199), Fixed(0x2B, 135),
            Fixed(0x2C, 247), Fixed(0x2D, 71), Fixed(0x2E, 11), Fixed(0x30, 5),
            Fixed(0x32, 5), Fixed(0x34, 4), Dynamic(0x36), Dynamic(0x38),
            Fixed(0x39, 51), Fixed(0x3A, 5), Fixed(0x3C, 76), Fixed(0x3D, 9),
            Fixed(0x3F, 3), Fixed(0x40, 3), Fixed(0x41, 135), Fixed(0x42, 3),
            Fixed(0x47, 7), Fixed(0x48, 3), Fixed(0x4A, 5), Fixed(0x4D, 52),
            Fixed(0x50, 48), Fixed(0x51, 135), Fixed(0x54, 5), Fixed(0x55, 4),
            Fixed(0x57, 4), Fixed(0x59, 5), Fixed(0x5B, 4), Fixed(0x5E, 10),
            Fixed(0x60, 6), Fixed(0x61, 5), Fixed(0x63, 7), Fixed(0x65, 3),
            Fixed(0x6B, 4), Fixed(0x6D, 15), Fixed(0x70, 51), Fixed(0x71, 7),
            Fixed(0x74, 4), Fixed(0x77, 9), Dynamic(0x7A), Fixed(0x7C, 4),
            Dynamic(0x82), Fixed(0x84, 2155), Fixed(0x86, 5), Fixed(0x88, 5),
            Fixed(0x89, 11), Fixed(0x8C, 7), Fixed(0x96, 276), Fixed(0x97, 357),
            Fixed(0x98, 100), Fixed(0x99, 276), Fixed(0x9A, 4), Fixed(0x9D, 51),
            Fixed(0xA1, 4), Fixed(0xA2, 16), Fixed(0xA7, 4), Fixed(0xAD, 8),
            Fixed(0xAE, 4), Fixed(0xAF, 2), Fixed(0xB0, 9), Fixed(0xB1, 2),
            Fixed(0xB3, 3), Fixed(0xB5, 3), Fixed(0xB6, 7), Fixed(0xB8, 3),
            Fixed(0xB9, 6), Fixed(0xBB, 3), Fixed(0xBC, 16), Fixed(0xBD, 7),
            Fixed(0xBE, 103), Fixed(0xC0, 14), Fixed(0xC4, 28), Fixed(0xC6, 12),
            Fixed(0xC9, 70), Fixed(0xCA, 12), Fixed(0xD2, 27), Fixed(0xD3, 25),
            Fixed(0xD4, 28), Fixed(0xD8, 69), Fixed(0xDA, 365), Fixed(0xDC, 245),
            Fixed(0xDE, 249), Fixed(0xE0, 249), Fixed(0xE4, 131), Fixed(0xE5, 483),
            Fixed(0xE6, 7), Fixed(0xE7, 35), Fixed(0xF1, 9), Fixed(0xF3, 5),
            Fixed(0xF4, 483), Fixed(0xF5, 243), Fixed(0xF6, 233), Dynamic(0xFB),
            Fixed(0xFC, 319), Fixed(0xFE, 69), Dynamic(0x10E), Fixed(0x12D, 8)
        };

    private static readonly IReadOnlyDictionary<PacketId, ReceivePacketDefinition> ById =
        CreateDispatchLookup();
    private static readonly IReadOnlyDictionary<ushort, ReceivePacketRegistration>
        RegistrationById = CreateRegistrationLookup();

    public static IReadOnlyList<ReceivePacketRegistration> RegisteredButIgnored { get; } =
        Registrations.Where(value => !ById.ContainsKey((PacketId)value.Id)).ToArray();

    public static IReadOnlyList<ReceivePacketDefinition> DispatchedButUnregistered { get; } =
        All.Where(value => !RegistrationById.ContainsKey((ushort)value.Id)).ToArray();

    static ReceivePacketCatalog()
    {
        if (RegisteredButIgnored.Count != 16)
        {
            throw new InvalidOperationException(
                $"Expected 16 registered-but-ignored IDs, found {RegisteredButIgnored.Count}.");
        }
        if (DispatchedButUnregistered.Count != 1 ||
            DispatchedButUnregistered[0].Id != PacketId.OnPlaySkipInf)
        {
            throw new InvalidOperationException(
                "0x68 must be the sole dispatched-but-unregistered receive ID.");
        }

        foreach (PacketId id in CaptureVerifiedSet)
        {
            if (!ById.ContainsKey(id) || !RegistrationById.ContainsKey((ushort)id))
            {
                throw new InvalidOperationException(
                    $"Capture-verified packet {id} is not both registered and dispatched.");
            }
        }
    }

    public static ReceivePacketDefinition Get(PacketId id) => ById[id];

    public static bool TryGet(PacketId id, out ReceivePacketDefinition definition) =>
        ById.TryGetValue(id, out definition);

    public static ReceivePacketRegistration GetRegistration(PacketId id) =>
        RegistrationById[(ushort)id];

    public static bool TryGetRegistration(
        PacketId id,
        out ReceivePacketRegistration registration) =>
        RegistrationById.TryGetValue((ushort)id, out registration);

    public static bool TryGetRegistration(
        ushort id,
        out ReceivePacketRegistration registration) =>
        RegistrationById.TryGetValue(id, out registration);

    public static ReceiveFramingEvidence GetFramingEvidence(PacketId id)
    {
        if (!ById.ContainsKey(id))
        {
            throw new KeyNotFoundException($"{id} is not dispatched by sub_42FB20.");
        }
        if (CaptureVerifiedSet.Contains(id))
        {
            return ReceiveFramingEvidence.CaptureVerified;
        }
        return RegistrationById.ContainsKey((ushort)id)
            ? ReceiveFramingEvidence.DeclaredButUncaptured
            : ReceiveFramingEvidence.HandlerOnly;
    }

    private static ReceivePacketRegistration Fixed(ushort id, int wireSize) =>
        new(id, wireSize);

    private static ReceivePacketRegistration Dynamic(ushort id) =>
        new(id, ReceivePacketRegistration.DynamicWireSize);

    private static IReadOnlyDictionary<PacketId, ReceivePacketDefinition>
        CreateDispatchLookup()
    {
        if (All.Count != ExpectedCaseCount)
        {
            throw new InvalidOperationException(
                $"sub_42FB20 has {ExpectedCaseCount} cases, catalog has {All.Count}.");
        }

        Dictionary<PacketId, ReceivePacketDefinition> result = new(All.Count);
        foreach (ReceivePacketDefinition definition in All)
        {
            if (!result.TryAdd(definition.Id, definition))
            {
                throw new InvalidOperationException(
                    $"Duplicate dispatch ID 0x{(ushort)definition.Id:X}.");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<ushort, ReceivePacketRegistration>
        CreateRegistrationLookup()
    {
        if (Registrations.Count != ExpectedRegistrationCount)
        {
            throw new InvalidOperationException(
                $"sub_42F440 has {ExpectedRegistrationCount} registrations, " +
                $"catalog has {Registrations.Count}.");
        }

        Dictionary<ushort, ReceivePacketRegistration> result =
            new(Registrations.Count);
        foreach (ReceivePacketRegistration registration in Registrations)
        {
            if (registration.WireSize == 0 || registration.WireSize < -1)
            {
                throw new InvalidOperationException(
                    $"Invalid receive size {registration.WireSize} for 0x{registration.Id:X}.");
            }
            if (!result.TryAdd(registration.Id, registration))
            {
                throw new InvalidOperationException(
                    $"Duplicate registration ID 0x{registration.Id:X}.");
            }
        }
        return result;
    }
}
