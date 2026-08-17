using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

public static class GameplayProtocol
{
    public const int ReservedSize = 8;
    public const int EffectorConfigurationSize = 24;
    public const int MountSnapshotSize = 64;
    public const int PlayStateSize = 11;
    public const int GameInfoHeaderSize = 132;
    public const int GameInfoDiscIdOffset = 4;

    internal static byte[] ReadReserved(DjMaxPacketReader reader) =>
        reader.ReadBytes(ReservedSize);

    internal static byte[] Reserved(byte value)
    {
        byte[] bytes = new byte[ReservedSize];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    internal static void RequireLength(byte[] value, int expected, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length != expected)
        {
            throw new ArgumentException(
                $"{name} must contain exactly {expected} bytes.", name);
        }
    }
}

public sealed record GameInfoPayload(uint DiscId, ushort ChartType, byte[] Bytes)
{
    /// <summary>
    /// Course-mode flag, at payload+10. Must be 1 for the course to run to completion.
    /// </summary>
    public const int CourseModeOffset = 10;

    /// <summary>Course id, at payload+12; see <see cref="WithCourseStage"/>.</summary>
    public const int CourseIdOffset = 12;

    /// <summary>Zero-based course stage, at payload+14.</summary>
    public const int CourseStageOffset = 14;

    /// <summary>The value payload+10 must carry while a course is being played.</summary>
    public const ushort CourseModeActive = 1;

    /// <summary>
    /// Stamps the course id and current stage into the game-info header.
    ///
    /// The client's OnGameInfoInf handler sub_4357F0 copies this payload from wire offset
    /// 7 straight into net+794279, so payload+12 lands on net+794291 and payload+14 on
    /// net+794293. Those two are what the Course Club reads back: the stage-result scene
    /// sub_4932D7 indexes the course's own Songname/Songdiff arrays with the stage index,
    /// and the stage-result director compares it against the stage count to decide between
    /// another stage and the total result. Nothing in the client ever writes them - they
    /// only arrive here - so leaving them zero makes every stage render stage one's chart
    /// and never finish the course.
    ///
    /// payload+10 is a third field and the one that ends a course. The stage-result
    /// director sub_4074C0 only takes its total-result branch when ALL of
    ///   net+794289 == 1, stageCount - 1 == net+794293, lastSongId == currentSongId
    /// hold; the first test fails first, so with payload+10 left at zero the director
    /// always chose "play another stage" - the course looped past its last chart instead
    /// of finishing. sub_40CC70 gates the same flag. Nothing in the client writes it.
    ///
    /// All three fields sit in the plaintext part of the header: outside the scrambled
    /// config block (16..127) and outside the descramble key inputs (4..9), so writing
    /// them cannot disturb the chart.
    /// </summary>
    public GameInfoPayload WithCourseStage(ushort courseId, ushort stageIndex)
    {
        byte[] bytes = Bytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(CourseModeOffset), CourseModeActive);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(CourseIdOffset), courseId);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(CourseStageOffset), stageIndex);
        return this with { Bytes = bytes };
    }

    /// <summary>
    /// payload+0 reaches <c>net+794279</c> (the handler copies this payload from wire+7),
    /// and it is the <b>GAME MODE</b>, not a result-screen switch. TESTED: writing 1 put
    /// the client into <b>DJ MISSION MATCH</b> (survival) - which is also why the branch at
    /// <c>sub_44F48D</c> 0x4516ef then chose the "mission clear" component. Its other
    /// readers agree it is a mode: <c>sub_404CA0</c> (asset root), <c>sub_424AA0</c>
    /// (item-battle gate), <c>sub_43FD0F</c>.
    ///
    /// The 1st..6th placement layout is NOT selected here. Leave this byte alone.
    /// </summary>
    public const int GameModeOffset = 0;

    /// <summary>Mode value that starts DJ Mission Match. Do not send it for a battle.</summary>
    public const byte MissionMatchMode = 1;

    /// <summary>
    /// Stamps the game-mode byte. Only ever called when the host actually chose the RANDOM
    /// disc slot, which is the client's own way into DJ Mission Match - sending it for an
    /// ordinary song puts the room into survival and makes the result screen render the
    /// "mission clear" component instead of the normal one.
    /// </summary>
    public GameInfoPayload WithGameMode(byte mode)
    {
        byte[] bytes = Bytes.ToArray();
        bytes[GameModeOffset] = mode;
        return this with { Bytes = bytes };
    }

    public static GameInfoPayload Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < GameplayProtocol.GameInfoHeaderSize)
        {
            throw new InvalidDataException(
                $"Game-info payload is {bytes.Length} bytes; expected at least " +
                $"{GameplayProtocol.GameInfoHeaderSize}.");
        }

        ushort chartType = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2));
        uint discId = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.Slice(GameplayProtocol.GameInfoDiscIdOffset));
        uint chartDataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(128));
        if (chartDataSize != bytes.Length - GameplayProtocol.GameInfoHeaderSize)
        {
            throw new InvalidDataException(
                $"Game-info header declares {chartDataSize} chart bytes, but " +
                $"{bytes.Length - GameplayProtocol.GameInfoHeaderSize} are present.");
        }

        return new GameInfoPayload(discId, chartType, bytes.ToArray());
    }
}

