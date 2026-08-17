using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

public enum LoginResult : ushort
{
    Accepted = 41,
    ExcessPeers = 47
}

/// <summary>
/// Three signed 16-bit values stored by sub_432CD0. Their downstream gameplay
/// meanings have not yet been proven, so neutral names avoid false semantics.
/// </summary>
public sealed record LoginLobbyParameters(short Value1, short Value2, short Value3)
{
    // The club-lobby client stores these at net+793816/+793820/+793824.  In
    // particular, Value3 is the number of EMPTY STAGE cells it allocates.  A
    // zero here leaves the room-list screen completely blank even though room
    // update packets arrive correctly.
    public const short DefaultValue1 = 6;
    public const short DefaultValue2 = 6;
    public const short DefaultRoomSlotCount = 200;

    public static LoginLobbyParameters LocalDefaults { get; } = new(
        DefaultValue1,
        DefaultValue2,
        DefaultRoomSlotCount);
}

public sealed record LoginAcknowledgement(
    LoginResult Result,
    byte[] SessionSeed,
    LoginLobbyParameters LobbyParameters);

public static class OnLogInAckPacket
{
    private const int LeadingReservedSize = 4;
    private const int SessionSeedTagOffset = 24;

    // All seven game-channel captures use this tag in the phase seed at bytes 24..27.
    public const uint SessionSeedTag = 0x1C1E0212;

