using System.Linq;
using Arrowgene.DJMaxOnline.Server.Japan400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Handler;

public sealed class ChatInfHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.ChatInf;

    public void Handle(Client client, Packet packet) =>
        lobby.RelayChat(client, ChatInfPacket.Parse(packet));
}

public sealed class WChatReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.WChatReq;

    public void Handle(Client client, Packet packet) =>
        lobby.RelayWhisper(client, WChatReqPacket.Parse(packet));
}

/// <summary>
/// The DJ메신저 send. Leaving this unhandled is not merely a missing feature: 0xFA is
/// dynamic, so without a meta the factory cannot even measure it and the whole stream
/// desyncs on the first message.
/// </summary>
public sealed class MsgChatReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.MsgChatReq;

    public void Handle(Client client, Packet packet) =>
        lobby.RelayMessengerChat(client, MsgChatReqPacket.Parse(packet));
}

public sealed class UserIdInfoReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.UserIdInfoReq;

    public void Handle(Client client, Packet packet) =>
        lobby.SendIdentityAck(client, UserIdInfoReqPacket.Parse(packet));
}

public sealed class PingTestInfHandler : IPacketHandler
{
    public PacketId Id => PacketId.PingTestInf;

    public void Handle(Client client, Packet packet)
    {
        client.PingTime = DateTime.UtcNow;
        // The ping test is client-initiated: the client sends PingTestInf (0x04)
        // and waits for the server to echo OnPingTestInf (0x03) — the same
        // request/ack shape as KeepAuthenticateInReq. The client's 0x03 handler
        // only clears its "awaiting pong" state, so without this echo the client
        // never sees a reply. Mirrors KeepAuthenticateInReqHandler.
        client.Send(OnPingTestInfPacket.Build());
    }
}

public sealed class InviteRejectReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.InviteRejectReq;

    /// <summary>
    /// The 초대거부 toggle. THE ACK IS NOT OPTIONAL: sub_434170 raises a pending flag
    /// (net+895004) before sending and only sub_4341F0 — the ack handler — lowers it, so
    /// staying silent left the checkbox stuck after one click for the rest of the session.
    /// The client also does not remember what it asked for; net+895008 is written from the
    /// ACK's byte, so the echo is what actually moves the tick.
    /// </summary>
    public void Handle(Client client, Packet packet)
    {
        bool refused = InviteRejectReqPacket.Parse(packet).Refused;
        lobby.SetInvitesRefused(client, refused);
        client.Send(OnInviteRejectAckPacket.Build(refused));
    }
}

public sealed class QuickInviteReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.QuickInviteReq;

    public void Handle(Client client, Packet packet)
    {
        QuickInviteReqPacket.Parse(packet);
        // This used to always answer NoUsersAvailable - correct when the server had one
        // account, wrong once it had several. QuickInvite picks a real target and sends
        // the invitation (0xA2); the ack still clears the pending UI either way.
        client.Send(OnQuickInviteAckPacket.Build(lobby.QuickInvite(client)));
    }
}

public sealed class CreateRoomReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.CreateRoomReq;

    public void Handle(Client client, Packet packet) =>
        lobby.CreateRoom(client, CreateRoomReqPacket.Parse(packet));
}

public sealed class JoinRoomReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.JoinRoomReq;

    public void Handle(Client client, Packet packet) =>
        lobby.JoinRoom(client, JoinRoomReqPacket.Parse(packet));
}

public sealed class LeaveRoomReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.LeaveRoomReq;

    public void Handle(Client client, Packet packet)
    {
        LeaveRoomReqPacket.Parse(packet);
        lobby.LeaveRoom(client);
    }
}

public sealed class ReadyReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.ReadyReq;

    public void Handle(Client client, Packet packet)
    {
        ReadyReqPacket.Parse(packet);
        lobby.ToggleReady(client);
    }
}

public sealed class TeamControlReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.TeamControlReq;

    public void Handle(Client client, Packet packet) =>
        lobby.ChangeTeam(client, TeamControlReqPacket.Parse(packet));
}

public sealed class RoomChangeInfoReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.RoomChangeInfoReq;

    public void Handle(Client client, Packet packet) =>
        lobby.ChangeRoom(client, RoomChangeInfoReqPacket.Parse(packet));
}

public sealed class ChangeDiscReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.ChangeDiscReq;

    public void Handle(Client client, Packet packet) =>
        lobby.ChangeDisc(client, ChangeDiscReqPacket.Parse(packet));
}