public static class OnGameInfoInfPacket
{
    public static Packet Build(
        GameInfoPayload payload,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(payload);
        GameInfoPayload validated = GameInfoPayload.Parse(payload.Bytes);
        if (validated.DiscId != payload.DiscId || validated.ChartType != payload.ChartType)
        {
            throw new ArgumentException(
                "Game-info metadata does not match its payload bytes.", nameof(payload));
        }

        int wireSize = DjMaxPacketBuilder.PacketIdSize +
                       DjMaxPacketBuilder.HeaderSize + payload.Bytes.Length;
        return DjMaxPacketBuilder.Dynamic(PacketMeta.OnGameInfoInf, wireSize, control)
            .WriteBytes(payload.Bytes)
            .Build();
    }

    public static GameInfoPayload Parse(Packet packet)
    {
        if (packet.Id != PacketId.OnGameInfoInf)
        {
            throw new ArgumentException("Expected OnGameInfoInf.", nameof(packet));
        }

        return GameInfoPayload.Parse(packet.Data);
    }
}

/// <param name="VideoMode">
/// The client's "video mode": it auto-plays the chart so the BGA can be watched. Proven by
/// capture - two starts of the same disc and difficulty differed in exactly this byte, and
/// nothing else on the wire.
/// </param>
public sealed record StartRequest(byte[] Parameters, bool VideoMode = false);

public static class StartReqPacket
{
    public static StartRequest Parse(Packet packet)
    {
        // Korean StartReq is only 5 wire bytes — smaller than the 5-byte structured
        // header DjMaxPacketReader expects — so the payload is read straight off Data,
        // which holds the control byte followed by the two parameter bytes.
        // StartGame starts from the host's stored disc/difficulty; the only parameter that
        // carries meaning is the video-mode flag:
        //     normal  5F 00 E7 00 00
        //     video   5F 00 9E 01 00   <- Data[1]
        ArgumentNullException.ThrowIfNull(packet);
        bool videoMode = packet.Data.Length > 1 && packet.Data[1] != 0;
        return new StartRequest([], videoMode);
    }
}

public sealed record EffectorSelection(byte[] Configuration);

public static class UseEffectorInfPacket
{
    // Korean UseEffectorInf is 27 wire bytes: config[24]@3, no reserved tail.
    public static EffectorSelection Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        EffectorSelection selection = new(
            reader.ReadBytes(GameplayProtocol.EffectorConfigurationSize));
        reader.EnsureComplete();
        return selection;
    }
}

public static class OnUseEffectorInfPacket
{
    public static Packet Build(
        byte slot,
        ReadOnlySpan<byte> configuration,
        byte control = ProtocolPadding.Unused)
    {
        if (configuration.Length != GameplayProtocol.EffectorConfigurationSize)
        {
            throw new ArgumentException(
                $"Effector configuration must be " +
                $"{GameplayProtocol.EffectorConfigurationSize} bytes.",
                nameof(configuration));
        }

        // Korean OnUseEffectorInf is 28 wire bytes: config[24]@3 + slot byte@27 (client
        // handler sub_4367D0 reads slot@27, config@3). No China reserved tail.
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnUseEffectorInf, control)
            .WriteBytes(configuration)
            .WriteByte(slot)
            .Build();
    }
}

public sealed record JoinEvent(short EventId);

public static class OnJoinEventInfPacket
{
    // Korean OnJoinEventInf is 5 wire bytes: eventId i16@3 (client handler sub_4350C0
    // stores *(i16)@3). No reserved tail — the China 8-byte pad overflowed the packet.
    public static Packet Build(
        short eventId = 0,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnJoinEventInf, control)
            .WriteInt16(eventId)
            .Build();

    public static JoinEvent Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        JoinEvent value = new(reader.ReadInt16());
        reader.EnsureComplete();
        return value;
    }
}

/// <param name="HpBonus">
/// The player's EQUIPMENT HP BONUS, at wire+4 - NOT an item id.
///
/// <c>sub_4368E0</c> stores it at <c>net + 894916 + 4*slot</c>, and the play-start path
/// <c>sub_422250</c> reads it back through <c>sub_428A0C</c>:
/// <code>
///   sub_428A0C(a1, slot) = net[894916 + 4*slot] + dword_55CB50[a1]
///   dword_55CB50 = { 100, 100, 102, 105, 108, 110, 112, ... }   // base gauge table
///   player+0x184 = (float)that
///   player+0x180 = (OnStartParameterInf gauge / 100.0) * player+0x184
/// </code>
/// So the starting gauge is <c>baseGauge + thisValue</c>. It is the ONE number that
/// decides how much punishment a player can take, it is authored entirely by the server,
/// and it must stay a small bonus: feeding it a raw item id (0x840A = 33802) gave that
/// player a gauge of ~33900 and made them unfailable.
/// </param>
public sealed record MountItemState(ushort HpBonus, byte[] Snapshot)
{
    /// <summary>
    /// Starting-gauge bonus for a 64-byte loadout, scaled by <paramref name="percent"/>.
    /// See <see cref="EquipmentBonus"/> for the full stat map.
    /// </summary>
    public static ushort HpBonusFor(byte[] loadout, ShopCatalog? shop, int percent = 100) =>
        EquipmentBonus.For(
            EquipmentBonus.EquippedItemIds(loadout),
            shop,
            EquipmentBonusScale.Full with { HpPercent = percent }).Hp;

    public static MountItemState Empty
    {
        get
        {
            byte[] snapshot = new byte[GameplayProtocol.MountSnapshotSize];
            snapshot.AsSpan().Fill(byte.MaxValue);
            return new MountItemState(0, snapshot);
        }
    }
}

