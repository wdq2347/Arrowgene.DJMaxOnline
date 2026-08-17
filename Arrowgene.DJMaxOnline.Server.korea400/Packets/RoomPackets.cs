using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

public sealed record RoomCreateRequest(
    byte[] TitleField,
    byte RoomType,
    byte Capacity,
    byte LevelRestriction,
    byte GameType,
    byte KeyMode,
    byte MatchMode,
    byte EffectorFlag,
    byte PublicFlag,
    byte[] PasswordField,
    byte DifficultyRestriction = 0)
{
    /// <summary>
    /// The chart difficulty the room is locked to: 0 free, otherwise
    /// <see cref="SongDifficulty"/> + 1 (1 EASY, 2 NORMAL, 3 HARD, 4 MX, 5 SC) - the order
    /// of the FREE/EASY/NORMAL/HARD/MX/SC row in the create dialog.
    ///
    /// It arrives as the LAST BYTE OF THE PACKET, not in the LevelRestriction slot. The
    /// client's sender (sub_433E10) copies a 4-byte block to wire 52..55 whose final byte
    /// is the dialog's difficulty selection, so the value sat inside what this parser used
    /// to read as a 16-byte password field and was discarded - which is why every room came
    /// out FREE however the host set it. LevelRestriction (wire 34) is never written by the
    /// client at all.
    /// </summary>
    public bool HasDifficultyLock => DifficultyRestriction != 0;

    /// <summary>The locked difficulty, or null when the room is free.</summary>
    public SongDifficulty? LockedDifficulty =>
        DifficultyRestriction == 0 ? null : (SongDifficulty)(DifficultyRestriction - 1);

    /// <summary>
    /// Whether the room actually carries a password. The client's own sender sub_433E10
    /// writes the password as up to 10 characters plus a terminator at wire 43, entirely
    /// separately from the flag bytes - so an EMPTY password field is the only thing that
    /// makes a room open.
    ///
    /// This used to be <c>PublicFlag != 0</c>, but that byte (wire 42) is the room-type
    /// toggle from the create dialog, not "has a password". A room made without premium
    /// selected therefore reported itself locked: the lobby drew a padlock on it and
    /// JoinRoom demanded a password match that the joiner could never satisfy.
    /// </summary>
    public bool HasPassword =>
        PasswordField.Length != 0 && PasswordField[0] != 0;

    public bool IsPublic => !HasPassword;

    /// <summary>
    /// Whether a JoinRoomReq credential carries this room's password.
    ///
    /// The two fields are DIFFERENT SIZES - the create request stores 16 bytes, the join
    /// credential is 12 - and the join tail's exact split is unverified, so a raw
    /// byte-for-byte compare (even a truncated one) rejects a correct password over
    /// padding or trailing junk. Compare the NUL-terminated text only, which is all the
    /// player actually typed.
    /// </summary>
    public bool PasswordMatches(byte[]? credential)
    {
        if (!HasPassword)
        {
            return true;
        }
        return string.Equals(
            NulTerminated(PasswordField), NulTerminated(credential), StringComparison.Ordinal);
    }

    private static string NulTerminated(byte[]? field)
    {
        if (field == null)
        {
            return string.Empty;
        }
        int length = Array.IndexOf<byte>(field, 0);
        return System.Text.Encoding.ASCII.GetString(
            field, 0, length < 0 ? field.Length : length);
    }

    public void Validate()
    {
        RoomPacketFields.ValidateFixed(TitleField, CreateRoomReqPacket.TitleSize, nameof(TitleField));
        RoomPacketFields.ValidateFixed(
            PasswordField, CreateRoomReqPacket.PasswordTextSize, nameof(PasswordField));
        if (Capacity is 0 or > LocalRoomProtocol.MaximumSlots)
        {
            throw new InvalidDataException(
                $"Room capacity must be between 1 and {LocalRoomProtocol.MaximumSlots}.");
        }
    }
}

public static class LocalRoomProtocol
{
    public const int MaximumSlots = 6;

    /// <summary>
    /// "No side" - what the 팀선택 panel's SINGLE button sends. It is 0xFF, NOT zero:
    /// zero is team A. Treating 0 as neutral put every player in a singles room on team A,
    /// and validating 0xFF as an out-of-range team meant pressing SINGLE was rejected
    /// outright, so nothing was broadcast and the player's own view never changed.
    /// </summary>
    public const byte SingleTeam = 0xFF;

    /// <summary>Team A. The three sides A/B/C are 0, 1 and 2.</summary>
    public const byte FirstTeam = 0;
    public const byte TeamCount = 3;

    /// <summary>Sides the slot-based default alternates between (A and B).</summary>
    public const byte DefaultTeamCount = 2;

    public const byte HostDefaultTeam = FirstTeam;

    /// <summary>Whether a requested side is one the client can actually draw.</summary>
    public static bool IsValidTeam(byte team) =>
        team == SingleTeam || team < TeamCount;

    /// <summary>
    /// <b>net+794272 is the EFFECTOR-USE flag, and ZERO means effects are ALLOWED.</b>
    /// It is NOT a team mode - the room has no team setting at all (see
    /// <see cref="InitialTeam"/>).
    ///
    /// <c>sub_454EAB</c> (the 0x9D room-settings refresh) does
    /// <c>flag = (raw[49] == 0)</c> and then picks
    /// <c>sub_453277(0)</c> when <c>raw[48] == 3 || flag != 1</c>, else
    /// <c>sub_453277(1)</c>. <c>sub_453277</c> is the modifier panel: it walks the effector
    /// table at 0x559640 ("SPEEDx1", "OFF", ...) and shows or greys dialog controls 6..13.
    /// Its <c>a1 == 0</c> branch RESETS all four modifier slots to entry 0 and then sets
    /// <c>[+36] = 1</c> / vtable+56 - the disable idiom <c>sub_443E9B</c> uses on the
    /// premium radio for a non-premium account. So <c>sub_453277(0)</c> is "no effect" and
    /// item battle is force-routed into it, which is correct: item battle has no effectors.
    ///
    /// Therefore <c>raw[49] == 0</c> is the ENABLED case. Sending 1 made every room
    /// NO EFFECT regardless of what the create dialog asked for.
    /// </summary>
    public const byte EffectsAllowed = 0;
    public const byte EffectsDisabled = 1;

    public static bool EffectsEnabled(byte effectorFlag) =>
        effectorFlag == EffectsAllowed;

    /// <summary>
    /// Every occupant starts on no side. There is NO room-level team mode: 0x9C carries
    /// title, level restriction, password, match mode and the effector flag, and nothing
    /// else. Sides are chosen per player through 0x58/0x59, so a room only becomes a team
    /// room when someone actually picks a side.
    /// </summary>
    public static byte InitialTeam(byte slot, bool isHost) => SingleTeam;

