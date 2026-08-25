namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

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
            OnInventoryInfoInfPacket.Build(inventory, profile.Collection),
            // Retail fills the in-game item box (道具箱) via these per-section updates,
            // not the bulk OnInventoryInfoInf, so send them too or the box stays blank.
            // 0x2A is the first collection cache, not an item-id table: raw item ids
            // turn 1025 into collection code 0x401 and draw a bogus Rainbow disc.
            OnUpdateUserInventoryDefaultItemInfPacket.BuildKorean(
                profile.WireUserId, profile.Collection),
            OnUpdateUserInventoryEventItemInfPacket.BuildKorean(
                profile.WireUserId, []),
            OnUpdateUserInventoryShopItemInfPacket.Build(profile.WireUserId, inventory.ShopItems),
            // JP 0x2E is the independent present/gift-box cache. The client does
            // not populate this live cache from the account-class packet (0x2F).
            OnUpdateUserInventoryPresentItemInfPacket.Build(
                profile.WireUserId, inventory.PresentItems),
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

    /// <summary>
    /// Packets that populate the Chinese client's profile caches <em>before</em> its
    /// compact 47-byte login acknowledgement. China does not carry this data in
    /// <c>OnLogInAck</c> (unlike Korea), and that acknowledgement is the event that
    /// creates the lobby scene. Sending this sequence afterward leaves the first scene
    /// rendered from its fallback identity ("Player"/female) until the next scene
    /// transition redraws it.
    /// </summary>
    public static IReadOnlyList<Packet> BuildBeforeLoginAcknowledgement(
        LocalPlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        InventorySnapshot inventory = profile.Inventory.CreateSnapshot();
        return new Packet[]
        {
            // This is the client-side user cache.  Its record+62 is gender, and the
            // local profile page resolves the displayed name/icon through this entry.
            OnUserIdInfoInfPacket.Build(new[]
            {
                LobbyUserIdentity.CreateLocal(profile)
            }),
            // The JP handler copies all 135 bytes from packet+3 into its local-profile
            // block.  Follow it with the independent live-property update, which is what
            // the lobby-mode UI uses for the current level and statistics.
            OnUserInfoInfPacket.Build(UserInfoSnapshot.CreateLocal(profile)),
            OnUpdateUserPropertyInfPacket.Build(profile.WireUserId, profile.Progress),
            OnInventoryInfoInfPacket.Build(inventory, profile.Collection),
            OnUpdateUserInventoryDefaultItemInfPacket.BuildKorean(
                profile.WireUserId, profile.Collection),
            OnUpdateUserInventoryEventItemInfPacket.BuildKorean(
                profile.WireUserId, []),
            OnUpdateUserInventoryShopItemInfPacket.Build(
                profile.WireUserId, inventory.ShopItems),
            OnUpdateUserInventoryPresentItemInfPacket.Build(
                profile.WireUserId, inventory.PresentItems),
            OnUpdateUserAccountClassInfPacket.Build(
                profile.WireUserId, profile.AccountClass),
            OnUpdateUserInventoryMountItemInfPacket.Build(
                profile.WireUserId, inventory.MountItems)
        };
    }
}