public static class OnUseMountItemInfPacket
{
    public static Packet Build(
        byte slot,
        MountItemState item,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(item);
        GameplayProtocol.RequireLength(
            item.Snapshot, GameplayProtocol.MountSnapshotSize, nameof(item.Snapshot));
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnUseMountItemInf, control)
            .WriteByte(slot)
            .WriteUInt16(item.HpBonus)
            .WriteBytes(item.Snapshot)
            .Build();
    }
}

public static class UseMountItemInfPacket
{
    public static Packet Build(
        ReadOnlySpan<byte> snapshot,
        byte control = ProtocolPadding.Unused)
    {
        if (snapshot.Length != GameplayProtocol.MountSnapshotSize)
        {
            throw new ArgumentException(
                $"Mount snapshot must be {GameplayProtocol.MountSnapshotSize} bytes.",
                nameof(snapshot));
        }
        return DjMaxPacketBuilder.Fixed(PacketMeta.UseMountItemInf, control)
            .WriteBytes(snapshot)
            .Build();
    }

    public static byte[] Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        byte[] snapshot = reader.ReadBytes(GameplayProtocol.MountSnapshotSize);
        reader.EnsureComplete();
        return snapshot;
    }
}

/// <param name="GaugeBits">Starting gauge as float bits; stored at net+894940[slot].</param>
/// <param name="CarriedCombo">
/// The combo the slot STARTS this song with - this is what makes a course combo carry from
/// chart to chart. sub_436960 stores it at net+894964[slot], and the play reset sub_422250
/// then seeds the live combo with it (`player+212 = net[894964 + 4*slot]`) instead of
/// zeroing it. sub_424AA0 subtracts the same value when computing the song's own max combo,
/// so the carried head start is not double counted in the result record.
/// Sending 0 here (the old behaviour) is precisely what reset the on-screen combo each stage.
/// </param>
public sealed record StartParameter(
    byte Slot,
    uint GaugeBits,
    uint CarriedCombo)
{
    /// <summary>A fresh song: full gauge, no carried combo.</summary>
    public static StartParameter Default(byte slot) => new(
        slot,
        BitConverter.SingleToUInt32Bits(100.0f),
        0);

    /// <summary>A course stage that continues the previous stage's combo run.</summary>
    public static StartParameter Continuing(byte slot, uint carriedCombo) => new(
        slot,
        BitConverter.SingleToUInt32Bits(100.0f),
        carriedCombo);
}

public static class OnStartParameterInfPacket
{
    // Korean OnStartParameterInf is 12 wire bytes: slot@3 + gaugeBits u32@4 + combo u32@8.
    // sub_436960: net[894940 + 4*slot] = @4, net[894964 + 4*slot] = @8.
    public static Packet Build(
        StartParameter value,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(value);
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnStartParameterInf, control)
            .WriteByte(value.Slot)
            .WriteUInt32(value.GaugeBits)
            .WriteUInt32(value.CarriedCombo)
            .Build();
    }

    public static StartParameter Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        StartParameter value = new(
            reader.ReadByte(),
            reader.ReadUInt32(),
            reader.ReadUInt32());
        reader.EnsureComplete();
        return value;
    }
}

public enum StartResult : byte
{
    // The Korean client (OnStartInf handler sub_435000) only begins play — calls
    // sub_436690 — when the result byte@3 == 152 (0x98). Any other value leaves the
    // room idle, so this is the real "GO" code, not China's 0x9A.
    Success = 0x98,

    // BattleClubDirector::OnStartInf (sub_4528F1) maps 0x99..0x9E directly to
    // STARTMSG1..STARTMSG6. Keep these values explicit so a normal refusal never falls
    // through to the client's "Unknown Start Result" popup.
    NotAllPlayersReady = 0x99,
    TeamPlayerCountMismatch = 0x9A,
    TeamBattleRequiresTwoTeams = 0x9B,
    TeamHasInsufficientPlayers = 0x9C,
    NotEnoughPlayers = 0x9D,
    InsufficientMax = 0x9E,

    // 0x9F is not recognized by the client and remains the fallback for technical
    // failures that have no retail message, such as a missing chart or shutdown race.
    GameInfoUnavailable = 0x9F
}

public sealed record StartResponse(StartResult Result, short EventId);

public static class OnStartInfPacket
{
    // Korean OnStartInf is 6 wire bytes: result u8@3 (152 = start) + eventId i16@4.
    // No reserved tail — the China 8-byte pad overflowed the 6-byte packet and threw,
    // so the start signal was never delivered and the chart never began.
    public static Packet Build(
        StartResult result,
        short eventId = 0,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnStartInf, control)
            .WriteByte((byte)result)
            .WriteInt16(eventId)
            .Build();

    public static StartResponse Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        StartResponse value = new(
            (StartResult)reader.ReadByte(),
            reader.ReadInt16());
        reader.EnsureComplete();
        return value;
    }
}

public static class OnPlayStartInfPacket
{
    // Korean OnPlayStartInf is a bare 3-byte signal (id + control, no body) that tells
    // every loaded client to begin the chart. China's 8-byte reserved pad overflowed it.
    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnPlayStartInf, control)
            .Build();
}

public static class OnPlaySkipInfPacket
{
    // Id 0x68 appears in sub_42FB20 but is absent from sub_42F440. The receive parser
    // frames an unregistered ID as three bytes, then the dispatcher reaches sub_435250.
    // A larger response leaves its extra bytes in the receive stream.
    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnPlaySkipInf, control).Build();
}