    /// <summary>
    /// The side a slot is put on when the host opens team play. The host keeps the side it
    /// chose and everyone else alternates from it, so a two-player room always ends up
    /// with opposition instead of both players on one team.
    /// </summary>
    public static byte TeamForSlot(byte hostTeam, byte hostSlot, byte slot) =>
        checked((byte)((hostTeam + slot - hostSlot + DefaultTeamCount * 0x100)
            % DefaultTeamCount + FirstTeam));

    /// <summary>
    /// MatchMode values the client sends in CreateRoomReq. Score and item battle differ by
    /// this byte ALONE (everything else in the request is identical), and it is what turns
    /// on the item loop: sub_424AA0 only requests items when net+794271 == 3, and the
    /// item-targeting keys (sub_407050) only run in that mode.
    /// </summary>
    /// <summary>
    /// RANKED. The play-scene director sub_42D5E4 switches on sub_428593(), which reads
    /// net+794271 - the match mode this descriptor carries - and mode 1 takes a scene loop
    /// of its own (sub_42CE65 + sub_42D159/sub_42D1AD). This is the mode a stricter
    /// judgment window belongs to; see LocalLobby.JudgmentAdjustmentMsByMatchMode.
    ///
    /// It is NOT DJ Mission Match. DJ Mission is a feature that occurs WITHIN score and
    /// item battle, so it has no match mode of its own and its trigger is still unknown.
    /// </summary>
    public const byte RankedMatchMode = 1;

    public const byte ScoreBattleMatchMode = 2;
    public const byte ItemBattleMatchMode = 3;
    public const byte CourseMatchMode = 4;
}

/// <summary>
/// The 초대거부 ("refuse invitations") toggle in Lobby_Nav.vci. NOT an opaque token: the
/// Korean packet is four bytes and the only payload byte is the requested state.
/// </summary>
public sealed record InviteRejectRequest(bool Refused);

public static class InviteRejectReqPacket
{
    /// <summary>
    /// <c>sub_434170</c> writes id 166 and a single bool at wire+3, then sends 4 bytes.
    /// Crucially it first sets the pending flag <c>net+895004 = 1</c> and ONLY
    /// <see cref="OnInviteRejectAckPacket"/> clears it, so a server that stays silent
    /// wedges the toggle after the very first click.
    /// </summary>
    public static InviteRejectRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        InviteRejectRequest request = new(reader.ReadByte() != 0);
        reader.EnsureComplete();
        return request;
    }

    public static Packet Build(bool refused, byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.InviteRejectReq, control)
            .WriteByte(refused ? (byte)1 : (byte)0)
            .Build();
}

public static class OnInviteRejectAckPacket
{
    /// <summary>
    /// Four bytes; <c>sub_4341F0</c> stores <c>wire[3] != 0</c> as the new refuse state
    /// (net+895008) and then clears the pending flag (net+895004). Echo the state the
    /// client asked for - it does not remember its own request.
    /// </summary>
    public static Packet Build(bool refused, byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnInviteRejectAck, control)
            .WriteByte(refused ? (byte)1 : (byte)0)
            .Build();

    public static bool Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        bool result = reader.ReadByte() != 0;
        reader.EnsureComplete();
        return result;
    }
}

public static class CreateRoomReqPacket
{
    public const int TitleSize = 32;
    public const int PasswordSize = 16;

    /// <summary>Password bytes proper; the 16th is the difficulty lock.</summary>
    public const int PasswordTextSize = PasswordSize - 1;

    public static RoomCreateRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        RoomCreateRequest request = new(
            reader.ReadBytes(TitleSize),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            // The password occupies the first 15 of these bytes (the client writes at most
            // 10 characters plus a terminator); the 16th is the difficulty lock.
            reader.ReadBytes(PasswordTextSize),
            reader.ReadByte());
        reader.EnsureComplete();
        request.Validate();
        return request;
    }

    public static Packet Build(
        RoomCreateRequest request,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return DjMaxPacketBuilder.Fixed(PacketMeta.CreateRoomReq, control)
            .WriteBytes(request.TitleField)
            .WriteByte(request.RoomType)
            .WriteByte(request.Capacity)
            .WriteByte(request.LevelRestriction)
            .WriteByte(request.GameType)
            .WriteByte(request.KeyMode)
            .WriteByte(request.MatchMode)
            .WriteByte(request.EffectorFlag)
            .WriteByte(request.PublicFlag)
            .WriteBytes(request.PasswordField)
            .WriteByte(request.DifficultyRestriction)
            .Build();
    }
}

public enum CreateRoomResult : byte
{
    // Korean client (sub_433F00) only enters the room when OnCreateRoomAck byte@3
    // == 115 (0x73). China used 0x75.
    Success = 0x73
}

public sealed record CreateRoomResponse(
    CreateRoomResult Result,
    ushort RoomIndex,
    RoomCreateRequest Room);

/// <summary>
/// The room owner's ENTIRE room state comes from this one packet. Its handler
/// <c>sub_433F00</c> does nothing but
/// <c>qmemcpy(net+794231, raw+4, 0x30)</c> — 48 bytes copied verbatim — so every wire
/// offset from 4 onwards maps to a fixed net address:
/// <code>
///   raw+4  -> 794231  room index (also stored to net+794072)
///   raw+6  -> 794233  title[33]        (sub_454EAB reads this address as the title text)
///   raw+41 -> 794268  written, never read by the client (dead)
///   raw+42 -> 794269  key mode
///   raw+43 -> 794270  GAME TYPE
///   raw+44 -> 794271  match mode
///   raw+45 -> 794272  effector use (0 = modifiers allowed)
///   raw+46 -> 794273  room FEE TYPE (FEETYPENAME: normal / premium / event)
///   raw+47 -> 794274  state
/// </code>
/// The settings block used to be written one byte early behind a 32-byte title, which
/// gave the HOST ALONE a corrupted room: the level restriction received the game type,
/// and the game type received a pad byte, i.e. zero. Joiners were unaffected because they
/// take the same fields from OnRoomDescInf. That is what made the host — and only the
/// host — look like a spectator in item battle: <c>Player::CreatePanel</c> (sub_4226B0)
/// enables the item slots on <c>net+794270 == 1</c>.
/// </summary>
public static class OnCreateRoomAckPacket
{
    /// <summary>The title runs to 33 bytes here, exactly as it does in OnRoomDescInf.</summary>
    private const int TitlePadding = 1;

    /// <summary>net+794266 and net+794267, between the title and the settings.</summary>
    private const int ReservedBeforeSettings = 2;

    /// <summary>net+794274..794278, the state bytes the server does not drive.</summary>
    private const int ReservedTailSize = 5;