    public static byte[] CreateSessionSeed(ReadOnlySpan<byte> connectionSeed)
    {
        ValidateSeed(connectionSeed, nameof(connectionSeed));
        byte[] sessionSeed = connectionSeed.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            sessionSeed.AsSpan(SessionSeedTagOffset), SessionSeedTag);
        return sessionSeed;
    }

    public static Packet BuildFromConnectionSeed(
        ReadOnlySpan<byte> connectionSeed,
        LoginLobbyParameters? lobbyParameters = null,
        byte control = ProtocolPadding.Unused)
    {
        return Build(new LoginAcknowledgement(
            LoginResult.Accepted,
            CreateSessionSeed(connectionSeed),
            lobbyParameters ?? LoginLobbyParameters.LocalDefaults), control);
    }

    public static Packet Build(
        LoginAcknowledgement acknowledgement,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ValidateSeed(acknowledgement.SessionSeed, nameof(acknowledgement.SessionSeed));

        byte[] transmittedSeed = acknowledgement.SessionSeed.ToArray();
        ComplementFirstTwoWords(transmittedSeed);
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnLogInAck, control)
            .WritePadding(LeadingReservedSize, ProtocolPadding.Unused)
            .WriteUInt16((ushort)acknowledgement.Result)
            .WriteBytes(transmittedSeed)
            .WriteInt16(acknowledgement.LobbyParameters.Value1)
            .WriteInt16(acknowledgement.LobbyParameters.Value2)
            .WriteInt16(acknowledgement.LobbyParameters.Value3)
            .Build();
    }

    /// <summary>
    /// Korean OnLogInAck (id 0x1A), 1874 bytes. Client sub_4314F0 gates on the
    /// result WORD@3 == 41, copies a 30-byte seed@5, three WORDs@35/37/39, a
    /// 135-byte user-info block@41 (userId u32, accountId[25], nickname[25], ...),
    /// a 748-byte block@176, and a 950-byte block@924. Inventory/records are
    /// zeroed here for a minimal lobby entry.
    /// </summary>
    /// <summary>
    /// The result word at +3 that means SUCCESS. sub_4314F0 loads the profile ONLY when
    /// it sees this; for anything else it skips the whole copy and simply stores the
    /// value at net+895300 - the field sub_44D012 reads to pick its dialog. So a login
    /// is refused by sending a different code here, NOT by sending a disconnect packet.
    /// The codes are the same ones listed on AccountLockReasons.
    /// </summary>
    public const ushort KoreanAccepted = 41;

    /// <summary>
    /// Bytes of cipher seed. The client copies 0x20 from offset 9 and re-keys on them.
    /// </summary>
    public const int SeedSize = 32;

    /// <summary>
    /// The JP login ack, which is NOT Korea's packet under another name.
    ///
    /// Korea answers LogInReq with 1874 bytes carrying the whole profile. This client's
    /// handler sub_431ED0 reads 47 bytes and does something quite different:
    ///
    ///     +3   4 bytes    (not read by sub_431ED0)
    ///     +7   u16 result -> must be 41; 47 means "Excesspeer-&gt;Reconnect"
    ///     +9   32 bytes   -> a NEW cipher seed, first two dwords inverted on the wire
    ///     +41  i16, +43 i16, +45 i16
    ///
    /// It RE-KEYS THE CONNECTION. Everything after this ack is enciphered with the new
    /// seed, so the caller must switch the server's cipher immediately after sending it -
    /// send first, so the ack itself still goes out under the old key.
    ///
    /// The profile Korea packs in here must therefore arrive some other way; ids 67/68/69
    /// (138/751/953 bytes) are the likely carriers.
    /// </summary>
    public static Packet BuildJapanese(
        ReadOnlySpan<byte> cipherSeed,
        ushort result = KoreanAccepted,
        short valueA = LoginLobbyParameters.DefaultValue1,
        short valueB = LoginLobbyParameters.DefaultValue2,
        short valueC = LoginLobbyParameters.DefaultRoomSlotCount,
        byte control = ProtocolPadding.Unused)
    {
        if (cipherSeed.Length != SeedSize)
        {
            throw new ArgumentException(
                $"Cipher seed must be {SeedSize} bytes.", nameof(cipherSeed));
        }

        // Same inversion the connect ack uses: the client NOTs these two dwords back on
        // arrival, so the plain seed is what both sides end up keyed on.
        byte[] transmitted = cipherSeed.ToArray();
        uint first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(transmitted);
        uint second = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            transmitted.AsSpan(sizeof(uint)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(transmitted, ~first);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            transmitted.AsSpan(sizeof(uint)), ~second);

        return DjMaxPacketBuilder.Fixed(PacketMeta.OnLogInAck, control)
            .WriteUInt32(ProtocolPadding.UnusedUInt32)
            .WriteUInt16(result)
            .WriteBytes(transmitted)
            .WriteInt16(valueA)
            .WriteInt16(valueB)
            .WriteInt16(valueC)
            .Build();
    }

    public static Packet BuildKorean(
        ReadOnlySpan<byte> seed30,
        uint userId,
        string accountId,
        string nickname,
        uint accountClass,
        uint iconWireValue,
        LocalPlayerProgress progress,
        IReadOnlyList<CollectionEntry> collection,
        IReadOnlyList<TimedInventoryItem> itemBoxItems,
        byte[] mountLoadout,
        byte gender = 0,
        IReadOnlyList<PresentInventoryItem>? presentItems = null,
        ushort result = KoreanAccepted,
        byte control = ProtocolPadding.Unused)
    {
        if (seed30.Length != 30)
        {
            throw new ArgumentException(
                "Korean session seed must be 30 bytes.", nameof(seed30));
        }

        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(itemBoxItems);
        if (itemBoxItems.Count > OnPurchaseItemAckPacket.ItemBoxSlots)
        {
            throw new ArgumentException(
                $"Item box cannot exceed {OnPurchaseItemAckPacket.ItemBoxSlots} entries.",
                nameof(itemBoxItems));
        }
        ArgumentNullException.ThrowIfNull(mountLoadout);
        if (mountLoadout.Length != 64)
        {
            throw new ArgumentException("Mount loadout must be 64 bytes.", nameof(mountLoadout));
        }
        // Accuracy is stored pre-scaled: the client divides BOTH slots by 100 when
        // displaying a percent. Sending the highest one x10 made the SCORE tab show a
        // 98.7% best as "9.87%".
        uint highestAccuracy = (uint)Math.Round(progress.HighestAccuracy * 100);
        uint averageAccuracy = (uint)Math.Round(progress.AverageAccuracy * 100);

        // The 135-byte user block @41 is bulk-copied to client this+794096 by
        // sub_4314F0. The auth handler sub_431020 and the profile-panel render
        // sub_45F4BC pin the exact storage offsets:
        //   +0   userId       (auth a2+3   -> this+794096)
        //   +4   accountId[25](auth a2+17  -> this+794100)
        //   +29  nickname[25] (auth a2+42  -> this+794125)
        //   +54  gender byte  (render reads this+794150 as "lobby info gender")
        //   +57  icon         (render draws MyIcon from this+794153; login handler
        //                      decrements it once, so we send iconId+1 = IconWireValue)
        //   +65  level        (auth a2+11  -> this+794161)
        //   +69  experience   (render draws exp bar from this+794165 vs table[level])
        //   +129 accountClass (auth a2+7   -> this+794225)
        const int CollectionSlots = 48;       // 48 medal slots (192 bytes) at block @176
        const int CollectionBlockSize = 748;
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(PacketMeta.OnLogInAck, control)
            .WriteUInt16(result)              // result gate @3
            .WriteBytes(seed30)               // session seed @5 (30 bytes)
            // Three lobby params sub_4314F0 stores at this+793808/812/816. The third
            // (@39) is the ROOM COUNT: the club-lobby scene allocates and draws that
            // many "EMPTY STAGE" slots (sub_4471E6 alloc + sub_444A9A hit-test bound).
            // Writing 0 here = blank room grid. China used (6, 6, 200).
            .WriteUInt16(6)                   // @35 lobby param 1
            .WriteUInt16(6)                   // @37 lobby param 2
            .WriteUInt16(200)                 // @39 room-grid slot count
            .WriteUInt32(userId)              // block+0
            .WriteFixedAscii(accountId, 25)   // block+4
            .WriteFixedAscii(nickname, 25)    // block+29
            .WriteByte(gender)                // block+54 (gender)
            .WritePadding(57 - 55)            // block+55..56
            .WriteUInt32(iconWireValue)       // block+57 icon (client decrements)
            .WritePadding(65 - 61)            // block+61..64
            .WriteUInt32(progress.Level)      // block+65
            .WriteUInt32(progress.Experience) // block+69
            // SCORE-tab stats — offsets pinned by marker-validation against the
            // 135-block written by sub_435AE0. The three unknown slots (+81/+117/
            // +125) are zeroed until identified.
            .WriteUInt32(progress.Money)              // block+73  max coins
            .WriteUInt32(progress.Wins)               // block+77  wins
            .WriteUInt32(0)                           // block+81  (unknown)
            .WriteUInt32(progress.Losses)             // block+85  losses
            // block+89..+128 - shared with OnUpdateUserPropertyMiscInf, which overwrites
            // exactly this span, so the layout lives in one place.
            .WriteUInt32Array(UserStatisticsBlock.Build(progress))
            .WriteUInt32(accountClass)        // block+129
            .WritePadding(135 - 133);         // block+133..134

        // 748-byte PRIZE/COLLECTION block @176: 48 medal slots of {code:u16, value:u16}.
        // Fill supplied entries, pad the rest with the 0xFFFF empty sentinel so the
        // client's mycollect loop (sub_465AD1) skips them instead of rendering medal 0.
        for (int i = 0; i < CollectionSlots; i++)
        {
            if (i < collection.Count)
            {
                builder.WriteUInt16(collection[i].Code).WriteUInt16(collection[i].Value);
            }
            else
            {
                builder.WriteUInt16(0xFFFF).WriteUInt16(0);
            }
        }

        // Collection disc section (block+192, 32 × 4-byte) — 0xFFFF empty sentinel so
        // the disc loops (sub_465AD1 / sub_47448C) treat it as empty.
        for (int i = 0; i < 32; i++)
        {
            builder.WriteUInt16(0xFFFF).WriteUInt16(0);
        }
        // Item box (block+320 = net+893035): 30 × 8-byte {itemId:u32, value:u32}. The
        // equipment scene sub_47448C reads it and treats a low-word 0xFFFF as empty.
        const int ItemSlots = 30;
        for (int i = 0; i < ItemSlots; i++)
        {
            if (i < itemBoxItems.Count)
            {
                builder
                    .WriteUInt32(itemBoxItems[i].ItemId)
                    .WriteUInt32(itemBoxItems[i].Expiration);
            }
            else
            {
                builder.WriteUInt32(0xFFFFFFFF).WriteUInt32(0xFFFFFFFF);
            }
        }

        // Present box (block+560 = net+893275): 10 × 12-byte {itemId, value, senderUserId}.
        // sub_472FA5 draws a row per entry and resolves the SENDER at +8 through the user
        // map to print "from <nick>". Unused slots stay all-zero rather than 0xFFFF-filled:
        // zero is the state the client has always been shipped and it renders as an empty
        // box, and sub_4728D4's answer for 0xFFFF is not established.
        const int PresentSlots = OnInventoryInfoInfPacket.PresentItemCount;
        const int PresentSlotSize = 12;
        int writtenPresents = Math.Min(presentItems?.Count ?? 0, PresentSlots);
        for (int i = 0; i < writtenPresents; i++)
        {
            PresentInventoryItem present = presentItems![i];
            builder
                .WriteUInt32(present.ItemId)
                .WriteUInt32(present.Expiration)
                .WriteUInt32(present.SenderUserId);
        }

        // After the present box we're at block+680, where the equipped mount loadout lives
        // (net+893395) — 64 bytes; sending it restores equipped gear on login. sub_437430
        // copies this same region from the OnMountItemAck.
        return builder
            .WritePadding(680 - 560 - writtenPresents * PresentSlotSize)
            .WriteBytes(mountLoadout)         // block+680..+743 equipped mount loadout
            .WritePadding(CollectionBlockSize - 744) // block+744..+747
            .WritePadding(950)                // block @924 (friend list) — zeroed = no friends
            .Build();
    }

    public static LoginAcknowledgement Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        reader.Skip(LeadingReservedSize);
        LoginResult result = (LoginResult)reader.ReadUInt16();
        byte[] sessionSeed = reader.ReadBytes(OnConnectAckPacket.SeedSize);
        ComplementFirstTwoWords(sessionSeed);
        LoginLobbyParameters lobbyParameters = new(
            reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
        reader.EnsureComplete();
        return new LoginAcknowledgement(result, sessionSeed, lobbyParameters);
    }

    private static void ComplementFirstTwoWords(Span<byte> seed)
    {
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(seed);
        uint second = BinaryPrimitives.ReadUInt32LittleEndian(seed[sizeof(uint)..]);
        BinaryPrimitives.WriteUInt32LittleEndian(seed, ~first);
        BinaryPrimitives.WriteUInt32LittleEndian(seed[sizeof(uint)..], ~second);
    }

    private static void ValidateSeed(ReadOnlySpan<byte> seed, string parameterName)
    {
        if (seed.Length != OnConnectAckPacket.SeedSize)
        {
            throw new ArgumentException(
                $"Cipher seed must be {OnConnectAckPacket.SeedSize} bytes.",
                parameterName);
        }
    }
}
