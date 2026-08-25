using Arrowgene.DJMaxOnline.Server.China260.Packets;

namespace Arrowgene.DJMaxOnline.Server.China260.Handler;

public sealed class LogOutReqHandler : IPacketHandler
{
    private readonly Func<IReadOnlyList<ChannelInfo>> _channelSnapshot;
    private readonly LocalLobby _lobby;

    public LogOutReqHandler(
        Func<IReadOnlyList<ChannelInfo>> channelSnapshot,
        LocalLobby lobby)
    {
        _channelSnapshot = channelSnapshot ??
            throw new ArgumentNullException(nameof(channelSnapshot));
        _lobby = lobby ?? throw new ArgumentNullException(nameof(lobby));
    }

    public PacketId Id => PacketId.LogOutReq;

    public void Handle(Client client, Packet packet)
    {
        // Captured retail order is 0x18 followed immediately by a fresh 0x0B.
        // Remove the user from the channel lobby while retaining the authenticated
        // socket so the client can choose either local key-mode endpoint again.
        _lobby.Leave(client);
        client.UserId = null;
        client.Send(OnLogOutAckPacket.Build());
        client.Send(OnChannelInfoInfPacket.Build(_channelSnapshot()));
    }
}

public sealed class UserInfoReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.UserInfoReq;

    public void Handle(Client client, Packet packet)
    {
        uint requestedUserId = UserInfoReqPacket.Parse(packet);

        // 0x1D is the 정보 (profile view) request and it asks by USER ID, so it can name
        // ANY player the asker can see. Comparing the id against the asker's own profile
        // and answering NotFound otherwise meant every button except "view myself"
        // returned nothing.
        if (lobby.TryResolveUserInfo(client, requestedUserId, out string nickname, out _))
        {
            // The panel draws level/exp/money/icon straight out of the block, so pass the
            // profile - without it every player showed the same hardcoded capture.
            LocalPlayerProfile? target = lobby.ResolveUserProfile(client, requestedUserId);
            client.Send(OnUserInfoAckPacket.Build(
                requestedUserId, nickname, target, target?.Collection));
            return;
        }

        // Only when nobody owns that id is it the battle club's kick. Trying the kick
        // FIRST turned every profile view of a room occupant into a removal.
        if (lobby.KickRoomMember(client, requestedUserId))
        {
            return;
        }

        client.Send(OnUserInfoResNotFoundPacket.Build());
    }
}

public sealed class GetItemReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.GetItemReq;

    public void Handle(Client client, Packet packet)
    {
        GetItemReqPacket.Parse(packet);
        // Mid-song in an item battle this is the client picking up an item note.
        if (lobby.TryHandleBattleGetItem(client))
        {
            return;
        }

        // This Korean sender exists only in the active item-battle scene. Reject a
        // stale/out-of-context request while still clearing its pending flag.
        client.Send(ItemFailurePackets.BuildGetItemFail());
    }
}

public sealed class ItemLevelUpReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.ItemLevelUpReq;

    public void Handle(Client client, Packet packet)
    {
        ItemLevelUpReqPacket.Parse(packet);
        if (lobby.TryHandleBattleLevelUp(client))
        {
            return;
        }

        client.Send(ItemFailurePackets.BuildItemLevelUpFail());
    }
}

public sealed class UseItemReqHandler(LocalPlayerStore? players, LocalLobby lobby)
    : IPacketHandler
{
    public PacketId Id => PacketId.UseItemReq;

    public void Handle(Client client, Packet packet)
    {
        UseItemRequest request = UseItemReqPacket.Parse(packet);
        // In an item battle the byte is the TARGET SLOT the item is fired at, not an
        // inventory slot - the same id means two different things by context.
        if (lobby.TryHandleBattleUseItem(client, request.Slot))
        {
            return;
        }

        LocalPlayerStore activePlayers = client.PlayerStoreOr(players);
        if (!activePlayers.Use(request))
        {
            client.Send(ItemFailurePackets.BuildUseItemFail());
            return;
        }

        client.Send(OnUseItemAckPacket.Build());
        // Using an item consumes a charge or removes it outright, but OnUseItemAck is only
        // 16 bytes and carries no inventory - unlike the shop acknowledgements, which each
        // ship the whole box. Without this push the client keeps showing the pre-use box
        // until the next relogin.
        client.Send(OnUpdateUserInventoryShopItemInfPacket.BuildItemBox(
            activePlayers.Read(profile => profile.UserId),
            activePlayers.Read(profile => profile.Inventory.ItemBoxItems())));
    }
}

public sealed class UseEffectorSetInfHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.UseEffectorSetInf;

    public void Handle(Client client, Packet packet) =>
        lobby.BroadcastEffectorSet(client, UseEffectorSetInfPacket.Parse(packet));
}

// Client-originated telemetry the retail server does not answer: the keepalive
// reply, the obfuscated anti-cheat probe, verification codes, and the OnCheckDataReq
// reply (0x72). The client makes progress without a response, so we accept them
// explicitly rather than leave them to the unhandled-packet log. 0xFD is different:
// it opens the equipment view and requires OnSystemInfoAck below.
public sealed class AliveAckHandler : IPacketHandler
{
    public PacketId Id => PacketId.AliveAck;

    public void Handle(Client client, Packet packet)
    {
    }
}

public sealed class ProbeObfuscatedHandler : IPacketHandler
{
    public PacketId Id => PacketId.ProbeObfuscated;

    public void Handle(Client client, Packet packet)
    {
    }
}

public sealed class VerifyCodeInfHandler : IPacketHandler
{
    public PacketId Id => PacketId.VerifyCodeInf;

    public void Handle(Client client, Packet packet)
    {
    }
}

public sealed class CheckDataReplyHandler : IPacketHandler
{
    public PacketId Id => PacketId.sub_437370;

    public void Handle(Client client, Packet packet)
    {
    }
}

// 0xFD is the equipment-room ("소지품"/Belongings) open request. The client stalls
// that screen until it receives OnSystemInfoAck (0xFE), which clears its in-flight
// flag and opens the dialog over the inventory we already sent. Retail replies with
// 0xFE immediately, so must we — a no-op leaves the equipment room permanently blank.
public sealed class Unknown434B40Handler : IPacketHandler
{
    public PacketId Id => PacketId.sub_434B40;

    public void Handle(Client client, Packet packet) =>
        client.Send(OnSystemInfoAckPacket.Build());
}