    public static Packet Build(
        CreateRoomResponse response,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Room.Validate();
        RoomCreateRequest room = response.Room;
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnCreateRoomAck, control)
            .WriteByte((byte)response.Result)
            .WriteUInt16(response.RoomIndex)
            .WriteBytes(room.TitleField)
            .WritePadding(TitlePadding)
            .WritePadding(ReservedBeforeSettings)
            .WriteByte(room.LevelRestriction)
            .WriteByte(room.KeyMode)
            .WriteByte(room.GameType)
            .WriteByte(room.MatchMode)
            .WriteByte(room.EffectorFlag)
            // net+794273 - the room fee type, as in OnRoomDescInf above.
            .WriteByte(room.PublicFlag)
            .WritePadding(ReservedTailSize - 1)
            // WIRE 51 -> net+794278: the room's DIFFICULTY LOCK.
            //
            // sub_434BC0(net, -1) returns *(net + 794275 + 3) - net+794278 - and that is
            // what the room-options dialog highlights its FREE/EASY/../SC row from
            // (sub_452E20) and what the chart gate sub_4544B9 reads. A JOINER gets the
            // same address by a different route: sub_434030 qmemcpys the 48-byte ROOM LIST
            // record over net+794231, so their copy comes from record+47. The host has no
            // list entry for their own room, so this packet is the only way they learn it -
            // and it was padding, which is why the creator's dialog always showed FREE.
            .WriteByte(response.Room.DifficultyRestriction)
            .Build();
    }

    public static CreateRoomResponse Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        CreateRoomResult result = (CreateRoomResult)reader.ReadByte();
        ushort roomIndex = reader.ReadUInt16();
        byte[] title = reader.ReadBytes(CreateRoomReqPacket.TitleSize);
        reader.Skip(TitlePadding);
        reader.Skip(ReservedBeforeSettings);
        byte levelRestriction = reader.ReadByte();
        byte keyMode = reader.ReadByte();
        byte gameType = reader.ReadByte();
        byte matchMode = reader.ReadByte();
        byte effectorFlag = reader.ReadByte();
        byte publicFlag = reader.ReadByte();
        reader.Skip(ReservedTailSize);
        reader.EnsureComplete();
        // Capacity and the room-type toggle are not on this wire at all - the client only
        // ever learns them from the lobby grid record - so they are restored as defaults.
        RoomCreateRequest room = new(
            title,
            RoomType: 0,
            Capacity: LocalRoomProtocol.MaximumSlots,
            levelRestriction,
            gameType,
            keyMode,
            matchMode,
            effectorFlag,
            publicFlag,
            new byte[CreateRoomReqPacket.PasswordTextSize]);
        room.Validate();
        return new CreateRoomResponse(result, roomIndex, room);
    }
}

public sealed record RoomDescriptor(
    byte[] TitleField,
    byte LevelRestriction,
    byte GameType,
    byte KeyMode,
    byte MatchMode,
    byte EffectorFlag,
    byte PublicFlag,
    byte State,
    byte[] ServerState,
    byte DifficultyRestriction = 0)
{
    public static RoomDescriptor FromCreate(RoomCreateRequest room) => new(
        room.TitleField.ToArray(),
        room.LevelRestriction,
        room.GameType,
        room.KeyMode,
        room.MatchMode,
        room.EffectorFlag,
        room.PublicFlag,
        State: 0,
        ServerState: new byte[OnRoomDescInfPacket.ServerStateSize],
        DifficultyRestriction: room.DifficultyRestriction);
}

public static class OnRoomDescInfPacket
{
    // The descriptor's title field is 33 bytes (wire 3..35), one MORE than the 32-byte
    // title CreateRoomReq sends. Proven by sub_434570 (0x9D), which copies 33 bytes from
    // raw+3 and then reads raw[36] into the SAME net+794268 that sub_434260 fills from
    // OnRoomDescInf's raw[36]. Getting this wrong shifts every settings byte by one.
    private const int TitlePadding = 1;
    public const int ServerStateSize = 5;

    public static Packet Build(
        RoomDescriptor room,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(room);
        RoomPacketFields.ValidateFixed(
            room.TitleField, CreateRoomReqPacket.TitleSize, nameof(room.TitleField));
        RoomPacketFields.ValidateFixed(
            room.ServerState, ServerStateSize, nameof(room.ServerState));
        // sub_434260 maps this packet's bytes straight onto the room state the whole
        // battle/course UI reads:
        //   +36 -> net+794268  difficulty/level restriction (0 = FREE)
        //   +37 -> net+794269  key mode  (sub_4285A0 tests == 0)
        //   +38 -> net+794270  GAME TYPE - the same field OnGameTypeInf (0x5B,
        //                      sub_434E40) writes, and what Player::CreatePanel
        //                      (sub_4226B0) reads to enable the item slots
        //   +39 -> net+794271  match mode (4 = course, 3 = item battle)
        //   +40 -> net+794272  EFFECTOR USE - 0 allows modifiers, non-zero greys them
        //   +41 -> net+794273  room FEE TYPE (FEETYPENAME: normal / premium / event)
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnRoomDescInf, control)
            .WriteBytes(room.TitleField)
            .WritePadding(TitlePadding)
            .WriteByte(room.LevelRestriction)
            .WriteByte(room.KeyMode)
            .WriteByte(room.GameType)
            .WriteByte(room.MatchMode)
            .WriteByte(room.EffectorFlag)
            // net+794273 is the ROOM FEE TYPE: sub_42A080 reads it and formats
            // FEETYPENAME<n> - "Normal Stage" / "Premium Room" / "Event Room" - which is
            // the 일반방/프리미엄방 toggle. "|| setroomfree" (sub_4629C2) means free of
            // CHARGE, not FREE difficulty. Do not put the difficulty lock here.
            .WriteByte(room.PublicFlag)
            .WriteByte(room.State)
            .WriteBytes(room.ServerState)
            .Build();
    }

    public static RoomDescriptor Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        byte[] title = reader.ReadBytes(CreateRoomReqPacket.TitleSize);
        reader.Skip(TitlePadding);
        byte levelRestriction = reader.ReadByte();
        byte keyMode = reader.ReadByte();
        byte gameType = reader.ReadByte();
        RoomDescriptor room = new(
            title,
            levelRestriction,
            gameType,
            keyMode,
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadBytes(ServerStateSize));
        reader.EnsureComplete();
        return room;
    }
}

/// <summary>
/// record+43. Confirmed live: a non-zero value is what makes the row animate as
/// "now playing", so a room waiting in the lobby must send zero. (China had this the
/// other way round, which is why every room advertised itself as in-progress.)
/// </summary>
public enum RoomListState : byte
{
    Waiting = 0,
    Playing = 1
}

