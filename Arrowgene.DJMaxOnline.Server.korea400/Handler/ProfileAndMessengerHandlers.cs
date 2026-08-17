using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Handler;

/// <summary>Handles the retail profile screen's native nickname-save request.</summary>
public sealed class UpdateUserAccountNickReqHandler(
    LocalPlayerStore? players,
    LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.UpdateUserAccountNickReq;

    public void Handle(Client client, Packet packet)
    {
        UpdateUserAccountNickRequest request =
            UpdateUserAccountNickReqPacket.Parse(packet);
        if (string.IsNullOrWhiteSpace(request.Nickname) ||
            request.Nickname[0] == '~' ||
            request.Nickname.Length >= OnUserIdInfoInfPacket.NicknameSize)
        {
            client.Send(OnUpdateUserAccountNickAckPacket.Build(
                UpdateUserAccountNickResult.Unavailable));
            return;
        }

        client.PlayerStoreOr(players).Update(profile =>
        {
            profile.Nickname = request.Nickname;
            return 0;
        });
        client.Send(OnUpdateUserAccountNickAckPacket.Build(
            UpdateUserAccountNickResult.Success));
        lobby.BroadcastLocalProfile(client);
    }
}

/// <summary>
/// Completes the second half of the retail profile save. The executable uses
/// result 60 as success and immediately displays its storage-failed message for
/// every other result.
/// </summary>
public sealed class UpdateUserProfileReqHandler(
    LocalPlayerStore? players,
    LocalLobby lobby) : IPacketHandler
{
    public PacketId Id => PacketId.UpdateUserProfileReq;

    public void Handle(Client client, Packet packet)
    {
        UpdateUserProfileRequest request = UpdateUserProfileReqPacket.Parse(packet);
        client.PlayerStoreOr(players).Update(profile =>
        {
            profile.State = request.State;
            profile.ProfileCode = request.ProfileCode;
            return 0;
        });
        client.Send(OnUpdateUserProfileAckPacket.Build(
            UpdateUserProfileResult.Success));
        lobby.BroadcastLocalProfile(client);
    }
}

/// <summary>
/// Gives the retail messenger a native error acknowledgement instead of
/// leaving its request pending forever. The local server currently exposes one
/// persisted identity, so there is no distinct account directory from which an
/// add-friend request could be resolved.
/// </summary>
public sealed class MsgRegisterUserReqHandler(
    LocalPlayerStore? players,
    LocalLobby lobby) : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(MsgRegisterUserReqHandler));

    public PacketId Id => PacketId.MsgRegisterUserReq;

    public void Handle(Client client, Packet packet)
    {
        MsgRegisterUserRequest request = MsgRegisterUserReqPacket.Parse(packet);
        // The player store only knows the caller's own profile and roster, so resolve the
        // target across every connected player first - otherwise adding another logged-in
        // account by nickname always came back UserNotFound.
        uint? resolved = null;
        string? resolvedAccount = null;
        if (request.UserId != 0 &&
            lobby.TryResolveUserInfo(client, request.UserId, out _, out string ownerAccount))
        {
            resolved = request.UserId;
            resolvedAccount = ownerAccount;
        }
        else if (lobby.TryResolveUserId(client, request.Nickname, out uint byNickname))
        {
            resolved = byNickname;
            lobby.TryResolveUserInfo(client, byNickname, out _, out resolvedAccount);
        }
        // The account id is what the contact is really saved against; the user id is only
        // a cache, because it is randomised on the target's next login.
        MsgRegisterUserResult result = client.PlayerStoreOr(players)
            .Messenger(request, resolved, resolvedAccount);
        Logger.Info(client,
            $"Messenger {request.Operation} " +
            $"(nickname '{request.Nickname}', id {request.UserId}, " +
            $"group {request.GroupIndex}) -> {result}.");

        client.Send(OnMsgRegisterUserAckPacket.Build(result));
        // The ack only reports the outcome; the client redraws its friends panel from the
        // list packets, so a successful change has to be followed by the new lists or the
        // window keeps showing the pre-change state until relogin.
        if (result == MsgRegisterUserResult.Success)
        {
            lobby.SendMessengerBook(client);
            if (request.Operation == MessengerOperation.AddFriend && resolved is uint added)
            {
                // Both sides need rebuilding: presence only turns on once BOTH hold each
                // other, so the add can complete a pair and bring the other side online.
                lobby.AnnounceFriendRequest(client, added);
                lobby.RefreshMessengerPresence(client);
            }
        }
    }
}
