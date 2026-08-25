namespace Arrowgene.DJMaxOnline.Server.China260.Protocol;

/// <summary>
/// One semantic case in China 2.60's receive dispatcher, <c>sub_4307C0</c>.
/// Both the id and handler address are taken directly from the China 2.60 IDA database.
/// </summary>
public readonly record struct ReceivePacketDefinition(PacketId Id, uint HandlerAddress);

/// <summary>A wire-framing registration made by the China 2.60 client registration table.</summary>
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
/// Receive-side model for China 2.60. Registrations describe how the receive framer
/// invokes the dispatcher; dispatcher cases describe which framed IDs have semantic
/// handlers. The two sets intentionally differ. Dispatcher coverage is IDA-verified;
/// framing is capture-verified where a registration exists.
/// </summary>
public static class ReceivePacketCatalog
{
    public const int ExpectedCaseCount = 116;
    public const int ExpectedRegistrationCount = 123;

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
    [
        new(PacketId.OnPingTestInf, 0x433260),
        new(PacketId.OnAliveReq, 0x433200),
        new(PacketId.OnDisconnectPeerInf, 0x433100),
        new(PacketId.OnConnectAck, 0x4319D0),
        new(PacketId.OnChannelInfoInf, 0x432FA0),
        new(PacketId.OnPeerCountInf, 0x433050),
        new(PacketId.OnAuthenticateInAck, 0x431EB0),
        new(PacketId.OnAuthenticateInSndeKeyReq, 0x432080),
        new(PacketId.OnAuthenticateInSndrPwdReq, 0x432110),
        new(PacketId.OnKeepAuthenticateInAck, 0x432670),
        new(PacketId.OnLogOutAck, 0x432EF0),
        new(PacketId.OnLogInAck, 0x432CD0),
        new(PacketId.OnUserInfoResNotFound, 0x4333E0),
        new(PacketId.OnUserInfoAck, 0x433370),
        new(PacketId.OnUserIdInfoInf, 0x4355C0),
        new(PacketId.OnUserIdInfoAck, 0x4354F0),
        new(PacketId.OnUpdateUserIconInf, 0x437B20),
        new(PacketId.OnUpdateUserPropertyInf, 0x437BD0),
        new(PacketId.OnUpdateUserPropertyLevelInf, 0x437EE0),
        new(PacketId.OnUpdateUserPropertyMoneyInf, 0x437FC0),
        new(PacketId.OnUpdateUserPropertyRecordInf, 0x437D60),
        new(PacketId.OnUpdateUserPropertyMiscInf, 0x437E30),
        new(PacketId.OnUpdateUserInventoryDefaultItemInf, 0x438060),
        new(PacketId.OnUpdateUserInventoryEventItemInf, 0x4380F0),
        new(PacketId.OnUpdateUserInventoryShopItemInf, 0x438180),
        new(PacketId.OnUpdateUserInventoryMountItemInf, 0x438210),
        new(PacketId.OnUpdateUserInventoryPresentItemInf, 0x438290),
        new(PacketId.OnUpdateUserAccountClassInf, 0x43A150),
        new(PacketId.OnUpdateUserAccountNickAck, 0x4379E0),
        new(PacketId.OnUpdateUserProfileAck, 0x437AC0),
        new(PacketId.OnReserved34Inf, 0x434160),
        new(PacketId.OnWChatInf, 0x433E60),
        new(PacketId.OnChatInf, 0x433B20),
        new(PacketId.OnRoomInfoUpdateInf, 0x435370),
        new(PacketId.OnRoomInfoEraseInf, 0x4353D0),
        new(PacketId.OnWaiterInfoUpdateInf, 0x435090),
        new(PacketId.OnWaiterInfoEraseInf, 0x435100),
        new(PacketId.OnJoinerListStart, 0x435850),
        new(PacketId.OnJoinerListEnt, 0x4358B0),
        new(PacketId.OnJoinerListEnd, 0x435910),
        new(PacketId.OnUserInfoInf, 0x432900),
        new(PacketId.OnInventoryInfoInf, 0x432BE0),
        new(PacketId.OnMessengerInfoInf, 0x432C60),
        new(PacketId.OnJoinRoomAck, 0x435BE0),
        new(PacketId.OnPostJoinRoomInf, 0x435D00),
        new(PacketId.OnCreateRoomAck, 0x435A70),
        new(PacketId.OnRoomDescInf, 0x435E80),
        new(PacketId.OnUpdateJoinerInfoInf, 0x4365B0),
        new(PacketId.OnSlotControlAck, 0x435F90),
        new(PacketId.OnSlotControlInf, 0x436310),
        new(PacketId.OnTeamControlInf, 0x436AC0),
        new(PacketId.OnGameTypeInf, 0x436B70),
        new(PacketId.OnReadyInf, 0x436C40),
        new(PacketId.OnStartInf, 0x436DB0),
        new(PacketId.OnJoinEventInf, 0x436E90),
        new(PacketId.OnEventInfoInf, 0x436F00),
        new(PacketId.OnPlayStartInf, 0x436FB0),
        new(PacketId.OnPlaySkipInf, 0x437090),
        new(PacketId.OnPlayOverInf, 0x437170),
        new(PacketId.OnPlayStateInf, 0x437290),
        new(PacketId.OnStageResultExInf, 0x437500),
        new(PacketId.OnCheckDataReq, 0x437300),
        new(PacketId.OnLeaveRoomAck, 0x4375E0),
        new(PacketId.OnChangeDiscInf, 0x437700),
        new(PacketId.OnAwardInfoInf, 0x4377A0),
        new(PacketId.OnGameInfoInf, 0x437840),
        new(PacketId.OnLoadCompleteInf, 0x4373F0),
        new(PacketId.OnCourseListInf, 0x4342D0),
        new(PacketId.OnCourseRankAck, 0x434400),
        new(PacketId.OnChangeCourseAck, 0x4344C0),
        new(PacketId.OnContinueCourseAck, 0x434570),
        new(PacketId.OnPostCourseItemReq, 0x4345C0),
        new(PacketId.OnAwardItemInf, 0x4346E0),
        new(PacketId.OnBigNewsInf, 0x433960),
        new(PacketId.OnChatControlInf, 0x4341B0),
        new(PacketId.OnRoomChangeInfoAck, 0x436210),
        new(PacketId.OnQuickInviteAck, 0x436000),
        new(PacketId.OnInviteReq, 0x436060),
        new(PacketId.OnInviteRejectAck, 0x435E00),
        new(PacketId.OnCrItemInf, 0x438380),
        new(PacketId.OnGetItemFail, 0x4383E0),
        new(PacketId.OnGetItemAck, 0x438440),
        new(PacketId.OnItemLevelUpFail, 0x438510),
        new(PacketId.OnItemLevelUpAck, 0x438570),
        new(PacketId.OnUseItemFail, 0x438640),
        new(PacketId.OnUseItemAck, 0x4386A0),
        new(PacketId.OnGoodLuckInf, 0x438700),
        new(PacketId.OnGoodLuckListInf, 0x438720),
        new(PacketId.OnMissionStandItemInf, 0x438740),
        new(PacketId.OnUseEffectorInf, 0x438920),
        new(PacketId.OnUseEffectorSetInf, 0x438990),
        new(PacketId.OnUseMountItemInf, 0x438A50),
        new(PacketId.OnStartParameterInf, 0x438B00),
        new(PacketId.OnBillingAuthInf, 0x438BA0),
        new(PacketId.OnGameStartInf, 0x438C20),
        new(PacketId.OnUserAlertInf, 0x438CD0),
        new(PacketId.OnMountItemAck, 0x439640),
        new(PacketId.OnGetPresentItemAck, 0x439740),
        new(PacketId.OnDeleteItemAck, 0x439880),
        new(PacketId.OnPurchaseItemAck, 0x439F70),
        new(PacketId.OnResaleItemAck, 0x43A0A0),
        new(PacketId.OnExpiredMountItemInf, 0x439920),
        new(PacketId.OnExpiredShopItemInf, 0x4399F0),
        new(PacketId.OnAlertCreditInf, 0x439AC0),
        new(PacketId.OnMsgNotifyInf, 0x439C40),
        new(PacketId.OnMsgRegisterUserAck, 0x439D50),
        new(PacketId.OnMsgRegUserInf, 0x439DC0),
        new(PacketId.OnMsgBlkUserInf, 0x439E30),
        new(PacketId.OnMsgGroupInf, 0x439EA0),
        new(PacketId.OnMsgChatInf, 0x434200),
        new(PacketId.OnEnvironmentInf, 0x434AE0),
        new(PacketId.OnSystemInfoAck, 0x434BA0),
        new(PacketId.OnCipherCommandInf, 0x434740),
        new(PacketId.OnUbsAccountAuthenResAck, 0x432260),
        new(PacketId.OnUbsAwardInfoInf, 0x4322E0),
        new(PacketId.OnUbsAwardAuthenAck, 0x432440)
    ];