/// <summary>
/// One row of the lobby grid. Field meanings past the title were confirmed by scanning
/// single bytes against the live client (see docs/korean-unimplemented-packets.md):
///   +37 Unlocked  — the padlock is drawn when this is 0, so non-zero = open room
///   +38 Category  — MUST be 1 for a joinable room (also renders its text blue)
///   +40 MatchMode — sprite set, must be 0..4 or the renderer crashes
///   +42 Premium   — 1 or 2 draws the premium room skin
///   +43 State     — non-zero animates the row as "now playing"
///   +44 DiscId    — u16 disc index; the row shows THAT SONG'S NAME (0 = the first disc,
///                   which is why every room used to advertise the same song)
///   +47 Difficulty— difficulty badge (1 = easy)
/// </summary>
public sealed record RoomListEntry(
    ushort RoomIndex,
    byte[] TitleField,
    byte TitlePadding,
    byte Capacity,
    byte MemberCount,
    byte Unlocked,
    byte Category,
    byte LevelRestriction,
    byte MatchMode,
    byte EffectorFlag,
    byte Premium,
    RoomListState State,
    ushort DiscId,
    byte Reserved46,
    byte Difficulty)
{
    // record+35 IS the "x/y" denominator and record+36 the numerator. Hex-Rays drops the
    // varargs, so this comes from the RAW listing of sub_441D56 at 0x441E28:
    //     movsx eax, byte ptr [esi+23h]   ; record+35
    //     push  eax
    //     movsx eax, byte ptr [esi+24h]   ; record+36
    //     push  eax
    //     push  offset aLobbymsg28        ; "%u/%u"
    //     ...
    //     add   esp, 24h                  ; 9 dwords
    // cdecl pushes right-to-left, so the call is
    //     sub_4BA6D0(ctx, x, y, 1, c1, c2, fmt, [+36], [+35])
    // - first %u = record+36 (headcount), second %u = record+35 (capacity).
    //
    // The row only ever reaches clients standing in the LOBBY. Someone inside the room
    // keeps whatever it said when they last saw it, which is why a closed slot can look
    // like it changed nothing.
    /// <param name="openSlots">
    /// Slots the room still admits players into - the "y" in the row's "x/y". Defaults to
    /// the created capacity; closing slots lowers it.
    /// </param>
    public static RoomListEntry Create(
        ushort roomIndex,
        RoomCreateRequest room,
        int memberCount,
        RoomListState state = RoomListState.Waiting,
        ushort discId = 0,
        byte? openSlots = null) => new(
            roomIndex,
            room.TitleField.ToArray(),
            ProtocolPadding.Unused,
            openSlots ?? room.Capacity,
            checked((byte)memberCount),
            // The padlock is drawn when +37 is zero, so a public room must send non-zero.
            Unlocked: room.IsPublic ? (byte)1 : (byte)0,
            // The lobby filter (sub_442E6A) and the text renderer (sub_441D56) both test
            // this for ==1. Anything else is classed as a Free/Ranking/Course stage and the
            // client refuses to join it ("...스테이지에는 참여할 수 없습니다").
            // Battle rooms only: this drives the blue text, the battle filter tab and
            // whether the client will let anyone join.
            Category: OnRoomInfoInfPacket.IsBattleMode(room.MatchMode)
                ? OnRoomInfoInfPacket.NormalRoomCategory
                : (byte)0,
            room.LevelRestriction,
            room.MatchMode,
            room.EffectorFlag,
            // record+42 picks the row's SKIN: sub_4404A7 uses the gold ClubPanel_Info2
            // sheet when this is 1 or 2, the plain ClubPanel_Info sheet otherwise. This is
            // the premium room's gold listing.
            Premium: room.PublicFlag,
            state,
            // The row advertises the host's selected song by disc index.
            DiscId: discId,
            Reserved46: 0,
            // record+47 is the DIFFICULTY BADGE - the EASY/NORMAL/HARD/MX/SC strip in
            // System/Lobby/ClubPanel_Info.png. sub_4404A7 draws it only when non-zero and
            // indexes the strip with `value + 5 * premiumSkin`, so the encoding is the same
            // one the create dialog sends: 0 free, 1..5 EASY..SC. Hard-coding 0 here is why
            // every room in the list read FREE regardless of how it was made.
            Difficulty: room.DifficultyRestriction);

}

/// <summary>
/// Korean lobby room-grid add (id 0x39). The client sub_433870 -> sub_4336D0 stores
/// a 48-byte record (keyed by roomIndex at record+0) into the grid container and
/// draws one slot per record. Sending an entry for every slot — occupied or empty —
/// is how the China-style numbered grid appears so the player can click to create a
/// room. The 46 bytes after roomIndex are still being marker-validated (title/state/
/// player-count TBD); an empty slot is a valid roomIndex with the rest zeroed.
/// </summary>
public static class OnRoomInfoInfPacket
{
    public const int RecordSize = 48;
    private const int ListTitleSize = CreateRoomReqPacket.TitleSize + 1;

    /// <summary>
    /// Adds/refreshes one room in the lobby grid. The 48-byte record is the same layout
    /// China carried on its (51-byte) OnRoomInfoUpdateInf — only the id moved — so the
    /// record is roomIndex@0, title[32]+pad@2, then the nine setting bytes and the
    /// activity dword: 2 + 33 + 9 + 4 = 48.
    /// </summary>
    /// <summary>
    /// record+40 (MatchMode) picks the row's sprite set and the client reads it as a
    /// SIGNED byte: 0-1, 2-3 and 4 each select a set, and every other value — including
    /// anything negative or above 4 — falls through to an uninitialized pointer that it
    /// immediately dereferences (crash at sub_4404A7+0x406). Verified from two client
    /// crash dumps. Never put an out-of-range value here.
    /// </summary>
    public const byte MaxMatchMode = 4;

    /// <summary>
    /// record+38. The lobby filter (sub_442E6A) and the row text renderer (sub_441D56)
    /// both compare this against 1; only a 1 counts as a normal, joinable room. Any other
    /// value is treated as a Free/Ranking/Course stage, which the client refuses to join.
    /// </summary>
    public const byte NormalRoomCategory = 1;

    /// <summary>
    /// record+40 selects the stage mode. The names come from TextStock.ini via
    /// sub_42A044 (EXTRATYPENAME1 + 16*mode):
    ///   0 프리 Free, 1 랭킹 Ranking, 2 스코어배틀 Score Battle,
    ///   3 아이템배틀 Item Battle, 4 코스 Course.
    /// Only the two BATTLE modes are multiplayer — the client refuses to join a
    /// Free/Ranking/Course stage. The row also shows the player count
    /// (LOBBYMSG28 = "%u/%u 명") only for modes 2 and 3; other modes show the song name
    /// instead, which is why a Free room's count box renders empty.
    /// </summary>
    public const byte ScoreBattleMode = 2;
    public const byte ItemBattleMode = 3;