public sealed class SlotControlReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.SlotControlReq;

    public void Handle(Client client, Packet packet) =>
        lobby.ToggleSlot(client, SlotControlReqPacket.Parse(packet));
}

public sealed class UseEffectorInfHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.UseEffectorInf;

    public void Handle(Client client, Packet packet) =>
        lobby.SetEffector(client, UseEffectorInfPacket.Parse(packet));
}

public sealed class StartReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.StartReq;

    public void Handle(Client client, Packet packet) =>
        lobby.StartGame(client, StartReqPacket.Parse(packet));
}

public sealed class PlayStartReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.PlayStartReq;

    public void Handle(Client client, Packet packet)
    {
        // JP PlayStartReq is an 11-byte bare signal plus the reserved tail. It just means
        // "chart finished loading"; it has no structured body to parse.
        lobby.CompleteLoad(client);
    }
}

public sealed class PlaySkipReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.PlaySkipReq;

    public void Handle(Client client, Packet packet)
    {
        // JP sends 0x67 as a bare signal followed by its reserved eight-byte tail.
        lobby.SkipPlay(client);
    }
}

public sealed class PlayOverReqHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.PlayOverReq;

    public void Handle(Client client, Packet packet)
    {
        // JP PlayOverReq is a bare signal followed by its reserved eight-byte tail. It is
        // a request the client blocks on, so it always gets an OnPlayOverInf back even
        // when the 0x6F result already finalized the song.
        lobby.PlayOver(client);
    }
}

/// <summary>
/// The retail client signals end-of-song with packet 0x6F (mislabeled "Test00"),
/// not PlayOverReq. It carries the client's final score; we only need to complete
/// the result flow so the game reaches the score screen and resumes.
/// </summary>
public sealed class SongCompleteHandler(LocalLobby lobby) : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(SongCompleteHandler));

    public PacketId Id => PacketId.Test00;

    public void Handle(Client client, Packet packet)
    {
        StageResult? result = lobby.RecordStageResult(client, packet);
        if (result == null)
        {
            return;
        }
        Logger.Info(client,
            $"StageResult: currentCombo={result.CurrentCombo} " +
            $"maxCombo={result.MaxCombo} notesHit={result.NotesHit} " +
             $"total={result.TotalNotes} break={result.Breaks} " +
             $"accuracy={result.Accuracy:0.00} score={result.Score} " +
             $"rank={result.Rank} bonus={result.BonusScore} " +
             $"gauge={result.Gauge:0.#}" +
             $"{(result.Failed ? " (failed)" : string.Empty)}.");
        lobby.FinishPlay(client);
    }
}

public sealed class PlayStateInfHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.PlayStateInf;

    public void Handle(Client client, Packet packet) =>
        lobby.RelayPlayState(client, PlayStateInfPacket.Parse(packet));
}

public sealed class UbsAccountAuthenticationReqHandler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(UbsAccountAuthenticationReqHandler));

    public PacketId Id => PacketId.UbsAccountAuthenticationReq;

    public void Handle(Client client, Packet packet)
    {
        UbsAccountAuthenticationReqPacket.Parse(packet);
        // This executable registers 0x12D as an ignored 8-byte packet and has no
        // receive registration or dispatcher case for 0x12E/0x130. Earlier replies
        // came from another client build and either threw in the fixed builder or
        // desynchronized this stream. Ordinary shop/purchase flow does not use them.
        Logger.Info(client,
            "Ignored unsupported UBS request 0x12C; no compatible response is known.");
    }
}

public sealed class PurchaseItemReqHandler(LocalPlayerStore? players) : IPacketHandler
{
    public PacketId Id => PacketId.PurchaseItemReq;

    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(PurchaseItemReqHandler));

    public void Handle(Client client, Packet packet)
    {
        PurchaseItemRequest request = PurchaseItemReqPacket.Parse(packet);
        Logger.Info(client,
            "PurchaseItemReq items: " +
            string.Join(", ", request.Items.Select(i => $"{{id=0x{i.ItemId:X},exp=0x{i.Expiration:X}}}")));
        PurchaseItemResponse response = client.PlayerStoreOr(players).Purchase(request);
        client.Send(OnPurchaseItemAckPacket.Build(response));
    }
}

public sealed class ResaleItemReqHandler(LocalPlayerStore? players) : IPacketHandler
{
    public PacketId Id => PacketId.ResaleItemReq;

    public void Handle(Client client, Packet packet)
    {
        ResaleItemResponse response = client.PlayerStoreOr(players)
            .Resale(ResaleItemReqPacket.Parse(packet));
        client.Send(OnResaleItemAckPacket.Build(response));
    }
}