    public static IReadOnlyList<ReceivePacketRegistration> Registrations { get; } =
    [
        Fixed(0x09, 47), Fixed(0x08, 13), Fixed(0x07, 3), Fixed(0x03, 3),
        Fixed(0x10, 92), Fixed(0x12, 20), Fixed(0x14, 11), Dynamic(0x0B),
        Dynamic(0x0C), Fixed(0x16, 15), Fixed(0x43, 138), Fixed(0x44, 751),
        Fixed(0x45, 953), Fixed(0x1A, 47), Fixed(0x18, 13), Dynamic(0x22),
        Dynamic(0x20), Fixed(0x1F, 892), Fixed(0x1E, 11), Dynamic(0x37),
        Dynamic(0x39), Fixed(0x35, 12), Fixed(0x9A, 12), Fixed(0x3C, 76),
        Fixed(0x3D, 17), Fixed(0x3A, 51), Fixed(0x3B, 13), Fixed(0x50, 48),
        Fixed(0x3F, 11), Fixed(0x40, 11), Fixed(0x41, 135), Fixed(0x42, 11),
        Fixed(0x4D, 52), Fixed(0x55, 12), Fixed(0x47, 15), Fixed(0x48, 11),
        Fixed(0xA7, 12), Fixed(0x51, 135), Fixed(0x59, 13), Fixed(0x5B, 12),
        Fixed(0x5E, 18), Fixed(0x60, 14), Fixed(0x61, 13), Fixed(0x63, 15),
        Fixed(0x65, 11), Fixed(0x6B, 12), Fixed(0x68, 11), Fixed(0x77, 17),
        Fixed(0x78, 19), Dynamic(0x7A), Fixed(0x7C, 12), Fixed(0x6D, 23),
        Fixed(0x71, 15), Fixed(0x70, 51), Fixed(0x54, 13), Fixed(0xA1, 12),
        Fixed(0xA2, 24), Fixed(0x9D, 51), Fixed(0x74, 12), Fixed(0x97, 357),
        Dynamic(0x01), Fixed(0xAD, 8), Fixed(0xAE, 4), Fixed(0xAF, 2),
        Fixed(0xB0, 9), Fixed(0xB1, 2), Fixed(0x24, 25), Fixed(0x31, 13),
        Fixed(0x2F, 19), Fixed(0x33, 13), Fixed(0x25, 73), Fixed(0x26, 25),
        Fixed(0x27, 21), Fixed(0x28, 29), Fixed(0x29, 49), Fixed(0x2A, 199),
        Fixed(0x2B, 135), Fixed(0x2C, 247), Fixed(0x2D, 71), Fixed(0x2E, 127),
        Fixed(0xB3, 11), Fixed(0xB5, 11), Fixed(0xB6, 15), Fixed(0xB8, 11),
        Fixed(0xB9, 14), Fixed(0xBB, 3), Fixed(0xBC, 24), Fixed(0xBD, 15),
        Fixed(0xBE, 103), Fixed(0xC0, 22), Fixed(0xC4, 36), Fixed(0xC6, 20),
        Fixed(0xC9, 70), Fixed(0xCA, 20), Fixed(0xDE, 253), Fixed(0xE0, 249),
        Fixed(0x12D, 16), Dynamic(0x12E), Fixed(0x130, 5), Fixed(0xD2, 35),
        Fixed(0xD3, 33), Fixed(0xD4, 36), Fixed(0xD8, 69), Fixed(0xDA, 365),
        Fixed(0xDC, 245), Fixed(0xE4, 131), Fixed(0xE5, 483), Fixed(0xE6, 15),
        Fixed(0xF1, 17), Fixed(0xF3, 13), Fixed(0xF4, 483), Fixed(0xF5, 243),
        Fixed(0xF6, 233), Dynamic(0xFB), Dynamic(0x10E), Dynamic(0x82),
        Fixed(0x84, 2155), Fixed(0x86, 13), Fixed(0x88, 13), Fixed(0x89, 19),
        Fixed(0x8C, 15), Fixed(0xFC, 319), Fixed(0xFE, 69)
    ];

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
        if (RegisteredButIgnored.Count != 7)
        {
            throw new InvalidOperationException(
                $"Expected 7 registered-but-ignored IDs, found {RegisteredButIgnored.Count}.");
        }
        if (DispatchedButUnregistered.Count != 0)
        {
            throw new InvalidOperationException(
                "Every dispatcher case must have a matching China 2.60 registration.");
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
            throw new KeyNotFoundException($"{id} is not dispatched by China 2.60 sub_4307C0.");
        }
        if (CaptureVerifiedSet.Contains(id))
        {
            return ReceiveFramingEvidence.CaptureVerified;
        }
        return RegistrationById.ContainsKey((ushort)id)
            ? ReceiveFramingEvidence.DeclaredButUncaptured
            : ReceiveFramingEvidence.HandlerOnly;
    }

    /// <summary>
    /// Ensures every server packet currently modelled by this project uses the exact
    /// size mode registered by China 2.60. This is deliberately limited to known server
    /// metas: registration-only IDs are not safe to synthesize just because the client
    /// can frame them.
    /// </summary>
    public static void ValidateServerPacketMetadata()
    {
        foreach (ReceivePacketRegistration registration in Registrations)
        {
            if (!PacketMeta.TryGet((PacketId)registration.Id, out PacketMeta meta) ||
                meta.Source != PacketSource.Server)
            {
                continue;
            }

            if (meta.IsDynamicSize != registration.IsDynamic ||
                (!meta.IsDynamicSize && meta.Size != registration.WireSize))
            {
                string registeredSize = registration.IsDynamic
                    ? "dynamic"
                    : registration.WireSize.ToString();
                string metaSize = meta.IsDynamicSize ? "dynamic" : meta.Size.ToString();
                throw new InvalidOperationException(
                    $"China 2.60 receive registration for {meta.Id} is {registeredSize} bytes, " +
                    $"but PacketMeta declares {metaSize} bytes.");
            }
        }
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
                $"China 2.60 sub_4307C0 has {ExpectedCaseCount} cases, catalog has {All.Count}.");
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
                $"The China 2.60 registration table has {ExpectedRegistrationCount} registrations, " +
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