    /// <summary>
    /// True for the two multiplayer battle modes. This is what record+38 encodes: the
    /// client renders battle rooms with blue text, lists them under the battle filter tab
    /// (sub_442E6A: tab 2 keeps +38==1, tab 3 keeps +38!=1) and only lets a player join
    /// one. A solo stage must therefore report 0, not 1.
    /// </summary>
    public static bool IsBattleMode(byte matchMode) =>
        matchMode is ScoreBattleMode or ItemBattleMode;

    /// <summary>
    /// The renderer also uses the room index as an ARRAY index — it touches
    /// <c>4 * roomIndex</c> in two per-room float tables sized from the room count the
    /// login packet advertised. An index at or beyond that count reads and writes past
    /// those tables, which corrupts neighbouring rows (they render with garbage alpha)
    /// rather than failing cleanly. Keep every room index below the advertised count.
    /// </summary>
    public static readonly ushort MaxRoomIndex =
        (ushort)LoginLobbyParameters.LocalDefaults.Value3;

    public static Packet Build(
        RoomListEntry room,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (room.MatchMode > MaxMatchMode)
        {
            throw new ArgumentOutOfRangeException(
                nameof(room),
                $"MatchMode (record+40) must be 0-{MaxMatchMode}; " +
                $"{room.MatchMode} crashes the client's row renderer.");
        }
        if (room.RoomIndex >= MaxRoomIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(room),
                $"Room index {room.RoomIndex} is at or beyond the advertised room count " +
                $"{MaxRoomIndex}; the client indexes per-room tables by it and would " +
                "corrupt the grid.");
        }
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnRoomInfoInf, control)
            .WriteUInt16(room.RoomIndex)       // record+0 (the grid container's key)
            .WriteBytes(room.TitleField)       // record+2  title[32]
            .WriteByte(room.TitlePadding)      // record+34 terminator/pad
            .WriteByte(room.Capacity)          // record+35
            .WriteByte(room.MemberCount)       // record+36
            .WriteByte(room.Unlocked)          // record+37 0 draws the padlock
            .WriteByte(room.Category)          // record+38 1 = normal/joinable
            .WriteByte(room.LevelRestriction)  // record+39
            .WriteByte(room.MatchMode)         // record+40 sprite set, 0..4 only
            .WriteByte(room.EffectorFlag)      // record+41
            .WriteByte(room.Premium)           // record+42 1/2 = premium skin
            .WriteByte((byte)room.State)       // record+43 non-zero animates the row
            .WriteUInt16(room.DiscId)          // record+44 disc index -> song name
            .WriteByte(room.Reserved46)        // record+46
            .WriteByte(room.Difficulty)        // record+47 difficulty badge
            .Build();
    }

    /// <summary>Serializes an entry to its raw 48-byte grid record.</summary>
    public static byte[] ToRecord(RoomListEntry room)
    {
        ArgumentNullException.ThrowIfNull(room);
        Packet packet = Build(room);
        byte[] record = new byte[RecordSize];
        // The structured record starts at wire offset 3. Header is wire[2..7] (control
        // plus the first four record bytes) and Data continues from wire 7, so the record
        // is header[1..] followed by the whole data segment.
        byte[] header = packet.Header!;
        int fromHeader = header.Length - 1;
        header.AsSpan(1).CopyTo(record);
        packet.Data.AsSpan().CopyTo(record.AsSpan(fromHeader));
        return record;
    }

    /// <summary>
    /// Sends a raw 48-byte record. Used by the layout scan so a single byte can be varied
    /// against an otherwise valid room — never send a record with an unterminated title,
    /// the client walks the string past the record and crashes.
    /// </summary>
    public static Packet BuildRaw(
        ReadOnlySpan<byte> record,
        byte control = ProtocolPadding.Unused)
    {
        if (record.Length != RecordSize)
        {
            throw new ArgumentException(
                $"Room record must be {RecordSize} bytes.", nameof(record));
        }
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnRoomInfoInf, control)
            .WriteBytes(record)
            .Build();
    }

    public static RoomListEntry Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        ushort roomIndex = reader.ReadUInt16();
        byte[] listTitle = reader.ReadBytes(ListTitleSize);
        RoomListEntry room = new(
            roomIndex,
            listTitle[..CreateRoomReqPacket.TitleSize],
            listTitle[^1],
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            (RoomListState)reader.ReadByte(),
            reader.ReadUInt16(),
            reader.ReadByte(),
            reader.ReadByte());
        reader.EnsureComplete();
        return room;
    }
}

/// <summary>
/// Korean id 0x3A REMOVES a room from the lobby grid — it is not an update. The client
/// (sub_4338D0 -> sub_433790) reads only the room index @3 and drops that entry from the
/// grid container. Refreshing a room is done by re-sending <see cref="OnRoomInfoInfPacket"/>.
/// (China used this id for the full 51-byte room record, which is why sending it on room
/// creation left the grid empty.)
/// </summary>
public static class OnRoomInfoUpdateInfPacket
{
    public static Packet BuildRemove(
        ushort roomIndex,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnRoomInfoUpdateInf, control)
            .WriteUInt16(roomIndex)
            .Build();

    public static ushort ParseRemove(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        ushort roomIndex = reader.ReadUInt16();
        reader.EnsureComplete();
        return roomIndex;
    }
}

public enum RoomMemberState : byte
{
    // sub_4348B0 stores whether the local player is the room owner by comparing THEIR
    // joiner state with 0x8D. Giving that value to every occupied slot makes every client
    // think it is the host, hides the ready control, and enables host-only song controls.
    // 0x8C is the ordinary occupied-member state immediately before the host state.
    Guest = 0x8C,
    Host = 0x8D,

    // Any of 0x8E/0x8F/0x90/0x91/0x94 makes sub_4348B0 remove the joiner keyed by the
    // connection value at +59. 0x8E is used to withdraw a render-only bot from a room.
    Leaving = 0x8E
}

