using Arrowgene.DJMaxOnline.Server.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Handler;

// Equipment screen: equip a mount loadout (avatar/gear). The client sends its 8-slot
// loadout; we persist it and echo it back with the success result so the client
// applies the equip. The 8-slot mount inventory is the equipped set, so storing it
// here also restores the loadout at the next login.
public sealed class MountItemReqHandler(LocalPlayerStore? players) : IPacketHandler
{
    public PacketId Id => PacketId.MountItemReq;

    public void Handle(Client client, Packet packet)
    {
        byte[] loadout = MountItemReqPacket.Parse(packet);
        client.Send(OnMountItemAckPacket.Build(
            client.PlayerStoreOr(players).Mount(loadout)));
    }
}

public sealed class DeleteItemReqHandler(LocalPlayerStore? players) : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(DeleteItemReqHandler));

    public PacketId Id => PacketId.DeleteItemReq;

    public void Handle(Client client, Packet packet)
    {
        DeleteItemRequest request = DeleteItemReqPacket.Parse(packet);
        Logger.Info(client,
            $"DeleteItemReq item 0x{request.ItemId:X} (value 0x{request.Value:X}).");
        client.Send(OnDeleteItemAckPacket.Build(
            client.PlayerStoreOr(players).Delete(request)));
    }
}

public sealed class GetPresentItemReqHandler(LocalPlayerStore? players) : IPacketHandler
{
    public PacketId Id => PacketId.GetPresentItemReq;

    public void Handle(Client client, Packet packet) =>
        client.Send(OnGetPresentItemAckPacket.Build(
            client.PlayerStoreOr(players).GetPresent(GetPresentItemReqPacket.Parse(packet))));
}

// Client-originated notifications and dispatch cases the retail server does not
// answer in this server's flow (final score report, unused eKey auth step, invite
// relays, course-mode requests, and several RE'd-but-unnamed cases). Accepting them
// explicitly keeps them out of the unhandled-packet log; none require a response
// during normal single-player/room play.
public sealed class StageResultInfHandler : IPacketHandler
{
    public PacketId Id => PacketId.StageResultInf;

    public void Handle(Client client, Packet packet)
    {
    }
}

public sealed class AuthenticateInSndeKeyAckHandler : IPacketHandler
{
    public PacketId Id => PacketId.AuthenticateInSndeKeyAck;

    public void Handle(Client client, Packet packet)
    {
    }
}

/// <summary>
/// CourseRankReq (0x83, 5 bytes: course id at +3). Answered with the 0x84 ranking board.
/// The client's sender sub_432990 sets a pending flag at net+895196 that only the ack
/// clears, so an unanswered request blocks every later ranking query.
/// </summary>
public sealed class Sub434390Handler(LocalPlayerStore? players, LocalLobby lobby)
    : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(Sub434390Handler));

    public PacketId Id => PacketId.sub_434390;

    public void Handle(Client client, Packet packet)
    {
        CourseSelection request = CourseSelectionReqPacket.Parse(packet);
        // The repository board is shared by every account but remains per channel: SEOUL
        // and TOKYO play different charts for the same course, so mixing their rows would
        // rank runs that were never comparable.
        byte keyMode = (byte)lobby.Channel.KeyMode;
        IReadOnlyList<CourseRankEntry> ranking = client.PlayerStoreOr(players)
            .CourseRanking(request.CourseId, keyMode);
        Logger.Info(client,
            $"CourseRankReq course {request.CourseId} ({keyMode}-key): " +
            $"{ranking.Count} ranked row(s).");
        client.Send(OnCourseRankAckPacket.Build(request.CourseId, ranking));
    }
}

/// <summary>
/// ChangeCourseReq (0x85, 5 bytes: course id at +3), sent whenever the course cursor
/// moves. The 0x86 ack must echo the same id: sub_4914A3 writes it into the scene's
/// current-course field and sub_49215D keeps re-sending while the two differ.
/// </summary>
public sealed class Sub434450Handler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(Sub434450Handler));

    public PacketId Id => PacketId.sub_434450;

    public void Handle(Client client, Packet packet)
    {
        CourseSelection request = CourseSelectionReqPacket.Parse(packet);
        // The client re-sends this every time the cursor lands on a course, so only a
        // genuine change discards the progress made in the course being left.
        if (client.SelectedCourseId != request.CourseId)
        {
            client.SelectedCourseId = request.CourseId;
            client.ResetCourseProgress();
        }
        Logger.Info(client, $"ChangeCourseReq selected course {request.CourseId}.");
        client.Send(OnChangeCourseAckPacket.Build(request.CourseId));
    }
}

/// <summary>
/// ContinueCourseReq (0x87, 5 bytes). Only the 0x88 ack's arrival matters - it clears the
/// pending flag at net+895204 - because no scene reads the ack body.
/// </summary>
public sealed class Sub434510Handler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(Sub434510Handler));

    public PacketId Id => PacketId.sub_434510;

    public void Handle(Client client, Packet packet)
    {
        CourseSelection request = CourseSelectionReqPacket.Parse(packet);
        Logger.Info(client, $"ContinueCourseReq value {request.CourseId}.");
        client.Send(OnContinueCourseAckPacket.Build(request.CourseId));
    }
}

/// <summary>
/// PostCourseItemReq (0x8A, a bare 3-byte signal) acknowledges the course reward shown by
/// server packet 0x89. This local server grants and persists the selected item before it
/// announces the result, so this request must not grant it a second time.
/// </summary>
public sealed class Sub434620Handler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(Sub434620Handler));

    public PacketId Id => PacketId.sub_434620;

    public void Handle(Client client, Packet packet)
    {
        PostCourseItemReqPacket.Parse(packet);
        Logger.Info(client,
            $"PostCourseItemReq for course {client.SelectedCourseId?.ToString() ?? "none"};" +
            " reward was already committed before the result announcement.");
    }
}

public sealed class Sub436120Handler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(Sub436120Handler));

    public PacketId Id => PacketId.sub_436120;

    public void Handle(Client client, Packet packet)
    {
        UnknownA3ReqPacket.Parse(packet);
        Logger.Info(client,
            "Received bare 0xA3 lobby/invite transition request; no response is " +
            "sent until its retail state transition is capture-verified.");
    }
}

public sealed class Sub4362D0Handler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(Sub4362D0Handler));

    public PacketId Id => PacketId.sub_4362D0;

    public void Handle(Client client, Packet packet)
    {
        byte state = PeerStateReqPacket.Parse(packet);
        Logger.Info(client,
            $"Received 0x56 peer-state value {state}; the Korean 0x57 receive path " +
            "is dispatcher-inert, so no speculative acknowledgement was sent.");
    }
}

public sealed class UpdateUserIconReqHandler(
    LocalPlayerStore? players,
    LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.sub_437900;

    public void Handle(Client client, Packet packet) =>
        lobby.UpdateLocalIcon(
            client.PlayerStoreOr(players), client, UpdateUserIconReqPacket.Parse(packet));
}

public sealed class UseMountItemInfHandler(LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.UseMountItemInf;

    public void Handle(Client client, Packet packet) =>
        lobby.SetMountSnapshot(client, UseMountItemInfPacket.Parse(packet));
}

public sealed class Sub4323D0Handler : IPacketHandler
{
    public PacketId Id => PacketId.sub_4323D0;

    public void Handle(Client client, Packet packet)
    {
    }
}