public static class OnLoadCompleteInfPacket
{
    // Korean OnLoadCompleteInf is 4 wire bytes: slot byte@3, no reserved tail (China's
    // 8-byte pad overflowed the 4-byte packet).
    public static Packet Build(byte slot, byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnLoadCompleteInf, control)
            .WriteByte(slot)
            .Build();
}

public static class OnCheckDataReqPacket
{
    // Korean OnCheckDataReq is 7 wire bytes: sequence u32@3, no reserved tail.
    public static Packet Build(uint sequence, byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnCheckDataReq, control)
            .WriteUInt32(sequence)
            .Build();
}

/// <summary>
/// The client's 11-byte live-play snapshot. sub_424400 writes gauge/progress followed by
/// player+220, which the local player stores XORed with the per-login mask from
/// sub_430A10. The receiver stores the same field for remote players in plain form, so
/// the server must remove that mask before relaying. The final byte is the player's
/// live/failed state.
/// </summary>
public sealed record PlayState(byte[] Data)
{
    private const int LifeGaugeOffset = 0;
    private const int BaseScoreOffset = 4;
    private const int EncodedMaxComboOffset = 8;

    /// <summary>Current life/groove gauge copied from Player+384.</summary>
    public float LifeGauge
    {
        get
        {
            GameplayProtocol.RequireLength(
                Data, GameplayProtocol.PlayStateSize, nameof(Data));
            return BinaryPrimitives.ReadSingleLittleEndian(
                Data.AsSpan(LifeGaugeOffset, sizeof(float)));
        }
    }

    /// <summary>
    /// Monotonic judgment/base score copied from Player+248. A combo-preserving
    /// judgment contributes at most 100 points in the retail judgment table.
    /// </summary>
    public float BaseScore
    {
        get
        {
            GameplayProtocol.RequireLength(
                Data, GameplayProtocol.PlayStateSize, nameof(Data));
            return BinaryPrimitives.ReadSingleLittleEndian(
                Data.AsSpan(BaseScoreOffset, sizeof(float)));
        }
    }

    public ushort EncodedMaxCombo
    {
        get
        {
            GameplayProtocol.RequireLength(
                Data, GameplayProtocol.PlayStateSize, nameof(Data));
            return BinaryPrimitives.ReadUInt16LittleEndian(
                Data.AsSpan(EncodedMaxComboOffset, sizeof(ushort)));
        }
    }

    /// <summary>
    /// Decodes the maximum combo earned during this chart. For a course this excludes
    /// the combo carried into the chart; item battle cannot be a course, so it is the
    /// exact battle-song maximum.
    /// </summary>
    public ushort DecodeMaxCombo(ushort loginKey) =>
        (ushort)(EncodedMaxCombo ^ loginKey);

    /// <summary>
    /// Converts the sending client's masked maximum-combo word into the unmasked form
    /// that <c>OnPlayStateInf</c> writes directly into a remote player's state.
    /// </summary>
    public PlayState DecodeMaxComboForRelay(ushort loginKey)
    {
        GameplayProtocol.RequireLength(
            Data, GameplayProtocol.PlayStateSize, nameof(Data));
        byte[] relayed = Data.ToArray();
        ushort maximumCombo = DecodeMaxCombo(loginKey);
        BinaryPrimitives.WriteUInt16LittleEndian(
            relayed.AsSpan(EncodedMaxComboOffset, sizeof(ushort)),
            maximumCombo);
        return new PlayState(relayed);
    }
}

public static class PlayStateInfPacket
{
    public static PlayState Parse(Packet packet)
    {
        // sub_435360 builds the 22-byte request as an 8-byte request/auth wrapper
        // followed by the exact 11-byte state consumed by sub_4288F1. Only that state
        // belongs in the 15-byte server relay (slot + 11 bytes).
        DjMaxPacketReader reader = new(packet);
        reader.Skip(GameplayProtocol.ReservedSize);
        PlayState value = new(reader.ReadBytes(GameplayProtocol.PlayStateSize));
        reader.EnsureComplete();
        return value;
    }
}

public static class OnPlayStateInfPacket
{
    public static Packet Build(
        byte slot,
        PlayState state,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(state);
        GameplayProtocol.RequireLength(
            state.Data, GameplayProtocol.PlayStateSize, nameof(state.Data));
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnPlayStateInf, control)
            .WriteByte(slot)
            .WriteBytes(state.Data)
            .Build();
    }
}

public static class OnPlayOverInfPacket
{
    // Korean OnPlayOverInf is 4 wire bytes: reason byte@3, no reserved tail.
    public static Packet Build(byte reason = 0, byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnPlayOverInf, control)
            .WriteByte(reason)
            .Build();
}

/// <summary>
/// Result selector stored at byte +38 of the server's 47-byte result record.
/// sub_45F4BC maps 1..11 to the medal animation and 12..20 to the A-to-F
/// animation. The concrete values below are also confirmed by retail result
/// packets (8=Bronze MAX, 10=Silver Disc, 11=Bronze Disc, 12=B+, 13=B,
/// 15=C, and 16=D+).
/// </summary>
public enum StageResultRank : byte
{
    None = 0,
    SapphireDisc = 1,
    RubyDisc = 2,
    RainbowMax = 3,
    DevilDisc = 4,
    SteelMax = 5,
    GoldenMax = 6,
    SilverMax = 7,
    BronzeMax = 8,
    GoldenDisc = 9,
    SilverDisc = 10,
    BronzeDisc = 11,
    BPlus = 12,
    B = 13,
    CPlus = 14,
    C = 15,
    DPlus = 16,
    D = 17,
    EPlus = 18,
    E = 19,
    F = 20
}