public sealed record RoomMemberInfo(
    byte Slot,
    uint UserId,
    string AccountId,
    string Nickname,
    RoomMemberState State,
    ushort ConnectionId,
    byte Team,
    // Wire+62 is the player's GENDER, not a key mode. sub_4288A9 copies this whole
    // 132-byte record to Player+44 at OnStartInf, so it lands at Player+103, and
    // Panel::LoadNoteVGI (sub_41ED1E @0x41F0AD) picks the PanelGender<slot> frame from
    // it - 0 shows frame 0, anything else frame 2. Feeding it the room's key mode drew
    // the wrong marker in every in-game player window.
    byte Gender,
    uint IconWireValue,
    uint Experience,
    uint Level,
    uint Wins,
    uint Losses,
    uint Draws,
    IReadOnlyList<uint> MiscStatistics,
    uint AccountClass,
    uint Money)
{
    public static RoomMemberInfo CreateLocal(
        LocalPlayerProfile profile,
        byte slot,
        ushort connectionId,
        byte team,
        bool isHost)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        LocalPlayerProgress progress = profile.Progress;
        return new RoomMemberInfo(
            slot,
            profile.WireUserId,
            profile.AccountId,
            profile.Nickname,
            isHost ? RoomMemberState.Host : RoomMemberState.Guest,
            connectionId,
            team,
            profile.Gender,
            profile.IconWireValue(),
            progress.Experience,
            progress.Level,
            progress.Wins,
            progress.Losses,
            progress.Draws,
            progress.MiscStatistics.ToArray(),
            profile.AccountClass,
            progress.Money);
    }

    /// <summary>
    /// Builds the joiner record for a roster stand-in occupying a room slot. A stand-in
    /// has no socket, so it exists only to be rendered as a second occupant: the record is
    /// filled from its <see cref="RosterUser"/> identity, with zeroed play statistics
    /// because it has none. It is always a guest - a bot is never the host.
    /// </summary>
    public static RoomMemberInfo CreateBot(
        RosterUser roster,
        byte slot,
        ushort connectionId,
        byte team)
    {
        ArgumentNullException.ThrowIfNull(roster);
        return new RoomMemberInfo(
            slot,
            roster.UserId,
            roster.AccountId,
            roster.Nickname,
            RoomMemberState.Guest,
            connectionId,
            team,
            roster.Gender,
            roster.IconId is uint iconId
                ? LocalPlayerProfile.EncodeIconId(iconId)
                : ProtocolPadding.UnusedUInt32,
            Experience: 0,
            roster.Level,
            Wins: 0,
            Losses: 0,
            Draws: 0,
            new uint[LocalPlayerProgress.MiscStatisticCount],
            roster.AccountClass,
            Money: 0);
    }
}

public static class OnUpdateJoinerInfoInfPacket
{
    public const int AccountIdSize = 25;
    public const int NicknameSize = 25;

    public static Packet Build(
        RoomMemberInfo member,
        byte control = ProtocolPadding.Unused)
        => Build(PacketMeta.OnUpdateJoinerInfoInf, member, control);

    internal static Packet Build(
        PacketMeta meta,
        RoomMemberInfo member,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.MiscStatistics.Count != LocalPlayerProgress.MiscStatisticCount)
        {
            throw new ArgumentException(
                $"Room member MISC block must contain {LocalPlayerProgress.MiscStatisticCount} values.",
                nameof(member));
        }

        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Fixed(meta, control)
            .WriteByte(member.Slot)
            .WriteUInt32(member.UserId)
            .WriteFixedAscii(member.AccountId, AccountIdSize)
            .WriteFixedAscii(member.Nickname, NicknameSize)
            .WriteByte((byte)member.State)
            .WriteUInt16(member.ConnectionId)
            .WriteByte(member.Team)
            .WriteByte(member.Gender)
            .WriteUInt32(member.IconWireValue)
            .WriteUInt32(member.Experience)
            .WriteUInt32(member.Level)
            .WriteUInt32(member.Wins)
            .WriteUInt32(member.Losses)
            .WriteUInt32(member.Draws);
        OnUpdateUserPropertyInfPacket.WriteMisc(builder, member.MiscStatistics);
        return builder
            .WriteUInt32(member.AccountClass)
            .WriteUInt32(member.Money)
            .Build();
    }

    public static RoomMemberInfo Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        byte slot = reader.ReadByte();
        uint userId = reader.ReadUInt32();
        string accountId = reader.ReadFixedAscii(AccountIdSize);
        string nickname = reader.ReadFixedAscii(NicknameSize);
        RoomMemberState state = (RoomMemberState)reader.ReadByte();
        ushort connectionId = reader.ReadUInt16();
        byte team = reader.ReadByte();
        byte gender = reader.ReadByte();
        uint icon = reader.ReadUInt32();
        uint experience = reader.ReadUInt32();
        uint level = reader.ReadUInt32();
        uint wins = reader.ReadUInt32();
        uint losses = reader.ReadUInt32();
        uint draws = reader.ReadUInt32();
        uint[] misc = OnUpdateUserPropertyInfPacket.ReadMisc(reader);
        RoomMemberInfo member = new(
            slot,
            userId,
            accountId,
            nickname,
            state,
            connectionId,
            team,
            gender,
            icon,
            experience,
            level,
            wins,
            losses,
            draws,
            misc,
            reader.ReadUInt32(),
            reader.ReadUInt32());
        reader.EnsureComplete();
        return member;
    }
}

/// <summary>
/// Legacy three-packet room-member enumeration used by the retail client. The
/// entry is byte-for-byte the same 135-byte member structure as the live update.
/// </summary>
public static class JoinerListPacket
{
    // The Korean start/end markers are bare 3-byte signals (China had an 8-byte tail).
    public static Packet BuildStart(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnJoinerListStart, control).Build();

    public static Packet BuildEntry(
        RoomMemberInfo member,
        byte control = ProtocolPadding.Unused) =>
        OnUpdateJoinerInfoInfPacket.Build(
            PacketMeta.OnJoinerListEnt, member, control);

    public static Packet BuildEnd(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnJoinerListEnd, control).Build();
}

public static class OnPostJoinRoomInfPacket
{
    // Korean OnPostJoinRoomInf is 3 bytes (id + 1 byte), too short for the 5-byte
    // header DjMaxPacketBuilder always reserves, so build it raw like the ping.
    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        new(PacketMeta.OnPostJoinRoomInf, new[] { control });

    public static void Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        reader.EnsureComplete();
    }
}

public sealed record JoinRoomRequest(
    ushort RoomIndex,
    byte[] PasswordField);

public static class JoinRoomReqPacket
{
    // Korean JoinRoomReq is 17 wire bytes: roomIndex u16@3 then a 12-byte credential tail
    // (China sent 25 = index + a 16-byte password + a dword). The tail's exact split is
    // unverified, so it is kept opaque and only compared for equality against the room's
    // stored password.
    public const int CredentialSize = 12;

    public static JoinRoomRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        JoinRoomRequest request = new(
            reader.ReadUInt16(),
            reader.ReadBytes(CredentialSize));
        reader.EnsureComplete();
        return request;
    }

    public static Packet Build(
        JoinRoomRequest request,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(request);
        RoomPacketFields.ValidateFixed(
            request.PasswordField,
            CredentialSize,
            nameof(request.PasswordField));
        return DjMaxPacketBuilder.Fixed(PacketMeta.JoinRoomReq, control)
            .WriteUInt16(request.RoomIndex)
            .WriteBytes(request.PasswordField)
            .Build();
    }
}

