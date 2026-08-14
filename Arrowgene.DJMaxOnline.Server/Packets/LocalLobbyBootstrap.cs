namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>Builds the retail packet order used immediately after LogInReq.</summary>
public static class LocalLobbyBootstrap
{
    // ALL SESSION VALUES MUST STAY ZERO ON THE KOREAN CLIENT. sub_436A50 (0xD3) writes
    // SessionValue1 to net+895140 and Mode/Flags into the 895136 dword — and that memory is
    // ALSO the course module's request gates: sub_432AF0 only sends ContinueCourseReq
    // (0x87) while net+895140 == 0, and sub_432A40 only sends ChangeCourseReq (0x85) while
    // net+895136 == 0. Nothing but the matching 0x88/0x86 acks ever clears them, and the
    // client asks for neither, so the capture's 0x000607E8 silently killed "continue
    // course" for the whole session from the moment of login. The values drive no UI
    // (they are stored and never read back), so zeroing them costs nothing.
    public static GameStartParameters GameStartParameters { get; } = new(
        Mode: 0,
        Flags: 0,
        SessionValue1: 0,
        SessionValue2: 0,
        SessionValue3: 0,
        SessionValue4: 0,
        SessionValue5: 0);

    public static IReadOnlyList<Packet> Build(
        LocalPlayerProfile profile,
        ChannelInfo channel,
        ReadOnlySpan<byte> connectionSeed,
        string downloadUrl)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(channel);

        InventorySnapshot inventory = profile.Inventory.CreateSnapshot();
        return new Packet[]
        {
            OnUserIdInfoInfPacket.Build(new[]
            {
                LobbyUserIdentity.CreateLocal(profile)
            }),
            OnEnvironmentInfPacket.Build(OnEnvironmentInfPacket.DifficultyMixFilter),
            OnEnvironmentInfPacket.Build(OnEnvironmentInfPacket.DownloadUrl(downloadUrl)),
            OnUserInfoInfPacket.Build(UserInfoSnapshot.CreateLocal(profile)),
            // Populates the client's live lobby user-property values (the client
            // stores Level at this+794185, which the mode UI requires to be >= 2 —
            // sub_4479FC/sub_4445DA). We build this packet but never sent it, leaving
            // those fields at 0 and gating lobby modes.
            OnUpdateUserPropertyInfPacket.Build(profile.WireUserId, profile.Progress),
            OnInventoryInfoInfPacket.Build(inventory),
            // Retail fills the in-game item box (道具箱) via these per-section updates,
            // not the bulk OnInventoryInfoInf, so send them too or the box stays blank.
            OnUpdateUserInventoryDefaultItemInfPacket.Build(profile.WireUserId, inventory.DefaultItems),
            OnUpdateUserInventoryEventItemInfPacket.Build(profile.WireUserId, inventory.EventItems),
            OnUpdateUserInventoryShopItemInfPacket.Build(profile.WireUserId, inventory.ShopItems),
            // 0x2E is an account-class update, not a present-item section. Present
            // items are already included in the bulk inventory packet above.
            OnUpdateUserAccountClassInfPacket.Build(profile.WireUserId, profile.AccountClass),
            OnUpdateUserInventoryMountItemInfPacket.Build(profile.WireUserId, inventory.MountItems),
            OnMessengerInfoInfPacket.Build(MessengerSnapshot.Empty),
            OnLogInAckPacket.BuildFromConnectionSeed(connectionSeed),
            // This is server-owned account availability. Send it only after the
            // login acknowledgement has established the game-channel session.
            OnCourseListInfPacket.Build(profile.AvailableCourseIds),
            OnChatInfPacket.BuildAscii(
                $"== Welcome to {channel.FullName} ==")
        };
    }
}