/// <summary>
/// The rank/medal and its score bonus. Bonus values and thresholds are the KOREAN
/// service's, per the published rank table; the enum indices come from the captures.
/// </summary>
public readonly record struct StageResultAward(
    StageResultRank Rank,
    uint RankBonus)
{
    public const uint AllComboBonus = 10_000;

    /// <summary>
    /// The matching PRIZE/COLLECTION code for a result-screen disc or MAX award.
    /// Result selectors and collection artwork use different number orders, so this must
    /// be an explicit mapping rather than an arithmetic conversion. Letter grades do not
    /// represent collectible discs and return null.
    /// </summary>
    public ushort? CollectionCode => Rank switch
    {
        StageResultRank.SapphireDisc => 0x409,
        StageResultRank.RubyDisc => 0x400,
        StageResultRank.RainbowMax => 0x401,
        StageResultRank.DevilDisc => 0x40A,
        StageResultRank.SteelMax => 0x402,
        StageResultRank.GoldenMax => 0x403,
        StageResultRank.SilverMax => 0x404,
        StageResultRank.BronzeMax => 0x405,
        StageResultRank.GoldenDisc => 0x406,
        StageResultRank.SilverDisc => 0x407,
        StageResultRank.BronzeDisc => 0x408,
        _ => null
    };

    public static StageResultAward Evaluate(float accuracy, bool failed)
    {
        if (failed)
        {
            return new StageResultAward(StageResultRank.F, 0);
        }

        // The result UI displays two decimal places and the special discs require
        // an exact displayed percentage, so classify the same hundredths value.
        int hundredths = Math.Clamp(
            (int)MathF.Round(
                accuracy * 100f,
                MidpointRounding.AwayFromZero),
            0,
            10_000);

        // The extracted N_Solo_Medal.vce contains four special-result selectors
        // before the seven ordinary medals: Sapphire, Ruby, Rainbow, and Devil.
        // Dragon Disc is China-only and has no selector in this client VCE, so its
        // bonus-free 88.88% result correctly falls through to the B+ grade.
        StageResultAward special = hundredths switch
        {
            100 => new StageResultAward(StageResultRank.SapphireDisc, 150_000),
            1_000 => new StageResultAward(StageResultRank.RubyDisc, 200_000),
            7_770 => new StageResultAward(StageResultRank.RainbowMax, 100_000),
            6_660 => new StageResultAward(StageResultRank.DevilDisc, 100_000),
            _ => default
        };
        if (special.Rank != StageResultRank.None)
        {
            return special;
        }

        return hundredths switch
        {
            10_000 => new StageResultAward(StageResultRank.SteelMax, 30_000),
            >= 9_981 => new StageResultAward(StageResultRank.GoldenMax, 20_000),
            >= 9_961 => new StageResultAward(StageResultRank.SilverMax, 15_000),
            >= 9_841 => new StageResultAward(StageResultRank.BronzeMax, 10_000),
            // KOREAN thresholds. The three disc bands are the ONLY ranks that differed
            // between services: Korea 98.01-98.40 / 97.01-98.00 / 96.01-97.00 against
            // China 96.01-98.40 / 93.01-96.00 / 90.01-93.00. Everything from Bronze MAX
            // up is identical in both. Running China's on this client made a 96.5% play
            // a GOLDEN disc when it should be a bronze one.
            >= 9_801 => new StageResultAward(StageResultRank.GoldenDisc, 5_000),
            >= 9_701 => new StageResultAward(StageResultRank.SilverDisc, 3_000),
            >= 9_601 => new StageResultAward(StageResultRank.BronzeDisc, 1_000),
            >= 8_500 => new StageResultAward(StageResultRank.BPlus, 0),
            >= 8_000 => new StageResultAward(StageResultRank.B, 0),
            >= 7_500 => new StageResultAward(StageResultRank.CPlus, 0),
            >= 7_000 => new StageResultAward(StageResultRank.C, 0),
            >= 6_500 => new StageResultAward(StageResultRank.DPlus, 0),
            >= 6_000 => new StageResultAward(StageResultRank.D, 0),
            >= 5_500 => new StageResultAward(StageResultRank.EPlus, 0),
            >= 5_000 => new StageResultAward(StageResultRank.E, 0),
            _ => new StageResultAward(StageResultRank.F, 0)
        };
    }
}

/// <summary>
/// The client's complete 52-byte end-of-song record. The 13 judgment counters are
/// XOR-protected with a key derived from the client's 30-byte login seed; they are
/// decoded before this record is constructed.
/// </summary>
/// <summary>Values the client accepts in the result record's struct+46.</summary>
public static class StageResultState
{
    /// <summary>Failed. sub_428950 forces the result screen into its failed state.</summary>
    public const byte Failed = 0;

    /// <summary>Cleared a normal stage; sub_46E94C lights "CLEARED" on == 1.</summary>
    public const byte Cleared = 1;

    /// <summary>
    /// Score/Item Battle result. Retail 0x70 captures use this for every occupied
    /// battle slot; the actual winner/loser decision comes from struct+6 placement.
    /// </summary>
    public const byte Battle = 2;