/// <summary>
/// The client handler (sub_434030) enters the room only when the result byte is 122.
/// Any other value makes it clear its pending-join state and carry on, which is how a
/// refusal is reported — so a rejected join MUST still be acknowledged or the client
/// waits forever.
/// </summary>
/// <summary>
/// Result byte at wire+5. <c>sub_441523</c> switches on it and shows the matching
/// TextStock message via <c>sub_46373C</c>, so the code IS the refusal reason the player
/// reads - sending a generic 0 told them nothing at all.
/// </summary>
public enum JoinRoomResult : byte
{
    /// <summary>Joined; sub_441523 returns before any message.</summary>
    Success = 122,

    /// <summary>LOBBYMSG13 "The stage is full."</summary>
    RoomFull = 123,

    /// <summary>LOBBYMSG14 "You cannot join while a game is in progress."</summary>
    GameInProgress = 124,

    /// <summary>
    /// LOBBYMSG12 "Incorrect password." Routed through sub_441469 rather than the plain
    /// notice, so the client re-prompts instead of just reporting.
    /// </summary>
    WrongPassword = 125,

    /// <summary>LOBBYMSG15 "You cannot join a Solo Stage."</summary>
    SoloStage = 126,

    /// <summary>LOBBYMSG16 "The stage no longer exists."</summary>
    RoomGone = 130,

    /// <summary>LOBBYMSG17 "Premium Rooms are available only to Premium Members."</summary>
    PremiumOnly = 131,

    /// <summary>
    /// Any value the switch does not name falls through to a notice built from an
    /// UNINITIALISED stack buffer - garbage text. Never send an unlisted code.
    /// </summary>
    Rejected = RoomGone
}

public sealed record JoinRoomResponse(
    ushort RoomIndex,
    JoinRoomResult Result,
    byte Slot);

public static class OnJoinRoomAckPacket
{
    // Korean OnJoinRoomAck is 7 wire bytes: roomIndex u16@3, result@5, slot@6 (China
    // packed 15 with an 8-byte tail). On success the client copies the room's record out
    // of its OWN grid container by that index, so the room must already be in the grid.
    public static Packet Build(
        JoinRoomResponse response,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnJoinRoomAck, control)
            .WriteUInt16(response.RoomIndex)
            .WriteByte((byte)response.Result)
            .WriteByte(response.Slot)
            .Build();

    public static JoinRoomResponse Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        JoinRoomResponse response = new(
            reader.ReadUInt16(),
            (JoinRoomResult)reader.ReadByte(),
            reader.ReadByte());
        reader.EnsureComplete();
        return response;
    }
}

public static class LeaveRoomReqPacket
{
    // A bare 3-byte signal: no body, and too short for the structured reader's header.
    public static void Parse(Packet packet) => ArgumentNullException.ThrowIfNull(packet);
}

public enum LeaveRoomResult : byte
{
    Success = 0x93
}

public static class OnLeaveRoomAckPacket
{
    public static Packet Build(
        LeaveRoomResult result = LeaveRoomResult.Success,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnLeaveRoomAck, control)
            .WriteByte((byte)result)
            .Build();

    public static LeaveRoomResult Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        LeaveRoomResult result = (LeaveRoomResult)reader.ReadByte();
        reader.EnsureComplete();
        return result;
    }
}

public static class ReadyReqPacket
{
    public static void Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        reader.EnsureComplete();
    }
}

public sealed record RoomReadyUpdate(
    bool IsReady,
    ushort ConnectionId,
    uint UserId);

public static class OnReadyInfPacket
{
    public static Packet Build(
        RoomReadyUpdate update,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnReadyInf, control)
            .WriteByte(update.IsReady ? (byte)1 : (byte)0)
            .WriteUInt16(update.ConnectionId)
            .WriteUInt32(update.UserId)
            .Build();

    public static RoomReadyUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        RoomReadyUpdate update = new(
            reader.ReadByte() != 0,
            reader.ReadUInt16(),
            reader.ReadUInt32());
        reader.EnsureComplete();
        return update;
    }
}

public sealed record TeamControlRequest(byte Team);

public static class TeamControlReqPacket
{
    public static TeamControlRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        TeamControlRequest request = new(reader.ReadByte());
        reader.EnsureComplete();
        return request;
    }
}

public sealed record RoomTeamUpdate(byte Slot, byte Team);

public static class OnTeamControlInfPacket
{
    public static Packet Build(
        RoomTeamUpdate update,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnTeamControlInf, control)
            .WriteByte(update.Slot)
            .WriteByte(update.Team)
            .Build();

    public static RoomTeamUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        RoomTeamUpdate update = new(reader.ReadByte(), reader.ReadByte());
        reader.EnsureComplete();
        return update;
    }
}

public static class OnGameTypeInfPacket
{
    public static Packet Build(
        byte gameType,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnGameTypeInf, control)
            .WriteByte(gameType)
            .Build();

    public static byte Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        byte gameType = reader.ReadByte();
        reader.EnsureComplete();
        return gameType;
    }
}

public sealed record RoomChangeRequest(byte[] TitleField, byte[] SettingsField)
{
    // Layout proven by the client's own sender sub_4344E0, which sends 0x32 (50) bytes:
    //   3..35  title (33 bytes)      36  level restriction -> net+794268
    //   37..47 password (11 bytes)   48  match mode        -> net+794271
    //                                49  EFFECTOR USE      -> net+794272
    // SettingsField starts at wire 35, so settings[i] is wire 35+i.
    //
    // There is NO game-type field: a room-settings change cannot alter it, and reading
    // settings[1] as one meant the server broadcast the LEVEL byte as a game type. There
    // is no team field either - sides are per player, over 0x58/0x59.
    /// <summary>
    /// The FREE/EASY/NORMAL/HARD/MX/SC row of the 방옵션변경 dialog, in the same encoding
    /// CreateRoomReq's last byte uses: 0 free, 1..5 EASY..SC. This is the room-change
    /// route to the lock; creation carries it separately.
    /// </summary>
    public byte LevelRestriction => SettingsField[1];

    /// <summary>
    /// The password typed into the dialog - wire 37..47, 10 characters plus a terminator.
    /// It is in the packet and was simply never applied, so setting a password on an
    /// existing room did nothing.
    /// </summary>
    public byte[] Password => SettingsField[2..13];

    public byte MatchMode => SettingsField[13];
    public byte EffectorFlag => SettingsField[14];
}

public static class RoomChangeInfoReqPacket
{
    public const int SettingsSize = 15;

    public static RoomChangeRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        RoomChangeRequest request = new(
            reader.ReadBytes(CreateRoomReqPacket.TitleSize),
            reader.ReadBytes(SettingsSize));
        reader.EnsureComplete();
        return request;
    }
}

public enum RoomChangeResult : byte
{
    Success = 0
}

