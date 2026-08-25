namespace Arrowgene.DJMaxOnline.Server.China260.Protocol;

/// <summary>One observed China 2.60 client packet emission.</summary>
public readonly record struct ClientSendPacketDefinition(
    PacketId Id,
    uint SenderAddress,
    int WireSize)
{
    public const int DynamicWireSize = -1;

    public bool IsDynamic => WireSize == DynamicWireSize;
}

/// <summary>
/// Client-to-server framing evidence for China 2.60. Keeping this separate from the
/// receive catalogue prevents a Korean or Japan short frame from returning unnoticed.
/// </summary>
public static class ClientSendPacketCatalog
{
    public static IReadOnlyList<ClientSendPacketDefinition> All { get; } =
    [
        new(PacketId.ConnectReq, 0x4311A0, 23),
        new(PacketId.CnConnectConfirmReq, 0x431760, 73),
        new(PacketId.KeepAuthenticateInReq, 0x431950, 15),
        new(PacketId.LogInReq, 0x431B20, 53),
        new(PacketId.LogOutReq, 0x432080, 13),
        new(PacketId.AliveAck, 0x4323B0, 3),
        new(PacketId.PingTestInf, 0x4324B0, 3),
        new(PacketId.ChatInf, 0x432730, ClientSendPacketDefinition.DynamicWireSize),
        new(PacketId.WChatReq, 0x432810, ClientSendPacketDefinition.DynamicWireSize),
        new(PacketId.MsgChatReq, 0x432AD0, ClientSendPacketDefinition.DynamicWireSize),
        new(PacketId.sub_434390, 0x433590, 13),
        new(PacketId.sub_434450, 0x433650, 13),
        new(PacketId.sub_434510, 0x433710, 13),
        new(PacketId.sub_434620, 0x433820, 11),
        new(PacketId.sub_434B40, 0x433D40, 13),
        new(PacketId.UserIdInfoReq, 0x434630, ClientSendPacketDefinition.DynamicWireSize),
        new(PacketId.CreateRoomReq, 0x434B70, 59),
        new(PacketId.JoinRoomReq, 0x434D40, 25),
        new(PacketId.InviteRejectReq, 0x434F60, 12),
        new(PacketId.SlotControlReq, 0x435120, 12),
        new(PacketId.QuickInviteReq, 0x4352C0, 11),
        new(PacketId.sub_436120, 0x435320, 11),
        new(PacketId.RoomChangeInfoReq, 0x435360, 50),
        new(PacketId.sub_4362D0, 0x4354D0, 12),
        new(PacketId.TeamControlReq, 0x435C50, 12),
        new(PacketId.ReadyReq, 0x435DD0, 11),
        new(PacketId.StartReq, 0x435F20, 13),
        new(PacketId.PlayStartReq, 0x436140, 11),
        new(PacketId.PlaySkipReq, 0x436210, 11),
        new(PacketId.PlayOverReq, 0x4362F0, 11),
        new(PacketId.PlayStateInf, 0x4363D0, 30),
        new(PacketId.sub_437370, 0x436570, 259),
        new(PacketId.Test00, 0x436660, 59),
        new(PacketId.LeaveRoomReq, 0x436770, 11),
        new(PacketId.ChangeDiscReq, 0x4368A0, 17),
        new(PacketId.sub_437900, 0x436A60, 15),
        new(PacketId.UpdateUserAccountNickReq, 0x436AB0, 28),
        new(PacketId.UpdateUserProfileReq, 0x436BA0, 18),
        new(PacketId.GetItemReq, 0x4375E0, 11),
        new(PacketId.ItemLevelUpReq, 0x437770, 11),
        new(PacketId.UseItemReq, 0x4378A0, 12),
        new(PacketId.UseEffectorInf, 0x437A70, 35),
        new(PacketId.UseEffectorSetInf, 0x437B50, 19),
        new(PacketId.UseMountItemInf, 0x437CD0, 67),
        new(PacketId.MountItemReq, 0x438890, 67),
        new(PacketId.GetPresentItemReq, 0x438990, 15),
        new(PacketId.DeleteItemReq, 0x438AA0, 19),
        new(PacketId.ProbeObfuscated, 0x438D90, 144),
        new(PacketId.MsgRegisterUserReq, 0x438F20, 36),
        new(PacketId.PurchaseItemReq, 0x439170, 35),
        new(PacketId.ResaleItemReq, 0x439270, 19),
        new(PacketId.VerifyCodeInf, 0x439610, 35)
    ];

    /// <summary>Fails fast if a server receive meta drifts from direct client evidence.</summary>
    public static void ValidatePacketMetadata()
    {
        foreach (ClientSendPacketDefinition definition in All)
        {
            if (!PacketMeta.TryGet(definition.Id, out PacketMeta meta))
            {
                throw new InvalidOperationException(
                    $"No PacketMeta is registered for client packet {definition.Id} " +
                    $"sent at 0x{definition.SenderAddress:X}.");
            }

            if (meta.Source != PacketSource.Client ||
                meta.IsDynamicSize != definition.IsDynamic ||
                (!definition.IsDynamic && meta.Size != definition.WireSize))
            {
                string expected = definition.IsDynamic ? "dynamic" : definition.WireSize.ToString();
                string actual = meta.IsDynamicSize ? "dynamic" : meta.Size.ToString();
                throw new InvalidOperationException(
                    $"PacketMeta mismatch for {definition.Id} (sender 0x{definition.SenderAddress:X}): " +
                    $"expected client/{expected}, found {meta.Source}/{actual}.");
            }
        }
    }
}