    /// <summary>
    /// Cleared a COURSE stage. The course total-result scene sub_4963E3 decides pass/fail
    /// with `record[struct+46] == 3` and nothing else - the per-objective SUCCESS/FAIL rows
    /// it draws are computed separately and do not feed that test. Sending the ordinary
    /// Cleared value here failed the course no matter how the objectives went.
    /// </summary>
    public const byte CourseCleared = 3;
}

public sealed record StageResult(
    uint SessionToken,
    byte ClientFlags,
    ushort TotalNotes,
    IReadOnlyList<ushort> Judgments,
    float Gauge,
    ushort EncodedCombo,
    uint CurrentCombo,
    uint MaxCombo,
    ushort AuxiliaryValue,
    byte ResultState,
    byte Tail)
{
    public const int JudgmentCount = 13;

    // sub_421910 builds the client's accuracy table from these exact values, and
    // sub_424AA0 adds the selected value for every judgment.
    private static readonly int[] JudgmentAccuracyWeights =
        [0, 0, 1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    /// <summary>Judgment 1 is the combo-breaking miss (sub_424AA0).</summary>
    public ushort Breaks => Judgments.Count > 1 ? Judgments[1] : (ushort)0;

    public ushort NotesHit => (ushort)Math.Max(0, TotalNotes - Breaks);

    // This is the CLIENT'S 0x6F encoding, where 1 means that the player failed.
    // The server-authored 0x70 record uses the opposite normal-stage encoding
    // (0 failed, 1 cleared), so OnStageResultExInfPacket must translate it.
    public bool Failed => ResultState == 1;

    public bool FullCombo => Breaks == 0 && !Failed;

    public StageResultRank Rank =>
        StageResultAward.Evaluate(Accuracy, Failed).Rank;

    public uint RankBonus =>
        StageResultAward.Evaluate(Accuracy, Failed).RankBonus;

    public uint BonusScore =>
        RankBonus + (FullCombo ? StageResultAward.AllComboBonus : 0u);

    /// <summary>
    /// Weighted judgment percentage. Long-note charts can contain more scored chart
    /// objects than TotalNotes, so the decoded judgment sum is the correct divisor.
    /// </summary>
    public float Accuracy
    {
        get
        {
            ulong count = 0;
            ulong weighted = 0;
            int length = Math.Min(Judgments.Count, JudgmentAccuracyWeights.Length);
            for (int index = 0; index < length; index++)
            {
                count += Judgments[index];
                weighted += (ulong)Judgments[index] *
                            (uint)JudgmentAccuracyWeights[index];
            }

            return count == 0
                ? 0f
                : Math.Clamp((float)weighted / count, 0f, 100f);
        }
    }

    /// <summary>
    /// A deterministic local score reconstructed from the two score components the
    /// request proves: weighted judgment accuracy and maximum combo. Retail applies
    /// chart-specific modifiers which are not sent in 0x6F; this gives the result UI
    /// a stable meaningful score without replaying a captured player's value.
    /// </summary>
    public uint Score
    {
        get
        {
            const double PerfectJudgmentScore = 200_000d;
            const double ComboPointValue = 90d;
            double value = Accuracy / 100d * PerfectJudgmentScore +
                           MaxCombo * ComboPointValue;
            return value >= uint.MaxValue
                ? uint.MaxValue
                : (uint)Math.Round(value, MidpointRounding.AwayFromZero);
        }
    }
}

/// <summary>
/// Parses the client's end-of-song StageResultInf (0x6F/Test00, 59 wire bytes).
/// sub_423E30 constructs the 52-byte record and sub_435570 appends its four-byte
/// checksum. sub_426700 proves the 13 u16 counters at record+8 are XORed with the
/// per-client key installed during login. sub_430AB0 and sub_4314F0 copy the
/// 30-byte connect/login seed before sub_430A10 derives this key from its tail.
/// </summary>
public static class StageResultInfPacket
{
    private const int JudgmentArrayOffset = 8;
    private const int JudgmentKeyDwordOffset = 24;
    private const int JudgmentKeyWordOffset = 28;
    private const int JudgmentKeySeedSize =
        JudgmentKeyWordOffset + sizeof(ushort);

    public static ushort DeriveJudgmentKey(ReadOnlySpan<byte> sessionSeed)
    {
        // Both login paths use the same tail formula. JP sends the exact 30 bytes read
        // here, while the Korean OnConnectAck model has a 32-byte seed. Requiring the
        // Korean size made every valid JP session appear to have no usable seed.
        if (sessionSeed.Length < JudgmentKeySeedSize)
        {
            throw new ArgumentException(
                $"Judgment seed must contain at least {JudgmentKeySeedSize} bytes.",
                nameof(sessionSeed));
        }

        uint dword = BinaryPrimitives.ReadUInt32LittleEndian(
            sessionSeed[JudgmentKeyDwordOffset..]);
        ushort word = BinaryPrimitives.ReadUInt16LittleEndian(
            sessionSeed[JudgmentKeyWordOffset..]);
        return unchecked((ushort)(dword + word));
    }

    /// <summary>
    /// Judgment bucket zero is initialized with the other counters but is never
    /// incremented: sub_424AA0 only handles judgment indices greater than zero.
    /// Its encrypted wire value is therefore 0 XOR key, which recovers the exact
    /// per-client mask even when the connection seed is no longer available.
    /// </summary>
    public static ushort RecoverJudgmentKey(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        DjMaxPacketReader reader = new(packet);
        reader.Skip(JudgmentArrayOffset);
        return reader.ReadUInt16();
    }

    public static StageResult Parse(Packet packet, ushort judgmentKey)
    {
        DjMaxPacketReader reader = new(packet); // positioned at wire +3
        uint sessionToken = reader.ReadUInt32();
        byte clientFlags = reader.ReadByte();
        reader.Skip(1);
        ushort totalNotes = reader.ReadUInt16();
        ushort[] judgments = new ushort[StageResult.JudgmentCount];
        for (int index = 0; index < judgments.Length; index++)
        {
            judgments[index] = (ushort)(reader.ReadUInt16() ^ judgmentKey);
        }
        float gauge =
            BitConverter.UInt32BitsToSingle(reader.ReadUInt32());
        ushort encodedCombo = reader.ReadUInt16();
        uint currentCombo = reader.ReadUInt32();
        uint maxCombo = reader.ReadUInt32();
        ushort auxiliaryValue = reader.ReadUInt16();
        byte resultState = reader.ReadByte();
        byte tail = reader.ReadByte();
        reader.Skip(sizeof(uint)); // CRC appended by sub_435570
        reader.EnsureComplete();
        return new StageResult(
            sessionToken,
            clientFlags,
            totalNotes,
            judgments,
            gauge,
            encodedCombo,
            currentCombo,
            maxCombo,
            auxiliaryValue,
            resultState,
            tail);
    }
}

/// <summary>
/// Builds the 48-byte per-stage result payload the client stores before showing the
/// result screen. sub_435600 reads payload[0] as the occupied room slot and passes the
/// following 47-byte record to sub_428950.
/// </summary>
/// <summary>
/// Aggregated course figures that replace the single stage's numbers in the 0x70 record.
///
/// The client cannot total a course itself: sub_428950 copies each stage's record into
/// <c>47 * dword_6846E4 + player + 416</c>, and sub_44EDCE advances that index as
/// <c>(n + 1) % 3</c> - a three-slot rolling buffer, too small to archive a six-stage
/// course. The total-result scene sub_4963E3 then reads exactly ONE slot, which is why the
/// panel showed only the last song. So the totals have to arrive already summed.
/// </summary>
public sealed record StageTotals(
    ushort NotesHit,
    ushort Breaks,
    uint MaxCombo,
    uint Score,
    uint BonusScore,
    float Accuracy);

/// <summary>
/// The three end-of-song decisions the server owns. None can be derived from the client's
/// own report, so each travels in a server-authored field of the result record.
/// </summary>
/// <param name="Money">Money credited for the run (struct+42).</param>
/// <param name="LeveledUp">Whether the run raised the level (struct+39).</param>
/// <param name="NewRecord">Whether the run beat the stored best (struct+44).</param>
public sealed record StageAward(uint Money, bool LeveledUp, bool NewRecord)
{
    public static readonly StageAward None = new(0, false, false);
}

/// <summary>
/// Assigns the zero-based ordinal stored at result record struct+6. A player that
/// failed is always placed behind every survivor, then score and accuracy break the
/// remaining order. Slot is the final tie-breaker so every occupied row is distinct.
/// </summary>
public static class StagePlacementPolicy
{
    public static IReadOnlyDictionary<byte, byte> Assign(
        IEnumerable<(byte Slot, StageResult Result)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        (byte Slot, StageResult Result)[] ordered = entries
            .OrderBy(entry => entry.Result.Failed ? 1 : 0)
            .ThenByDescending(entry => entry.Result.Score)
            .ThenByDescending(entry => entry.Result.Accuracy)
            .ThenByDescending(entry => entry.Result.MaxCombo)
            .ThenBy(entry => entry.Slot)
            .ToArray();

        Dictionary<byte, byte> placements = [];
        for (int index = 0; index < ordered.Length; index++)
        {
            if (!placements.TryAdd(ordered[index].Slot, checked((byte)index)))
            {
                throw new ArgumentException(
                    $"Result set contains duplicate slot {ordered[index].Slot}.",
                    nameof(entries));
            }
        }
        return placements;
    }

    /// <summary>
    /// Placements for a TEAM battle. Sides win or lose together, so every member of the
    /// best-scoring side shares placement 0 and everyone else shares 1 - as opposed to the
    /// per-player ladder <see cref="Assign"/> builds for a room with no sides.
    ///
    /// A side's strength is the sum of its members' scores, so one carry player cannot be
    /// out-placed by a weaker side that happens to hold the single highest row.
    /// </summary>
    public static IReadOnlyDictionary<byte, byte> AssignTeams(
        IEnumerable<(byte Slot, byte Team, StageResult Result)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        (byte Slot, byte Team, StageResult Result)[] rows = [.. entries];

        byte? best = rows
            .GroupBy(row => row.Team)
            .OrderByDescending(side => side.Sum(row => (long)row.Result.Score))
            .ThenByDescending(side => side.Average(row => row.Result.Accuracy))
            .ThenBy(side => side.Key)
            .Select(side => (byte?)side.Key)
            .FirstOrDefault();

        Dictionary<byte, byte> placements = [];
        foreach ((byte slot, byte team, _) in rows)
        {
            if (!placements.TryAdd(slot, team == best ? (byte)0 : (byte)1))
            {
                throw new ArgumentException(
                    $"Result set contains duplicate slot {slot}.", nameof(entries));
            }
        }
        return placements;
    }
}

public static class OnStageResultExInfPacket
{
    /// <summary>Retail uses 0xFF when a result has no multiplayer placement.</summary>
    public const byte UnrankedPlacement = byte.MaxValue;

    // The slot must match an occupied room member; the native lookup does not guard a
    // missing member before copying the result record.
    /// <summary>
    /// Overrides the server-authored result fields for probing. Two playthroughs resolved
    /// them against sub_46E94C: +42 is the money earned (a probe value of 222 appeared in
    /// the gained-MAX box), +44 raises the NEW RECORD banner (its low byte becomes
    /// scene+68, which sub_46D099 shows four seconds in), and +40 is read by nothing in
    /// the solo result scene - so the probe's first slot is repurposed to drive struct+39,
    /// the level-up notice, which is otherwise the only one a probe could not reproduce.
    /// Setting the probe replaces all of them; clear it to send the real award again.
    /// </summary>
    public static (ushort At40, ushort At42, ushort At44)? FieldProbe { get; set; }

    /// <summary>
    /// struct+42 is the money the run earned. The result-screen constructor sub_46E94C
    /// copies it to scene+164, which is the first of the three values the "price window"
    /// (MoneyMax_Single) draws - the "N MAX" gained box - followed by the money held
    /// (net+794169) and a percentage derived from it. It is a u16, so the award is
    /// clamped rather than wrapped.
    /// </summary>
    public static Packet Build(
        byte slot,
        StageResult result,
        StageAward? award = null,
        StageTotals? totals = null,
        byte placement = UnrankedPlacement,
        byte? resultStateOverride = null,
        bool carriesCombo = false,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(result);
        award ??= StageAward.None;
        // A course's final record carries the whole course's figures; everything else
        // still describes the single stage it came from.
        ushort notesHit = totals?.NotesHit ?? result.NotesHit;
        ushort breaks = totals?.Breaks ?? result.Breaks;
        // The client's 0x6F MaxCombo is player+216, and sub_424AA0 is the ONLY writer of
        // that field: it does max(max, current) and nothing ever resets it. So it is a
        // session-lifetime high-water mark, not this stage's combo, and a ranked match
        // reports the same stale peak on every row - a stage with no breaks can end up
        // printing a combo larger than its own note count, three times over.
        //
        // Clamping to the notes actually hit fixes that: it is the hard ceiling for ONE
        // stage, exact when nothing broke, and a bound otherwise.
        //
        // A COURSE is the exception and must not be clamped. There the combo really does
        // carry from chart to chart, so a middle stage legitimately reports more than its
        // own notes - a 172/268/330 full combo reports 172, then 440, then 770. Clamping
        // that would cut 440 back to 268 and break the carry the course panel depends on.
        uint reportedCombo = totals?.MaxCombo ?? result.MaxCombo;
        uint maxCombo = carriesCombo ? reportedCombo : Math.Min(reportedCombo, notesHit);
        uint score = totals?.Score ?? result.Score;
        uint bonusScore = totals?.BonusScore ?? result.BonusScore;
        float accuracy = totals?.Accuracy ?? result.Accuracy;
        // The medal must describe the same accuracy written at struct+34. On a terminal
        // course record that is the accumulated course accuracy, not the last stage's
        // accuracy. Keeping result.Rank here made the percentage and medal disagree.
        StageResultRank rank = StageResultAward.Evaluate(accuracy, result.Failed).Rank;
        (ushort at40, ushort at42, ushort at44) = FieldProbe ??
            (0, Clamp16(award.Money), award.NewRecord ? (ushort)1 : (ushort)0);
        // struct+39 gates the level-up notice, so the probe has to be able to drive it
        // too; otherwise a probe run could never reproduce the notice.
        byte at39 = FieldProbe is { At40: var probe39 } && probe39 != 0
            ? (byte)1
            : award.LeveledUp ? (byte)1 : (byte)0;
        byte resultState = resultStateOverride ?? result.ResultState switch
        {
            StageResultState.CourseCleared => StageResultState.CourseCleared,
            _ when result.Failed => StageResultState.Failed,
            _ => StageResultState.Cleared
        };
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnStageResultExInf, control)
            .WriteByte(slot)                                  // payload+0
            .WriteUInt32(result.SessionToken)                 // struct+0
            .WriteByte(result.ClientFlags)                    // struct+4
            .WriteByte(0)                                     // struct+5
            .WriteByte(placement)                             // struct+6 battle placement
            .WriteUInt16(notesHit)                            // struct+7  MAX/hit
            .WriteUInt16(breaks)                              // struct+9  BREAK
            .WriteUInt16(Clamp16(maxCombo))                   // struct+11 combo
            .WriteUInt32(result.CurrentCombo)                 // struct+13
            .WriteUInt32(maxCombo)                            // struct+17
            .WriteUInt32(BitConverter.SingleToUInt32Bits(
                result.Gauge))                                // struct+21 gauge
            .WriteByte(result.FullCombo ? (byte)1 : (byte)0) // struct+25
            .WriteUInt32(score)                               // struct+26 score
            .WriteUInt32(bonusScore)                          // struct+30 bonus
            .WriteUInt32(BitConverter.SingleToUInt32Bits(
                accuracy))                                    // struct+34 accuracy
            .WriteByte((byte)rank)                            // struct+38 medal/grade
            .WriteByte(at39)                                  // struct+39 level-up notice
            .WriteUInt16(at40)                                // struct+40 unread by the scene
            .WriteUInt16(at42)                                // struct+42 money earned
            .WriteUInt16(at44)                                // struct+44 NEW RECORD banner
            .WriteByte(resultState)                           // struct+46 result scene state
            .Build();
    }

    private static ushort Clamp16(uint value) =>
        value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;
}
