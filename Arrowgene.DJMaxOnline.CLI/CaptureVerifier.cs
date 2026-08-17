using System.Buffers.Binary;
using System.Net;
using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline;

public sealed record CaptureVerificationResult(
    int CaptureCount,
    int PacketCount,
    int DistinctPacketIds,
    int TypedRoundTrips,
    int CaptureVerifiedReceiveCases,
    int DeclaredButUncapturedReceiveCases,
    int HandlerOnlyReceiveCases);

public static class CaptureVerifier
{
    public static CaptureVerificationResult VerifyDirectory(string captureDirectory)
    {
        ValidateCryptoSeedDerivation();
        ValidateLocalChannels();
        ValidateLocalLobbyBootstrap();

        string[] files = Directory.GetFiles(captureDirectory, "*.yaml")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"No YAML captures found in {captureDirectory}.");
        }

        HashSet<PacketId> ids = new();
        HashSet<PacketId> receiveIds = new();
        int packetCount = 0;
        int roundTrips = 0;
        foreach (string file in files)
        {
            (int packets, int typed) = VerifyFile(file, ids, receiveIds);
            packetCount += packets;
            roundTrips += typed;
        }

        if (ReceivePacketCatalog.All.Count != ReceivePacketCatalog.ExpectedCaseCount)
        {
            throw new InvalidOperationException("The sub_42FB20 receive catalog is incomplete.");
        }
        if (ReceivePacketCatalog.Registrations.Count !=
            ReceivePacketCatalog.ExpectedRegistrationCount)
        {
            throw new InvalidOperationException("The sub_42F440 registration catalog is incomplete.");
        }

        if (!receiveIds.SetEquals(ReceivePacketCatalog.CaptureVerifiedIds))
        {
            string missing = string.Join(", ",
                ReceivePacketCatalog.CaptureVerifiedIds.Except(receiveIds));
            string unexpected = string.Join(", ",
                receiveIds.Except(ReceivePacketCatalog.CaptureVerifiedIds));
            throw new InvalidOperationException(
                $"Capture-evidence catalog mismatch. Missing: [{missing}]; " +
                $"unexpected: [{unexpected}].");
        }

        int declaredButUncaptured = ReceivePacketCatalog.All.Count(definition =>
            ReceivePacketCatalog.GetFramingEvidence(definition.Id) ==
            ReceiveFramingEvidence.DeclaredButUncaptured);
        int handlerOnly = ReceivePacketCatalog.All.Count(definition =>
            ReceivePacketCatalog.GetFramingEvidence(definition.Id) ==
            ReceiveFramingEvidence.HandlerOnly);

        return new CaptureVerificationResult(
            files.Length,
            packetCount,
            ids.Count,
            roundTrips,
            receiveIds.Count,
            declaredButUncaptured,
            handlerOnly);
    }

    private static void ValidateCryptoSeedDerivation()
    {
        byte[] seed = new byte[OnConnectAckPacket.SeedSize];
        seed[28] = 0x34;
        seed[29] = 0x12;
        if (DjMaxCrypto.DeriveSumSeed(seed) != 0x00001234u)
        {
            throw new InvalidOperationException("Positive signed sum-seed derivation failed.");
        }

        seed[28] = 0x80;
        seed[29] = 0xFF;
        if (DjMaxCrypto.DeriveSumSeed(seed) != 0xFFFFFF80u)
        {
            throw new InvalidOperationException("Negative signed sum-seed derivation failed.");
        }
    }

    private static void ValidateLocalChannels()
    {
        Setting setting = new();
        IReadOnlyList<ChannelInfo> channels = LocalChannelCatalog.Create(setting);
        if (channels.Count != 2 ||
            channels[0].Port != setting.ServerPort ||
            channels[1].Port != setting.SevenKeyServerPort ||
            !channels.All(channel => channel.Address.Equals(IPAddress.Loopback)) ||
            channels.Any(channel => channel.ChannelId !=
                LocalChannelCatalog.ChannelIdForPort(channel.Port)))
        {
            throw new InvalidOperationException("Local 5-key/7-key channel catalog is invalid.");
        }

        Packet packet = OnChannelInfoInfPacket.Build(channels);
        IReadOnlyList<ChannelInfo> parsed = OnChannelInfoInfPacket.Parse(packet);
        if (parsed.Count != 2 ||
            parsed[0].Port != setting.ServerPort ||
            parsed[1].Port != setting.SevenKeyServerPort)
        {
            throw new InvalidOperationException("Local channel packet did not round-trip.");
        }
    }

    private static void ValidateLocalLobbyBootstrap()
    {
        Setting setting = new();
        LocalPlayerProfile profile = CreateFullyPopulatedLocalProfile();
        profile = LocalPlayerProfileFile.Deserialize(
            LocalPlayerProfileFile.Serialize(profile));
        ValidateLocalPlayerUpdates(profile);
        ValidateLocalLobbyServices(profile);
        PacketId[] expectedOrder =
        {
            PacketId.OnUserIdInfoInf,
            PacketId.OnEnvironmentInf,
            PacketId.OnEnvironmentInf,
            PacketId.OnUserInfoInf,
            PacketId.OnUpdateUserPropertyInf,
            PacketId.OnInventoryInfoInf,
            PacketId.OnUpdateUserInventoryDefaultItemInf,
            PacketId.OnUpdateUserInventoryEventItemInf,
            PacketId.OnUpdateUserInventoryShopItemInf,
            PacketId.OnUpdateUserAccountClassInf,
            PacketId.OnUpdateUserInventoryMountItemInf,
            PacketId.OnMessengerInfoInf,
            PacketId.OnLogInAck,
            PacketId.OnCourseListInf,
            PacketId.OnChatInf
        };
        IReadOnlyList<ChannelInfo> channels = LocalChannelCatalog.Create(setting);
        foreach (ChannelInfo channel in channels)
        {
            ValidateLocalGameChannelFlow(profile, channel, expectedOrder);
        }
    }

    private static LocalPlayerProfile CreateFullyPopulatedLocalProfile()
    {
        LocalPlayerProfile profile = new()
        {
            UserId = 0x10203040,
            AccountId = "LOCAL_ACCOUNT",
            SecondaryId = "LOCAL_SECONDARY",
            Nickname = "LOCAL_PLAYER",
            State = 3,
            ProfileCode = 0x1234,
            IconId = 0x00008824,
            ProfileFlags = 0x34567890,
            AccountClass = 0x45678901,
            Reserved = 0x5678,
            Progress = new LocalPlayerProgress
            {
                Level = 19,
                Experience = 0x11121314,
                Money = 0x21222324,
                Cash = 0x25262728,
                Wins = 31,
                Losses = 32,
                Draws = 33,
                MiscStatistics = Enumerable.Range(0, LocalPlayerProgress.MiscStatisticCount)
                    .Select(index => 0x31000000u + (uint)index)
                    .ToArray()
            }
        };
        profile.Inventory.State = 0x61626364;
        profile.Inventory.SetDefaultItem(0, 0x1001);
        profile.Inventory.SetDefaultItem(OnInventoryInfoInfPacket.DefaultItemCount - 1, 0x10FF);
        profile.Inventory.SetEventItem(0, 0x2001);
        profile.Inventory.SetEventItem(OnInventoryInfoInfPacket.EventItemCount - 1, 0x20FF);
        profile.Inventory.SetShopItem(0, 0x3001, 0x3002);
        profile.Inventory.SetShopItem(
            OnInventoryInfoInfPacket.ShopItemCount - 1, 0x30FE, 0x30FF);
        profile.Inventory.SetPresentItem(0, 0x4001, 0x4002, 0x4003);
        profile.Inventory.SetPresentItem(
            OnInventoryInfoInfPacket.PresentItemCount - 1, 0x40FD, 0x40FE, 0x40FF);
        profile.Inventory.SetMountItem(0, 0x5001, 0x5002);
        profile.Inventory.SetMountItem(
            OnInventoryInfoInfPacket.MountItemCount - 1, 0x50FE, 0x50FF);
        profile.Validate();
        return profile;
    }

    private static void ValidateLocalPlayerUpdates(LocalPlayerProfile profile)
    {
        InventorySnapshot expectedInventory = profile.Inventory.CreateSnapshot();
        IReadOnlyList<Packet> packets = LocalPlayerDataUpdatePackets.Build(profile);
        PacketId[] expectedIds =
        {
            PacketId.OnUpdateUserIconInf,
            PacketId.OnUpdateUserAccountClassInf,
            PacketId.OnUpdateUserPropertyInf,
            PacketId.OnUpdateUserInventoryDefaultItemInf,
            PacketId.OnUpdateUserInventoryEventItemInf,
            PacketId.OnUpdateUserInventoryShopItemInf,
            PacketId.OnUpdateUserInventoryMountItemInf
        };
        if (!packets.Select(packet => packet.Id).SequenceEqual(expectedIds))
        {
            throw new InvalidOperationException("Local player update packet order is invalid.");
        }
        foreach (Packet packet in packets)
        {
            ValidateFraming(packet);
        }

        UserIconUpdate icon = OnUpdateUserIconInfPacket.Parse(packets[0]);
        UserAccountClassUpdate accountClass =
            OnUpdateUserAccountClassInfPacket.Parse(packets[1]);
        UserPropertyUpdate properties = OnUpdateUserPropertyInfPacket.Parse(packets[2]);
        (uint defaultUserId, IReadOnlyList<uint> defaultItems) =
            OnUpdateUserInventoryDefaultItemInfPacket.Parse(packets[3]);
        (uint eventUserId, IReadOnlyList<uint> eventItems) =
            OnUpdateUserInventoryEventItemInfPacket.Parse(packets[4]);
        (uint shopUserId, IReadOnlyList<TimedInventoryItem> shopItems) =
            OnUpdateUserInventoryShopItemInfPacket.Parse(packets[5]);
        (uint mountUserId, IReadOnlyList<TimedInventoryItem> mountItems) =
            OnUpdateUserInventoryMountItemInfPacket.Parse(packets[6]);

        if (icon != new UserIconUpdate(
                profile.UserId, profile.ProfileCode, profile.IconId!.Value, profile.AccountClass) ||
            accountClass != new UserAccountClassUpdate(profile.UserId, profile.AccountClass) ||
            properties.UserId != profile.UserId ||
            !ProgressEquals(properties.Progress, profile.Progress) ||
            new[] { defaultUserId, eventUserId, shopUserId, mountUserId }
                .Any(userId => userId != profile.UserId) ||
            !defaultItems.SequenceEqual(expectedInventory.DefaultItems) ||
            !eventItems.SequenceEqual(expectedInventory.EventItems) ||
            !shopItems.SequenceEqual(expectedInventory.ShopItems) ||
            !mountItems.SequenceEqual(expectedInventory.MountItems))
        {
            throw new InvalidOperationException("Local player update packets lost editable data.");
        }

        Packet level = OnUpdateUserPropertyLevelInfPacket.Build(
            profile.UserId, profile.Progress.Experience, profile.Progress.Level, 0x1111);
        Packet money = OnUpdateUserPropertyMoneyInfPacket.Build(
            profile.UserId, profile.Progress.Money, 0x2222);
        Packet record = OnUpdateUserPropertyRecordInfPacket.Build(
            profile.UserId,
            profile.Progress.Wins,
            profile.Progress.Losses,
            profile.Progress.Draws,
            0x3333);
        Packet misc = OnUpdateUserPropertyMiscInfPacket.Build(
            profile.UserId, profile.Progress.MiscStatistics, 0x4444);
        foreach (Packet packet in new[] { level, money, record, misc })
        {
            ValidateFraming(packet);
        }

        UserLevelUpdate levelValue = OnUpdateUserPropertyLevelInfPacket.Parse(level);
        UserMoneyUpdate moneyValue = OnUpdateUserPropertyMoneyInfPacket.Parse(money);
        UserRecordUpdate recordValue = OnUpdateUserPropertyRecordInfPacket.Parse(record);
        UserMiscUpdate miscValue = OnUpdateUserPropertyMiscInfPacket.Parse(misc);
        if (levelValue != new UserLevelUpdate(
                profile.UserId, 0x1111, profile.Progress.Experience, profile.Progress.Level) ||
            moneyValue != new UserMoneyUpdate(profile.UserId, 0x2222, profile.Progress.Money) ||
            recordValue != new UserRecordUpdate(
                profile.UserId,
                0x3333,
                profile.Progress.Wins,
                profile.Progress.Losses,
                profile.Progress.Draws) ||
            miscValue.UserId != profile.UserId || miscValue.UpdateCode != 0x4444 ||
            !miscValue.MiscStatistics.SequenceEqual(profile.Progress.MiscStatistics))
        {
            throw new InvalidOperationException("Specialized player update packets lost data.");
        }
    }

    private static bool ProgressEquals(LocalPlayerProgress left, LocalPlayerProgress right) =>
        left.Level == right.Level &&
        left.Experience == right.Experience &&
        left.Money == right.Money &&
        left.Wins == right.Wins &&
        left.Losses == right.Losses &&
        left.Draws == right.Draws &&
        left.MiscStatistics.SequenceEqual(right.MiscStatistics);

    private static void ValidateLocalLobbyServices(LocalPlayerProfile profile)
    {
        LobbyUserIdentity identity = LobbyUserIdentity.CreateLocal(profile);
        Packet identityAck = OnUserIdInfoAckPacket.Build(new[] { identity });
        Packet waiterPacket = OnWaiterInfoUpdateInfPacket.Build(
            LobbyWaiterInfo.CreateLocal(profile));
        Packet erasePacket = OnWaiterInfoEraseInfPacket.Build(
            unchecked((ushort)profile.UserId));
        foreach (Packet packet in new[] { identityAck, waiterPacket, erasePacket })
        {
            ValidateFraming(packet);
        }
        if (OnUserIdInfoAckPacket.Parse(identityAck).Single() != identity ||
            OnWaiterInfoUpdateInfPacket.Parse(waiterPacket) !=
                LobbyWaiterInfo.CreateLocal(profile) ||
            OnWaiterInfoEraseInfPacket.Parse(erasePacket) !=
                unchecked((ushort)profile.UserId))
        {
            throw new InvalidOperationException("Lobby user-list packets lost local identity data.");
        }

        byte[] chatText = "hello"u8.ToArray();
        int chatWireSize = DjMaxPacketBuilder.PacketIdSize +
                           DjMaxPacketBuilder.HeaderSize + chatText.Length;
        Packet chatRequest = DjMaxPacketBuilder
            .Dynamic(PacketMeta.ChatInf, chatWireSize, ProtocolPadding.Unused)
            .WriteBytes(chatText)
            .Build();
        if (!ChatInfPacket.Parse(chatRequest).EncodedText.SequenceEqual(chatText))
        {
            throw new InvalidOperationException("Client lobby chat did not round-trip.");
        }

        // This executable registers 0x12D as an ignored eight-byte receive. It does
        // not register or dispatch 0x12E/0x130. Do not manufacture a shop handshake
        // from handlers belonging to another client build.
        if (!ReceivePacketCatalog.TryGetRegistration(
                PacketId.OnUbsAccountAuthenResAck, out ReceivePacketRegistration ubs) ||
            ubs.WireSize != 8 ||
            ReceivePacketCatalog.TryGet(PacketId.OnUbsAccountAuthenResAck, out _) ||
            ReceivePacketCatalog.TryGetRegistration(PacketId.OnUbsAwardInfoInf, out _) ||
            ReceivePacketCatalog.TryGet(PacketId.OnUbsAwardInfoInf, out _) ||
            ReceivePacketCatalog.TryGetRegistration(PacketId.OnUbsAwardAuthenAck, out _) ||
            ReceivePacketCatalog.TryGet(PacketId.OnUbsAwardAuthenAck, out _))
        {
            throw new InvalidOperationException(
                "UBS receive registrations do not match this client executable.");
        }

        byte[] roomTitle = new byte[CreateRoomReqPacket.TitleSize];
        "LOCAL ROOM"u8.CopyTo(roomTitle);
        RoomCreateRequest roomRequest = new(
            roomTitle,
            RoomType: 0,
            Capacity: LocalRoomProtocol.MaximumSlots,
            LevelRestriction: 0,
            GameType: 1,
            KeyMode: 0,
            MatchMode: 0,
            EffectorFlag: 0,
            PublicFlag: 1,
            new byte[CreateRoomReqPacket.PasswordSize]);
        RoomCreateRequest parsedRoomRequest = CreateRoomReqPacket.Parse(
            CreateRoomReqPacket.Build(roomRequest));
        if (!parsedRoomRequest.TitleField.SequenceEqual(roomTitle) ||
            parsedRoomRequest.Capacity != LocalRoomProtocol.MaximumSlots)
        {
            throw new InvalidOperationException("Create-room request lost its room settings.");
        }

        RoomMemberInfo localMember = RoomMemberInfo.CreateLocal(
            profile,
            slot: 0,
            connectionId: 0x014D,
            team: LocalRoomProtocol.HostDefaultTeam,
            isHost: true);
        Packet createAck = OnCreateRoomAckPacket.Build(new CreateRoomResponse(
            CreateRoomResult.Success, RoomIndex: 0, roomRequest));
        Packet roomDesc = OnRoomDescInfPacket.Build(RoomDescriptor.FromCreate(roomRequest));
        Packet roomList = OnRoomInfoInfPacket.Build(
            RoomListEntry.Create(0, roomRequest, memberCount: 1));
        Packet joiner = OnUpdateJoinerInfoInfPacket.Build(localMember);
        Packet postJoin = OnPostJoinRoomInfPacket.Build();
        Packet inviteRejectAck = OnInviteRejectAckPacket.Build(refused: false);
        foreach (Packet roomPacket in new[]
                 {
                     createAck, roomDesc, roomList, joiner, postJoin, inviteRejectAck,
                     OnJoinRoomAckPacket.Build(new JoinRoomResponse(
                         0, JoinRoomResult.Success, Slot: 1)),
                     OnReadyInfPacket.Build(new RoomReadyUpdate(
                         true, localMember.ConnectionId, profile.UserId)),
                     OnTeamControlInfPacket.Build(new RoomTeamUpdate(
                         0, LocalRoomProtocol.HostDefaultTeam)),
                     OnGameTypeInfPacket.Build(1),
                     OnSlotControlAckPacket.Build(new SlotControlResponse(3, Enabled: false)),
                     OnLeaveRoomAckPacket.Build(),
                     OnRoomInfoUpdateInfPacket.BuildRemove(0)
                 })
        {
            ValidateFraming(roomPacket);
        }
        OnPostJoinRoomInfPacket.Parse(postJoin);
        OnInviteRejectAckPacket.Parse(inviteRejectAck);
        if (OnCreateRoomAckPacket.Parse(createAck).Result != CreateRoomResult.Success ||
            OnRoomDescInfPacket.Parse(roomDesc).GameType != roomRequest.GameType ||
            OnRoomInfoInfPacket.Parse(roomList).MemberCount != 1 ||
            OnUpdateJoinerInfoInfPacket.Parse(joiner).UserId != profile.UserId ||
            OnUpdateJoinerInfoInfPacket.Parse(joiner).ConnectionId !=
            localMember.ConnectionId)
        {
            throw new InvalidOperationException("Room-entry responses lost local room state.");
        }

        TimedInventoryItem[] requested =
        {
            new(0x00010401, 0),
            TimedInventoryItem.Empty,
            TimedInventoryItem.Empty,
            TimedInventoryItem.Empty
        };
        DjMaxPacketBuilder purchaseBuilder = DjMaxPacketBuilder.Fixed(
            PacketMeta.PurchaseItemReq, ProtocolPadding.Unused);
        foreach (TimedInventoryItem item in requested)
        {
            purchaseBuilder.WriteUInt32(item.ItemId).WriteUInt32(item.Expiration);
        }
        PurchaseItemRequest purchase = PurchaseItemReqPacket.Parse(purchaseBuilder.Build());
        string shopData = ShopCatalog.FindDataDirectory() ??
                          throw new DirectoryNotFoundException(
                              "Shop DATA is required for local protocol verification.");
        LocalPlayerStore store = new(profile, ShopCatalog.Load(shopData));
        PurchaseItemResponse purchased = store.Purchase(purchase);
        Packet purchaseAck = OnPurchaseItemAckPacket.Build(purchased);
        ValidateFraming(purchaseAck);
        if (purchased.Money != profile.Progress.Money ||
            !purchased.ItemBoxIds.Any(id => id == 0x00010401))
        {
            throw new InvalidOperationException("Shop purchase response lost balance or item-box data.");
        }

        Packet resaleRequest = DjMaxPacketBuilder
            .Fixed(PacketMeta.ResaleItemReq, ProtocolPadding.Unused)
            .WriteUInt32(0x00010401)
            .WriteUInt32(0)
            .Build();
        ResaleItemResponse resold = store.Resale(ResaleItemReqPacket.Parse(resaleRequest));
        Packet resaleAck = OnResaleItemAckPacket.Build(resold);
        ValidateFraming(resaleAck);
        if (resold.ItemBoxIds.Any(id => id == 0x00010401))
        {
            throw new InvalidOperationException("Shop resale did not remove the item-box entry.");
        }
    }

    private static void ValidateLocalGameChannelFlow(
        LocalPlayerProfile profile,
        ChannelInfo channel,
        IReadOnlyList<PacketId> expectedOrder)
    {
        byte[] connectionSeed = Enumerable.Range(0, OnConnectAckPacket.SeedSize)
            .Select(value => unchecked((byte)(value + channel.Port)))
            .ToArray();
        PacketFactory server = new();
        PacketFactory client = new();

        byte[] connectWire = server.Write(
            OnConnectAckPacket.Build(connectionSeed, assignedUserId: 0x014D));
        client.FillReadBuffer(connectWire);
        Packet connect = client.ReadPacket() ??
                         throw new InvalidOperationException("ConnectAck did not frame.");
        (_, _, byte[] parsedSeed) = OnConnectAckPacket.Parse(connect);
        if (!parsedSeed.AsSpan().SequenceEqual(connectionSeed))
        {
            throw new InvalidOperationException("ConnectAck seed did not round-trip.");
        }

        server.InitCrypto(new DjMaxCrypto(
            connectionSeed, DjMaxCrypto.DeriveSumSeed(connectionSeed)));
        client.InitCrypto(new DjMaxCrypto(
            connectionSeed, DjMaxCrypto.DeriveSumSeed(connectionSeed)));

        // LogInReq round-trip verification removed: LogInReqPacket was rewritten for
        // the Korean client (43-byte, 30-byte seed, no ClientToken / no Build path).
        // This verifier targets the old China capture format.

        IReadOnlyList<Packet> bootstrap = LocalLobbyBootstrap.Build(
            profile, channel, connectionSeed, new Setting().DownloadUrl);
        if (!bootstrap.Select(packet => packet.Id).SequenceEqual(expectedOrder))
        {
            throw new InvalidOperationException("Local lobby bootstrap order is invalid.");
        }

        foreach (Packet packet in bootstrap)
        {
            client.FillReadBuffer(server.Write(packet));
        }
        List<Packet> decoded = client.ReadPackets();
        if (!decoded.Select(packet => packet.Id).SequenceEqual(expectedOrder) ||
            OnUserIdInfoInfPacket.Parse(decoded[0]).Count != 1 ||
            OnEnvironmentInfPacket.Parse(decoded[1]) !=
                OnEnvironmentInfPacket.DifficultyMixFilter ||
            OnEnvironmentInfPacket.Parse(decoded[2]) !=
                OnEnvironmentInfPacket.DownloadUrl(new Setting().DownloadUrl) ||
            OnUserInfoInfPacket.Parse(decoded[3]) != UserInfoSnapshot.CreateLocal(profile) ||
            OnUpdateUserPropertyInfPacket.Parse(decoded[4]).UserId != profile.UserId ||
            !InventoryEquals(
                OnInventoryInfoInfPacket.Parse(decoded[5]),
                profile.Inventory.CreateSnapshot()) ||
            OnUpdateUserAccountClassInfPacket.Parse(decoded[9]) !=
                new UserAccountClassUpdate(profile.UserId, profile.AccountClass) ||
            OnMessengerInfoInfPacket.Parse(decoded[11]).Contacts.Any(contact => contact.UserId != 0) ||
            OnLogInAckPacket.Parse(decoded[12]).SessionSeed.AsSpan(24, sizeof(uint))
                .SequenceEqual(connectionSeed.AsSpan(24, sizeof(uint))) ||
            !OnChatInfPacket.Parse(decoded[14]).AsciiText.Contains(
                channel.FullName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Local lobby bootstrap contents are invalid for port {channel.Port}.");
        }
    }

    private static bool InventoryEquals(InventorySnapshot left, InventorySnapshot right) =>
        left.State == right.State &&
        left.DefaultItems.SequenceEqual(right.DefaultItems) &&
        left.EventItems.SequenceEqual(right.EventItems) &&
        left.ShopItems.SequenceEqual(right.ShopItems) &&
        left.PresentItems.SequenceEqual(right.PresentItems) &&
        left.MountItems.SequenceEqual(right.MountItems);

    private static (int Packets, int TypedRoundTrips) VerifyFile(
        string capturePath,
        ISet<PacketId> ids,
        ISet<PacketId> receiveIds)
    {
        PacketReader captureReader = new();
        List<PacketReader.PcapPacket> chunks = captureReader.ReadYamlPcap(
            File.ReadAllText(capturePath));
        PacketFactory server = new();
        PacketFactory client = new();
        byte[]? negotiatedSeed = null;
        int packetCount = 0;
        int typedRoundTrips = 0;

        foreach (PacketReader.PcapPacket chunk in chunks)
        {
            PacketFactory factory = chunk.Source == PacketSource.Server ? server : client;
            if (chunk.Source == PacketSource.Server && chunk.Data.Length >= 2 &&
                chunk.Data[0] == 0x0A && chunk.Data[1] == 0x00)
            {
                chunk.Data[0] = 0x09;
            }

            factory.FillReadBuffer(chunk.Data);
            while (factory.ReadPacket() is { } packet)
            {
                if (packet.Id == PacketId.OnConnectAck)
                {
                    (_, _, negotiatedSeed) = OnConnectAckPacket.Parse(packet);
                    DjMaxCrypto crypto = DjMaxCrypto.FromOnConnectAckPacket(packet);
                    server.InitCrypto(crypto);
                    client.InitCrypto(DjMaxCrypto.FromOnConnectAckPacket(packet));
                }

                ValidateNegotiatedSeed(packet, negotiatedSeed);

                if (packet.Meta.Source != chunk.Source)
                {
                    throw new InvalidDataException(
                        $"{Path.GetFileName(capturePath)}: {packet.Id} is marked " +
                        $"{packet.Meta.Source}, observed {chunk.Source}.");
                }

                ValidateFraming(packet);
                typedRoundTrips += ValidateTypedRoundTrip(packet);
                ids.Add(packet.Id);
                if (chunk.Source == PacketSource.Server &&
                    ReceivePacketCatalog.TryGet(packet.Id, out _) &&
                    ReceivePacketCatalog.TryGetRegistration(packet.Id, out _))
                {
                    receiveIds.Add(packet.Id);
                }
                packetCount++;
            }
        }

        if (server.HasPendingPacket || client.HasPendingPacket)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(capturePath)} ended with " +
                $"{server.BufferedByteCount} server and {client.BufferedByteCount} client bytes pending.");
        }

        return (packetCount, typedRoundTrips);
    }

    private static void ValidateNegotiatedSeed(Packet packet, byte[]? negotiatedSeed)
    {
        if (negotiatedSeed == null)
        {
            return;
        }

        ReadOnlySpan<byte> observedSeed = packet.Id switch
        {
            PacketId.AuthenticateInSndAccReq =>
                AuthenticateInSndAccReqPacket.Parse(packet).CipherSeed,
            PacketId.LogInReq => LogInReqPacket.Parse(packet).CipherSeed,
            PacketId.OnLogInAck => OnLogInAckPacket.Parse(packet).SessionSeed,
            _ => ReadOnlySpan<byte>.Empty
        };
        if (observedSeed.IsEmpty)
        {
            return;
        }

        byte[] expectedSeed = packet.Id == PacketId.OnLogInAck
            ? OnLogInAckPacket.CreateSessionSeed(negotiatedSeed)
            : negotiatedSeed;
        if (!observedSeed.SequenceEqual(expectedSeed))
        {
            throw new InvalidDataException(
                $"{packet.Id}: seed does not match the negotiated connection seed.");
        }
    }

    private static void ValidateFraming(Packet packet)
    {
        int wireSize = DjMaxPacketBuilder.PacketIdSize + packet.Data.Length +
                       (packet.Header?.Length ?? 0);
        if (!packet.Meta.IsDynamicSize)
        {
            if (wireSize != packet.Meta.Size)
            {
                throw new InvalidDataException(
                    $"{packet.Id}: expected {packet.Meta.Size} bytes, observed {wireSize}.");
            }
            return;
        }

        if (packet.Header is not { Length: DjMaxPacketBuilder.HeaderSize })
        {
            throw new InvalidDataException($"{packet.Id}: dynamic packet has no header.");
        }

        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(packet.Header.AsSpan(1));
        if (declaredSize != wireSize)
        {
            throw new InvalidDataException(
                $"{packet.Id}: header declares {declaredSize} bytes, observed {wireSize}.");
        }
    }

    private static int ValidateTypedRoundTrip(Packet packet)
    {
        Packet? rebuilt = packet.Id switch
        {
            PacketId.OnConnectAck => RebuildConnect(packet),
            PacketId.AuthenticateInSndAccReq => RebuildAuthenticationRequest(packet),
            PacketId.OnAuthenticateInAck => RebuildAuthentication(packet),
            PacketId.OnChannelInfoInf => OnChannelInfoInfPacket.Build(
                OnChannelInfoInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnUpdateUserIconInf => RebuildIcon(packet),
            PacketId.OnUpdateUserPropertyInf => RebuildProperties(packet),
            PacketId.OnUpdateUserPropertyLevelInf => RebuildLevel(packet),
            PacketId.OnUpdateUserPropertyMoneyInf => RebuildMoney(packet),
            PacketId.OnUpdateUserPropertyRecordInf => RebuildRecord(packet),
            PacketId.OnUpdateUserPropertyMiscInf => RebuildMisc(packet),
            PacketId.OnUpdateUserInventoryDefaultItemInf => RebuildDefaultItems(packet),
            PacketId.OnUpdateUserInventoryEventItemInf => RebuildEventItems(packet),
            PacketId.OnUpdateUserInventoryShopItemInf => RebuildShopItems(packet),
            PacketId.OnUpdateUserInventoryMountItemInf => RebuildMountItems(packet),
            PacketId.OnUpdateUserAccountClassInf => RebuildAccountClass(packet),
            PacketId.OnGameStartInf => OnGameStartInfPacket.Build(
                OnGameStartInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnKeepAuthenticateInAck => RebuildKeepAuthentication(packet),
            PacketId.LogInReq => packet, // China Build path removed in the Korean rewrite
            PacketId.OnUserIdInfoInf => OnUserIdInfoInfPacket.Build(
                OnUserIdInfoInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnEnvironmentInf => OnEnvironmentInfPacket.Build(
                OnEnvironmentInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnUserInfoInf => OnUserInfoInfPacket.Build(
                OnUserInfoInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnInventoryInfoInf => OnInventoryInfoInfPacket.Build(
                OnInventoryInfoInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnMessengerInfoInf => OnMessengerInfoInfPacket.Build(
                OnMessengerInfoInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnLogInAck => OnLogInAckPacket.Build(
                OnLogInAckPacket.Parse(packet), packet.Header![0]),
            PacketId.OnChatInf => OnChatInfPacket.Build(
                OnChatInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnRoomInfoInf => OnRoomInfoInfPacket.Build(
                OnRoomInfoInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnRoomInfoUpdateInf => OnRoomInfoUpdateInfPacket.BuildRemove(
                OnRoomInfoUpdateInfPacket.ParseRemove(packet), packet.Header![0]),
            PacketId.OnRoomDescInf => OnRoomDescInfPacket.Build(
                OnRoomDescInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnCreateRoomAck => OnCreateRoomAckPacket.Build(
                OnCreateRoomAckPacket.Parse(packet), packet.Header![0]),
            PacketId.OnUpdateJoinerInfoInf => OnUpdateJoinerInfoInfPacket.Build(
                OnUpdateJoinerInfoInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnPostJoinRoomInf => RebuildPostJoinRoom(packet),
            PacketId.OnJoinRoomAck => OnJoinRoomAckPacket.Build(
                OnJoinRoomAckPacket.Parse(packet), packet.Header![0]),
            PacketId.OnReadyInf => OnReadyInfPacket.Build(
                OnReadyInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnTeamControlInf => OnTeamControlInfPacket.Build(
                OnTeamControlInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnGameTypeInf => OnGameTypeInfPacket.Build(
                OnGameTypeInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnGameInfoInf => OnGameInfoInfPacket.Build(
                OnGameInfoInfPacket.Parse(packet), packet.Header![0]),
            PacketId.OnLeaveRoomAck => OnLeaveRoomAckPacket.Build(
                OnLeaveRoomAckPacket.Parse(packet), packet.Header![0]),
            PacketId.OnRoomChangeInfoAck => RebuildRoomChange(packet),
            PacketId.OnSlotControlAck => OnSlotControlAckPacket.Build(
                OnSlotControlAckPacket.Parse(packet), packet.Header![0]),
            PacketId.OnInviteRejectAck => RebuildInviteRejectAck(packet),
            _ => null
        };

        if (rebuilt == null)
        {
            return 0;
        }

        if (!packet.Data.AsSpan().SequenceEqual(rebuilt.Data) ||
            !packet.Header!.AsSpan().SequenceEqual(rebuilt.Header))
        {
            byte[] expected = packet.Header.Concat(packet.Data).ToArray();
            byte[] actual = rebuilt.Header!.Concat(rebuilt.Data).ToArray();
            int mismatch = Enumerable.Range(0, Math.Min(expected.Length, actual.Length))
                .FirstOrDefault(index => expected[index] != actual[index], -1);
            string detail = mismatch >= 0
                ? $" First mismatch at body offset {mismatch}: " +
                  $"capture=0x{expected[mismatch]:X2}, rebuilt=0x{actual[mismatch]:X2}."
                : $" Length mismatch: capture={expected.Length}, rebuilt={actual.Length}.";
            throw new InvalidDataException(
                $"{packet.Id}: typed codec is not byte-exact.{detail}");
        }

        return 1;
    }

    private static Packet RebuildConnect(Packet packet)
    {
        (ConnectResult result, ushort assignedUserId, byte[] seed) =
            OnConnectAckPacket.Parse(packet);
        return OnConnectAckPacket.Build(seed, assignedUserId, result, packet.Header![0]);
    }

    private static Packet RebuildPostJoinRoom(Packet packet)
    {
        OnPostJoinRoomInfPacket.Parse(packet);
        return OnPostJoinRoomInfPacket.Build(packet.Header![0]);
    }

    private static Packet RebuildInviteRejectAck(Packet packet)
    {
        bool refused = OnInviteRejectAckPacket.Parse(packet);
        return OnInviteRejectAckPacket.Build(refused, packet.Header![0]);
    }

    private static Packet RebuildRoomChange(Packet packet)
    {
        (RoomChangeRequest request, RoomChangeResult result) =
            OnRoomChangeInfoAckPacket.Parse(packet);
        return OnRoomChangeInfoAckPacket.Build(request, result, packet.Header![0]);
    }

    private static Packet RebuildAuthentication(Packet packet)
    {
        (AuthenticationIdentity identity, AuthenticationResult result) =
            OnAuthenticateInAckPacket.Parse(packet);
        return OnAuthenticateInAckPacket.Build(identity, result, packet.Header![0]);
    }

    private static Packet RebuildAuthenticationRequest(Packet packet)
    {
        AuthenticationRequest request = AuthenticateInSndAccReqPacket.Parse(packet);
        return AuthenticateInSndAccReqPacket.Build(request, packet.Header![0]);
    }

    private static Packet RebuildKeepAuthentication(Packet packet)
    {
        OnKeepAuthenticateInAckPacket.Parse(packet);
        return OnKeepAuthenticateInAckPacket.Build(packet.Header![0]);
    }

    private static Packet RebuildIcon(Packet packet)
    {
        UserIconUpdate value = OnUpdateUserIconInfPacket.Parse(packet);
        return OnUpdateUserIconInfPacket.Build(
            value.UserId, value.ProfileCode, value.IconId, packet.Header![0]);
    }

    private static Packet RebuildProperties(Packet packet)
    {
        UserPropertyUpdate value = OnUpdateUserPropertyInfPacket.Parse(packet);
        return OnUpdateUserPropertyInfPacket.Build(
            value.UserId, value.Progress, value.UpdateCode, packet.Header![0]);
    }

    private static Packet RebuildLevel(Packet packet)
    {
        UserLevelUpdate value = OnUpdateUserPropertyLevelInfPacket.Parse(packet);
        return OnUpdateUserPropertyLevelInfPacket.Build(
            value.UserId,
            value.Experience,
            value.Level,
            value.UpdateCode,
            packet.Header![0]);
    }

    private static Packet RebuildMoney(Packet packet)
    {
        UserMoneyUpdate value = OnUpdateUserPropertyMoneyInfPacket.Parse(packet);
        return OnUpdateUserPropertyMoneyInfPacket.Build(
            value.UserId, value.Money, value.UpdateCode, packet.Header![0]);
    }

    private static Packet RebuildRecord(Packet packet)
    {
        UserRecordUpdate value = OnUpdateUserPropertyRecordInfPacket.Parse(packet);
        return OnUpdateUserPropertyRecordInfPacket.Build(
            value.UserId,
            value.Wins,
            value.Losses,
            value.Draws,
            value.UpdateCode,
            packet.Header![0]);
    }

    private static Packet RebuildMisc(Packet packet)
    {
        UserMiscUpdate value = OnUpdateUserPropertyMiscInfPacket.Parse(packet);
        return OnUpdateUserPropertyMiscInfPacket.Build(
            value.UserId,
            value.MiscStatistics,
            value.UpdateCode,
            packet.Header![0]);
    }

    private static Packet RebuildDefaultItems(Packet packet)
    {
        (uint userId, IReadOnlyList<uint> items) =
            OnUpdateUserInventoryDefaultItemInfPacket.Parse(packet);
        return OnUpdateUserInventoryDefaultItemInfPacket.Build(
            userId, items, packet.Header![0]);
    }

    private static Packet RebuildEventItems(Packet packet)
    {
        (uint userId, IReadOnlyList<uint> items) =
            OnUpdateUserInventoryEventItemInfPacket.Parse(packet);
        return OnUpdateUserInventoryEventItemInfPacket.Build(
            userId, items, packet.Header![0]);
    }

    private static Packet RebuildShopItems(Packet packet)
    {
        (uint userId, IReadOnlyList<TimedInventoryItem> items) =
            OnUpdateUserInventoryShopItemInfPacket.Parse(packet);
        return OnUpdateUserInventoryShopItemInfPacket.Build(
            userId, items, packet.Header![0]);
    }

    private static Packet RebuildMountItems(Packet packet)
    {
        (uint userId, IReadOnlyList<TimedInventoryItem> items) =
            OnUpdateUserInventoryMountItemInfPacket.Parse(packet);
        return OnUpdateUserInventoryMountItemInfPacket.Build(
            userId, items, packet.Header![0]);
    }

    private static Packet RebuildAccountClass(Packet packet)
    {
        UserAccountClassUpdate update = OnUpdateUserAccountClassInfPacket.Parse(packet);
        return OnUpdateUserAccountClassInfPacket.Build(
            update.UserId, update.AccountClass, packet.Header![0]);
    }
}