public static class OnRoomChangeInfoAckPacket
{
    public static Packet Build(
        RoomChangeRequest request,
        RoomChangeResult result = RoomChangeResult.Success,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(request);
        RoomPacketFields.ValidateFixed(
            request.TitleField, CreateRoomReqPacket.TitleSize, nameof(request.TitleField));
        RoomPacketFields.ValidateFixed(
            request.SettingsField,
            RoomChangeInfoReqPacket.SettingsSize,
            nameof(request.SettingsField));
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnRoomChangeInfoAck, control)
            .WriteBytes(request.TitleField)
            .WriteBytes(request.SettingsField)
            .WriteByte((byte)result)
            .Build();
    }

    public static (RoomChangeRequest Request, RoomChangeResult Result) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        RoomChangeRequest request = new(
            reader.ReadBytes(CreateRoomReqPacket.TitleSize),
            reader.ReadBytes(RoomChangeInfoReqPacket.SettingsSize));
        RoomChangeResult result = (RoomChangeResult)reader.ReadByte();
        reader.EnsureComplete();
        return (request, result);
    }
}

public sealed record ChangeDiscRequest(uint DiscId, byte[] SettingsField)
{
    /// <summary>
    /// The chart difficulty the host picked (0=EZ, 1=NM, 2=HD, 3=MX, 4=SC), sent in
    /// the first byte of the settings field. When the host has not toggled difficulty
    /// the client leaves that byte as uninitialised stack, so anything outside 0..4 is
    /// treated as the EZ default rather than fed on as a garbage difficulty (which
    /// would index the client's level table out of bounds).
    /// </summary>
    public byte Difficulty =>
        SettingsField.Length > 0 && SettingsField[0] <= 4 ? SettingsField[0] : (byte)0;
}

public static class ChangeDiscReqPacket
{
    // Korean ChangeDiscReq/OnChangeDiscInf carry 2 settings bytes after the disc id
    // (wire size 9), not China's 10. The first settings byte is the difficulty.
    public const int SettingsSize = 2;

    public static ChangeDiscRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        ChangeDiscRequest request = new(reader.ReadUInt32(), reader.ReadBytes(SettingsSize));
        reader.EnsureComplete();
        return request;
    }
}

public static class OnChangeDiscInfPacket
{
    private const int SettingsSize = ChangeDiscReqPacket.SettingsSize;

    /// <summary>
    /// Announces the selected chart. The settings field must be ECHOED, not zeroed: its
    /// first byte is the difficulty the host picked (0=EZ..4=SC), and this packet is what
    /// every other client - and the host's own room UI - reads it from. Sending 0 back
    /// silently forced every room to the EZ chart no matter which difficulty was chosen.
    /// </summary>
    public static Packet Build(
        uint discId,
        byte difficulty = 0,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnChangeDiscInf, control)
            .WriteUInt32(discId)
            .WriteByte(difficulty)
            .WritePadding(SettingsSize - 1)
            .Build();

    public static (uint DiscId, byte Difficulty) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint discId = reader.ReadUInt32();
        byte difficulty = reader.ReadByte();
        reader.Skip(SettingsSize - 1);
        reader.EnsureComplete();
        return (discId, difficulty);
    }
}

/// <param name="Slot">Target slot index, at wire offset 3.</param>
public sealed record SlotControlRequest(byte Slot);

public static class SlotControlReqPacket
{
    public static SlotControlRequest Parse(Packet packet)
    {
        // Korean 0x53 is 4 wire bytes: id, a per-request control byte, then the slot at
        // offset 3. The control byte varies (0x7E/0x85 observed) and is not an action code.
        //
        // The slot index passes through UNCHANGED. Converting it (on a theory that the
        // wire was one-based) made a lock whose converted index landed on an occupied slot
        // fail the occupancy guard silently, so locking stopped working at all.
        DjMaxPacketReader reader = new(packet);
        byte slot = reader.ReadByte();
        reader.EnsureComplete();
        return new SlotControlRequest(slot);
    }
}

/// <param name="Enabled">
/// Whether the slot is OPEN. It is written to the wire INVERTED - see
/// <see cref="OnSlotControlAckPacket"/>.
/// </param>
public sealed record SlotControlResponse(byte Slot, bool Enabled);

/// <summary>
/// raw+4 is "is the X drawn", i.e. <b>1 = CLOSED, 0 = open</b> - the opposite of an
/// "enabled" flag. <c>sub_45284E</c> says so outright: <c>raw[4] == 1</c> logs
/// <c>"X-ON:%d"</c> and <c>raw[4] == 0</c> logs <c>"X-OFF:%d"</c>, and the X is the closed
/// marker. <c>sub_434350</c> stores the same byte at <c>net+895060 + 4*slot</c>, so that
/// array is "is closed", not "is enabled".
///
/// Sending the enabled flag straight through inverted every lock: opening a slot drew the
/// X and closing one cleared it.
/// </summary>
public static class OnSlotControlAckPacket
{
    // Korean OnSlotControlAck (0x54) is 5 wire bytes: slot at raw+3, enabled at raw+4
    // (sub_434350). The China layout carried an 8-byte reserved tail that cannot fit the
    // 5-byte meta, so every slot-toggle ack threw once the toggle actually ran - which it
    // did not before, because the 12-vs-4 SlotControlReq size desynced first.
    public static Packet Build(
        SlotControlResponse response,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnSlotControlAck, control)
            .WriteByte(response.Slot)
            // Inverted: the byte turns the X ON, so an open slot sends 0.
            .WriteByte(response.Enabled ? (byte)0 : (byte)1)
            .Build();

    public static SlotControlResponse Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        SlotControlResponse response = new(reader.ReadByte(), reader.ReadByte() == 0);
        reader.EnsureComplete();
        return response;
    }
}

internal static class RoomPacketFields
{
    public static void ValidateFixed(byte[]? value, int size, string name)
    {
        if (value == null || value.Length != size)
        {
            throw new ArgumentException($"{name} must contain exactly {size} bytes.", name);
        }
    }

    /// <summary>
    /// Room titles are player-typed CP949 bytes padded to a fixed field. Decoded here for
    /// the status API only; the wire path keeps the raw bytes untouched.
    /// </summary>
    public static string DecodeTitle(byte[]? field)
    {
        if (field == null || field.Length == 0)
        {
            return string.Empty;
        }

        int length = Array.IndexOf(field, (byte)0);
        if (length < 0)
        {
            length = field.Length;
        }
        if (length == 0)
        {
            return string.Empty;
        }

        try
        {
            System.Text.Encoding.RegisterProvider(
                System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(949).GetString(field, 0, length).Trim();
        }
        catch (Exception)
        {
            // A title is cosmetic; never let a decode failure break the API.
            return System.Text.Encoding.ASCII.GetString(field, 0, length).Trim();
        }
    }
}
