using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

/// <summary>
/// Editable local account. Every field copied into the retail client's user
/// record is represented here; no captured account data is used as a default.
/// </summary>
public sealed class LocalPlayerProfile
{
    /// <summary>
    /// A valid icon id. The client's DJMaxObject::LoadIconSet (sub_42B414) parses
    /// System\Icon\IconSet.csv (rows: No,Title,type,filename,remark) and renders an
    /// icon only when its stored value V satisfies BOTH: (V &amp; 0x3FF) == No-1 AND
    /// the client's category function sub_426E29(V) == type-1. So the value is NOT a
    /// flat index — it is category-encoded. Known-good bases (add No-1):
    ///   type 3 avatar/female (45 icons): 0x2000   type 4 avatar/male (24): 0xA400
    ///   type 7 (8 icons): 0x8800
    /// An out-of-catalog value (e.g. 0x842C = type 6 No 45, but type 6 has only 10)
    /// fails the lookup and draws nothing. The wire value is this + 1 (the client
    /// decrements every icon field once on receipt).
    /// </summary>
    public const uint FirstUserIconId = 0x0000A400;

    /// <summary>
    /// PERSISTENT account key. Every SQLite row is keyed on this
    /// (<c>SqlitePlayerRepository.Save</c> upserts by it, <c>TryLoad</c> and
    /// <c>StageScores</c> look it up, <c>SaveWithStageScore</c> rejects a mismatch), so it
    /// must never change for a live account. It is NOT what goes on the wire - see
    /// <see cref="WireUserId"/>.
    /// </summary>
    public uint UserId { get; set; } = 1;

    /// <summary>
    /// Randomised per login and never persisted. The default <see cref="UserId"/> is 1 for
    /// every freshly created profile, so two players could answer to the same id and every
    /// cross-player feature - messenger routing, presence, profile view - collapsed onto
    /// whoever asked. A session id makes the on-the-wire identity unique without touching
    /// the account key the database is built on.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public uint SessionUserId { get; set; }

    /// <summary>
    /// The id every packet should carry. Falls back to the account key until a session is
    /// assigned, so offline//CLI paths keep working unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public uint WireUserId => SessionUserId != 0 ? SessionUserId : UserId;

    /// <summary>
    /// Key the lobby waiter list files this player under (waiter record+71). It must be
    /// STABLE per account and must NOT be the session id: <c>sub_4333F0</c> inserts a new
    /// row whenever the key is unseen and overwrites in place when it is not, so a key
    /// that changed every login stacked up a fresh duplicate row on every reconnect while
    /// the erase only ever named the newest one.
    ///
    /// The account key is unique per account and already small, so it is used directly;
    /// the hash is only a fallback for ids that cannot fit the u16 the client compares.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ushort WaiterKey
    {
        get
        {
            if (UserId is > 0 and <= ushort.MaxValue)
            {
                return (ushort)UserId;
            }
            uint hash = 2166136261;
            foreach (char character in AccountId)
            {
                hash = (hash ^ char.ToUpperInvariant(character)) * 16777619;
            }
            ushort key = (ushort)(hash ^ (hash >> 16));
            return key == 0 ? (ushort)1 : key;
        }
    }

    public string AccountId { get; set; } = "LOCAL";
    public string SecondaryId { get; set; } = string.Empty;
    public string Nickname { get; set; } = "PLAYER";
    public byte State { get; set; }
    public ushort ProfileCode { get; set; }

    /// <summary>
    /// Login-block gender byte (block+54, read by the info box "lobby info gender").
    /// 0 = female, 1 = male.
    /// </summary>
    public byte Gender { get; set; } = 1;

    /// <summary>
    /// Global zero-based icon identifier retained by the client. Null requests
    /// the retail no-selection path; the normal local default is a real icon.
    /// </summary>
    public uint? IconId { get; set; } = FirstUserIconId;

    public uint ProfileFlags { get; set; }

    /// <summary>
    /// Native user-class bit field. player.json exposes this as named booleans while
    /// packets continue to receive the exact 32-bit value expected by the client.
    /// </summary>
    [JsonConverter(typeof(AccountClassJsonConverter))]
    public uint AccountClass { get; set; }

    /// <summary>
    /// Whether this account may log in. A locked or under-review account is refused at
    /// login with the matching client dialog; see <see cref="AccountLockReasons"/>.
    /// </summary>
    public AccountLockState LockState { get; set; } = AccountLockState.None;

    /// <summary>
    /// Why the account was locked. OPERATOR-ONLY: never sent to a client, never
    /// broadcast and never published by the status API. The banned player sees the
    /// client's own fixed dialog, chosen by the reason CODE, not this text.
    /// </summary>
    public string LockReason { get; set; } = string.Empty;

    /// <summary>
    /// PRIZE/COLLECTION (수상경력 / 컬렉션) entries. The client's mycollect dialog
    /// sub_465AD1 reads the 748-byte login block as {code:u16, value:u16} pairs:
    /// code &lt;= 0x3FF is a medal, 0x400..0x43F an event/disc award; 0xFFFF = empty.
    /// Up to 48 entries fit the first (medal) section.
    /// </summary>
    public List<CollectionEntry> Collection { get; set; } = [];
    public ushort Reserved { get; set; }
    /// <summary>
    /// Server-owned Course Club availability. Values are the zero-based course
    /// identifiers consumed by DJMaxNet::OnCourseListInf (packet 0x82).
    /// </summary>
    public List<ushort> AvailableCourseIds { get; set; } =
        [.. Enumerable.Range(0, 62).Select(id => (ushort)id)];

    /// <summary>
    /// This account's per-course best results. SQLite combines these records across every
    /// account into the shared top-50 Course Club ranking board (packet 0x84).
    /// </summary>
    public List<CourseRecord> CourseRecords { get; set; } = [];

    /// <summary>Persisted friends list, blocked users and group names.</summary>
    public MessengerBook Messenger { get; set; } = new();

    /// <summary>
    /// Stand-in accounts that exist only on this server. A local server hosts one real
    /// player, which makes every feature keyed on "somebody else" untestable: the client
    /// draws a messenger or waiter row ONLY when it has that id's 67-byte user record
    /// (sub_48AB44 and sub_48BE29 both skip an id sub_433B80 cannot resolve), and the
    /// server could previously resolve exactly one id. Each roster entry supplies a
    /// record, so contacts, conversation tabs, blocking and groups all become reachable.
    /// </summary>
    public List<RosterUser> Roster { get; set; } = [];

    public LocalPlayerProgress Progress { get; set; } = new();
    public LocalPlayerInventory Inventory { get; set; } = new();

    /// <summary>Returns a new neutral profile on every access.</summary>
    public static LocalPlayerProfile Default => new();


    public uint IconWireValue()
    {
        if (IconId is not uint iconId)
        {
            return ProtocolPadding.UnusedUInt32;
        }

        return EncodeIconId(iconId);
    }

    public static uint EncodeIconId(uint iconId)
    {
        uint wireValue = checked(iconId + 1);
        if (wireValue == ProtocolPadding.UnusedUInt32)
        {
            throw new InvalidDataException(
                $"Icon id {iconId} encodes to the protocol's unused sentinel.");
        }

        return wireValue;
    }

    public void Validate()
    {
        if (UserId == 0)
        {
            throw new InvalidDataException("The local player UserId must be non-zero.");
        }

        ValidateAscii(AccountId, OnUserInfoInfPacket.AccountIdSize, nameof(AccountId), false);
        ValidateAscii(
            SecondaryId,
            OnAuthenticateInAckPacket.SecondaryIdSize,
            nameof(SecondaryId),
            true);
        // OnUserIdInfoInf has the shortest native nickname field (23 bytes);
        // the terminator leaves at most 22 ASCII characters even though the
        // full user/profile records allocate 25.
        ValidateAscii(
            Nickname,
            OnUserIdInfoInfPacket.NicknameSize,
            nameof(Nickname),
            false);
        _ = IconWireValue();
        if (AvailableCourseIds == null)
        {
            throw new InvalidDataException("AvailableCourseIds cannot be null.");
        }
        if (AvailableCourseIds.Count != AvailableCourseIds.Distinct().Count())
        {
            throw new InvalidDataException("AvailableCourseIds cannot contain duplicates.");
        }
        if (CourseRecords == null)
        {
            throw new InvalidDataException("CourseRecords cannot be null.");
        }
        if (CourseRecords
                .Select(record => (record.CourseId, record.KeyMode))
                .Distinct()
                .Count() != CourseRecords.Count)
        {
            throw new InvalidDataException(
                "CourseRecords cannot hold the same course twice for one key mode.");
        }
        ArgumentNullException.ThrowIfNull(Messenger);
        Messenger.Validate();
        ArgumentNullException.ThrowIfNull(Roster);
        if (Roster.Select(user => user.UserId).Distinct().Count() != Roster.Count)
        {
            throw new InvalidDataException("Roster cannot hold a user id twice.");
        }
        foreach (RosterUser user in Roster)
        {
            user.Validate(UserId);
        }
        ArgumentNullException.ThrowIfNull(Progress);
        ArgumentNullException.ThrowIfNull(Inventory);
        Progress.Validate();
        Inventory.Validate();
    }

    internal static void ValidateAscii(
        string? value,
        int fieldSize,
        string fieldName,
        bool allowEmpty)
    {
        if (value == null || (!allowEmpty && value.Length == 0))
        {
            throw new InvalidDataException($"{fieldName} cannot be null or empty.");
        }

        if (value.Any(character => character > 0x7F))
        {
            throw new InvalidDataException($"{fieldName} must contain ASCII characters only.");
        }

        if (value.Length >= fieldSize)
        {
            throw new InvalidDataException(
                $"{fieldName} is limited to {fieldSize - 1} ASCII characters.");
        }
    }
}

/// <summary>
/// Deterministic, visibly non-default account contents for packet and UI diagnostics.
/// This is intentionally opt-in: <see cref="Apply"/> overwrites profile-owned game data
/// but preserves the account id, nickname, persistent id, and password credential.
/// </summary>
public static class DebugProfileSeed
{
    /// <summary>
    /// Applies a rich, known-safe fixture built from item ids present in the bundled
    /// ItemStock.csv.  The values are not rewards and must never be enabled by normal
    /// account creation.
    /// </summary>
    public static void Apply(LocalPlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        profile.SecondaryId = "DBG-JP400";
        profile.State = 0x5A;
        profile.ProfileCode = 0xBEEF;
        // The JP lobby reads gender from the dedicated local-user snapshot byte at +54.
        // Force a male fixture so a displayed female marker proves the packet is not being
        // consumed, rather than merely reflecting an empty database default.
        profile.Gender = 1;
        profile.IconId = LocalPlayerProfile.FirstUserIconId + 7;
        profile.ProfileFlags = 0x13579BDF;
        profile.AccountClass = (uint)(
            AccountClassFlags.Normal |
            AccountClassFlags.Premium |
            AccountClassFlags.PcBang |
            AccountClassFlags.Jjang |
            AccountClassFlags.GameMaster);
        profile.Reserved = 0xD06E;

        profile.Progress = new LocalPlayerProgress
        {
            Level = 37,
            Experience = 123_456,
            Money = 987_654,
            Cash = 456_789,
            AwardPoints = 12_345,
            Wins = 314,
            Losses = 27,
            Draws = 9,
            FreemodeBest5Key = 456_789,
            FreemodeBest7Key = 345_678,
            RankingBest5Key = 234_567,
            RankingBest7Key = 123_456,
            MaxCombo = 987,
            HighestAccuracy = 98.76,
            AverageAccuracy = 94.32,
            MiscStatistics =
            [
                0x01020304, 0x11121314, 0x21222324, 0x31323334, 0x41424344,
                0x51525354, 0x61626364, 0x71727374, 0x81828384, 0x91929394
            ]
        };

        // 769/770 are coin items, 1025/1026 are avatars, and 9217/9218 are equipment
        // entries present in the shipped catalog.  Permanence is represented by zero.
        profile.Inventory = new LocalPlayerInventory
        {
            DefaultItems =
            [
                new InventoryItemSlot(0, 769),
                new InventoryItemSlot(1, 770),
                new InventoryItemSlot(2, 1025)
            ],
            EventItems =
            [
                new InventoryItemSlot(0, 1026),
                new InventoryItemSlot(1, 9217)
            ],
            ShopItems =
            [
                new TimedInventorySlot(0, 1025, 0),
                new TimedInventorySlot(1, 1026, 0)
            ],
            MountItems =
            [
                new TimedInventorySlot(0, 9217, 0),
                new TimedInventorySlot(1, 9218, 0)
            ],
            State = 0x2468ACE0,
            ItemBox = [769, 770, 1025, 1026, 9217, 9218],
            ItemBoxValues = [0, 0, 0, 0, 0, 0]
        };

        // Only these collection code ranges have artwork in the JP client. The
        // renderer maps 0x400..0x413 and 0x420..0x42D onto the EVENT_DISC sheet;
        // arbitrary low values are medal ids and do not make a useful debug fixture.
        profile.Collection =
        [
            .. Enumerable.Range(0x400, 0x14)
                .Select(code => new CollectionEntry((ushort)code, (ushort)(code - 0x3FF))),
            .. Enumerable.Range(0x420, 0x0E)
                .Select(code => new CollectionEntry((ushort)code, (ushort)(code - 0x41F)))
        ];
        profile.AvailableCourseIds =
            [.. Enumerable.Range(0, 62).Select(id => (ushort)id)];
        profile.CourseRecords =
        [
            new CourseRecord
            {
                CourseId = 0,
                KeyMode = (byte)SongKeyMode.FiveKey,
                Score = 765_432,
                Combo = 876,
                Clears = 12
            },
            new CourseRecord
            {
                CourseId = 1,
                KeyMode = (byte)SongKeyMode.SevenKey,
                Score = 654_321,
                Combo = 765,
                Clears = 8
            }
        ];

        profile.Validate();
    }
}

/// <summary>
/// Progress layout proven by sub_437BD0 and the four specialized property
/// update handlers. The ten MISC values remain numbered because the executable
/// does not establish reliable gameplay names for them.
/// </summary>
public sealed class LocalPlayerProgress
{
    public const int MiscStatisticCount = 10;

    /// <summary>
    /// ZERO-BASED, matching both the client and the experience curve.
    ///
    /// The client renders this value plus one, so a stored 1 showed as "Lv 2" on a brand
    /// new account. <see cref="ExperienceCurve.Required"/> also indexes its threshold
    /// table by this value, so starting at 1 skipped the first rung of the curve as well
    /// (60 experience to advance instead of 40).
    /// </summary>
    public uint Level { get; set; }
    public uint Experience { get; set; }
    public uint Money { get; set; } = 10_000;
    /// <summary>
    /// The second shop balance updated by OnPurchaseItemAck.
    ///
    /// A starting balance, not a sandbox one: the shop's prices are tuned, and handing a
    /// new account a million cash means nothing in the cash shop ever has to be earned.
    /// </summary>
    public uint Cash { get; set; } = 10_000;
    /// <summary>Balance reserved for UBS award redemption items.</summary>
    public uint AwardPoints { get; set; } = 10_000;
    public uint Wins { get; set; }
    public uint Losses { get; set; }
    public uint Draws { get; set; }

    // Korean SCORE-tab stats (내정보). Mapped by marker-validation to the 135-byte
    // login user block written by sub_435AE0: block+89 = freemode best 5key, etc.
    public uint FreemodeBest5Key { get; set; }
    public uint FreemodeBest7Key { get; set; }
    public uint RankingBest5Key { get; set; }
    public uint RankingBest7Key { get; set; }
    public uint MaxCombo { get; set; }
    /// <summary>Highest accuracy as a percent (e.g. 98.7). Wire value = percent * 10.</summary>
    public double HighestAccuracy { get; set; }
    /// <summary>Average accuracy as a percent (e.g. 92.34). Wire value = percent * 100.</summary>
    public double AverageAccuracy { get; set; }

    public uint[] MiscStatistics { get; set; } = new uint[MiscStatisticCount];

    public void Validate()
    {
        if (MiscStatistics == null || MiscStatistics.Length != MiscStatisticCount)
        {
            throw new InvalidDataException(
                $"MiscStatistics must contain exactly {MiscStatisticCount} values.");
        }
    }
}

/// <summary>A PRIZE/COLLECTION entry: medal/award code and its displayed value.</summary>
/// <summary>
/// The ten stat words the client keeps at login-block +89..+128.
///
/// These are not free-form: OnUpdateUserPropertyMiscInf (0x29) memcpy's its 40-byte
/// payload straight over that region (sub_435D10: <c>memcpy(net+794185, packet+9, 40)</c>),
/// which is the same span OnLogInAck fills. Both paths therefore have to agree
/// byte-for-byte, so both build the block here rather than laying it out twice.
/// </summary>
public static class UserStatisticsBlock
{
    /// <summary>Dword count; also the size sub_435D10 copies, divided by four.</summary>
    public const int Length = 10;

    public static uint[] Build(LocalPlayerProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return
        [
            progress.FreemodeBest5Key,                            // +89
            progress.RankingBest5Key,                             // +93
            progress.MaxCombo,                                    // +97
            // Both accuracy slots are hundredths. The SCORE tab rendered a 98.7% best
            // as "9.87%" while the INFO tab's average read correctly, which is what a
            // x10 scale looks like against a client that divides both by 100.
            (uint)Math.Round(progress.HighestAccuracy * 100),     // +101 (% x100)
            (uint)Math.Round(progress.AverageAccuracy * 100),     // +105 (% x100)
            progress.RankingBest7Key,                             // +109
            progress.Cash,                                        // +113
            0,                                                    // +117 unidentified
            progress.FreemodeBest7Key,                            // +121
            0                                                     // +125 unidentified
        ];
    }
}

/// <summary>
/// A stand-in account that exists only on this server, so that features keyed on another
/// user have somebody to be keyed on. Everything here feeds the 67-byte user record the
/// client caches; see <see cref="LobbyUserIdentity"/> for what each field drives.
/// </summary>
public sealed class RosterUser
{
    /// <summary>Must be non-zero and must not collide with the real player's id.</summary>
    public uint UserId { get; set; }

    public string AccountId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;

    /// <summary>1 = male, 0 = female (record+62).</summary>
    public byte Gender { get; set; } = 1;

    /// <summary>Zero-based icon id; sent one-based because sub_4339F0 decrements it.</summary>
    public uint? IconId { get; set; }

    public uint Level { get; set; } = 1;

    /// <summary>Drives record+56: 0 normal, 1 premium, 8 PC-bang, 9 both.</summary>
    public uint AccountClass { get; set; }

    /// <summary>
    /// Whether the user is reported present. Only <see cref="MessengerPresence.Online"/>
    /// renders as online, so this is a bool rather than a raw word.
    /// </summary>
    public bool Online { get; set; } = true;

    public void Validate(uint localUserId)
    {
        if (UserId == 0)
        {
            throw new InvalidDataException("A roster user id must be non-zero.");
        }
        if (UserId == localUserId)
        {
            throw new InvalidDataException(
                $"Roster user id {UserId} collides with the local player.");
        }
        LocalPlayerProfile.ValidateAscii(
            AccountId, OnUserIdInfoInfPacket.AccountIdSize, nameof(AccountId), true);
        LocalPlayerProfile.ValidateAscii(
            Nickname, OnUserIdInfoInfPacket.NicknameSize, nameof(Nickname), false);
        if (IconId is uint iconId)
        {
            _ = LocalPlayerProfile.EncodeIconId(iconId);
        }
    }

    public LobbyUserIdentity ToIdentity() => new(
        UserId,
        AccountId,
        Nickname,
        Unknown52: 0,
        LobbyUserIdentity.TierFor(AccountClass),
        Level,
        Gender,
        IconId is uint iconId
            ? LocalPlayerProfile.EncodeIconId(iconId)
            : ProtocolPadding.UnusedUInt32);
}

/// <summary>
/// One saved messenger contact. Mirrors the eight-byte record the client keeps
/// (OnMsgRegUserInf: userId u32, group u16, status u16).
/// </summary>
public sealed record MessengerContactEntry
{
    /// <summary>
    /// The contact's CURRENT wire id. Session ids are randomised per login, so this is
    /// refreshed from <see cref="AccountId"/> every time the book is built - it is a cache,
    /// not the identity.
    /// </summary>
    public uint UserId { get; set; }

    /// <summary>
    /// Stable identity of the contact. User ids change every login, so a friends list
    /// saved by id alone would point at nobody (or worse, somebody else) next session.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    public ushort GroupIndex { get; set; }
}

/// <summary>
/// The persisted friends list. The client holds 60 contact slots, 60 blocked ids and
/// 10 group names; anything beyond those bounds cannot be shown, so the store enforces
/// them rather than letting a builder throw at send time.
/// </summary>
public sealed class MessengerBook
{
    public List<MessengerContactEntry> Contacts { get; set; } = [];
    public List<uint> BlockedUserIds { get; set; } = [];
    public List<string> GroupNames { get; set; } = [];

    public void Validate()
    {
        if (Contacts == null || BlockedUserIds == null || GroupNames == null)
        {
            throw new InvalidDataException("Messenger sections cannot be null.");
        }
        if (Contacts.Count > OnMessengerInfoInfPacket.ContactCount)
        {
            throw new InvalidDataException(
                $"Contacts cannot exceed {OnMessengerInfoInfPacket.ContactCount}.");
        }
        if (BlockedUserIds.Count > OnMsgBlkUserInfPacket.RecordCount)
        {
            throw new InvalidDataException(
                $"Blocked users cannot exceed {OnMsgBlkUserInfPacket.RecordCount}.");
        }
        if (GroupNames.Count > OnMessengerInfoInfPacket.GroupCount)
        {
            throw new InvalidDataException(
                $"Group names cannot exceed {OnMessengerInfoInfPacket.GroupCount}.");
        }
        if (Contacts.Select(contact => contact.UserId).Distinct().Count() != Contacts.Count)
        {
            throw new InvalidDataException("Contacts cannot list a user twice.");
        }
    }

    /// <summary>Pads the contacts out to the client's fixed 60 slots.</summary>
    public MessengerContact[] ContactSlots()
    {
        MessengerContact[] slots = new MessengerContact[
            OnMessengerInfoInfPacket.ContactCount];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = i < Contacts.Count
                ? new MessengerContact(Contacts[i].UserId, Contacts[i].GroupIndex, 0)
                : new MessengerContact(0, 0, 0);
        }
        return slots;
    }

    /// <summary>
    /// Pads the blocked list to the 30 eight-byte records sub_4378C0 indexes at
    /// net+893943 - the same record shape as the contact list, not packed user ids.
    /// </summary>
    public MessengerContact[] BlockedSlots()
    {
        MessengerContact[] slots = new MessengerContact[OnMsgBlkUserInfPacket.RecordCount];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = i < BlockedUserIds.Count
                ? new MessengerContact(BlockedUserIds[i], 0, 0)
                : new MessengerContact(0, 0, 0);
        }
        return slots;
    }

    public string[] GroupSlots()
    {
        string[] slots = new string[OnMessengerInfoInfPacket.GroupCount];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = i < GroupNames.Count ? GroupNames[i] ?? string.Empty : string.Empty;
        }
        return slots;
    }
}

public sealed record CollectionEntry(ushort Code, ushort Value);

/// <summary>
/// A stored Course Club result. Score is the ranking board's "%8d" column and Clears its
/// "%5d" column; both render as a dash row in the client when zero.
/// </summary>
public sealed record CourseRecord
{
    public ushort CourseId { get; set; }

    /// <summary>
    /// Which channel's charts this record belongs to (5 = SEOUL, 7 = TOKYO). SEOUL and
    /// TOKYO serve entirely different charts for the same course, so a single record per
    /// course would let a 7-key run overwrite a 5-key best and put both on one board.
    /// </summary>
    public byte KeyMode { get; set; } = (byte)SongKeyMode.FiveKey;

    /// <summary>Best course score; the board's "%8d" column.</summary>
    public uint Score { get; set; }

    /// <summary>Best combo; the board's "%5d" column, NOT the clear count.</summary>
    public uint Combo { get; set; }

    /// <summary>How many times the course was cleared. Server-side bookkeeping only.</summary>
    public uint Clears { get; set; }
}

public sealed record InventoryItemSlot(int Slot, uint ItemId);

public sealed record TimedInventorySlot(int Slot, uint ItemId, uint Expiration);

public sealed record PresentInventorySlot(
    int Slot,
    uint ItemId,
    uint SenderUserId,
    uint Expiration);

/// <summary>
/// Editable sparse inventory. Omitted slots are serialized to the retail
/// unused value; listed slots can address every entry in every inventory area.
/// </summary>
public sealed class LocalPlayerInventory
{
    public List<InventoryItemSlot> DefaultItems { get; set; } = [];
    public List<InventoryItemSlot> EventItems { get; set; } = [];
    public List<TimedInventorySlot> ShopItems { get; set; } = [];
    public List<PresentInventorySlot> PresentItems { get; set; } = [];
    public List<TimedInventorySlot> MountItems { get; set; } = [];
    public uint State { get; set; }

    /// <summary>
    /// Explicit owned-item ids for the Korean item box (소지품), up to 30. When set,
    /// this drives the item box directly (real wItem ids from ItemStock.csv); when
    /// empty it falls back to the section lists above. Ids are the ItemStock wItem
    /// values, e.g. 769/770 boosters, 1025+ avatars, 9217+ note gear.
    /// </summary>
    public List<uint> ItemBox { get; set; } = [];

    /// <summary>
    /// Second u32 for each Korean item-box slot. Zero is permanent; timed items use
    /// an expiry value. It is parallel to ItemBox and may be shorter for old profiles.
    /// </summary>
    public List<uint> ItemBoxValues { get; set; } = [];

    /// <summary>
    /// Consumable boosters (ItemStock section 5) the player has used and that apply to
    /// their NEXT song: HP, EXP, MAX and HP_RECOVERY. Using one moves it out of the item
    /// box and into here; finishing a song spends it. Nothing in the client tracks this -
    /// the boosters' whole effect is server-side (see <see cref="EquipmentBonus"/>) - so
    /// without this list a used booster simply vanished and did nothing.
    /// </summary>
    public List<uint> ActiveBoosters { get; set; } = [];

    public void SetDefaultItem(int slot, uint itemId) =>
        SetSlot(DefaultItems, new InventoryItemSlot(slot, itemId));

    public void SetEventItem(int slot, uint itemId) =>
        SetSlot(EventItems, new InventoryItemSlot(slot, itemId));

    public void SetShopItem(int slot, uint itemId, uint expiration) =>
        SetSlot(ShopItems, new TimedInventorySlot(slot, itemId, expiration));

    public void SetPresentItem(
        int slot,
        uint itemId,
        uint senderUserId,
        uint expiration) =>
        SetSlot(PresentItems, new PresentInventorySlot(
            slot, itemId, senderUserId, expiration));

    public void SetMountItem(int slot, uint itemId, uint expiration) =>
        SetSlot(MountItems, new TimedInventorySlot(slot, itemId, expiration));

    /// <summary>
    /// Flat list of owned item ids for the Korean item box (소지품). The equipment
    /// scene (sub_47448C) reads a 30-slot array at net+893035; each slot is an item
    /// id whose type/render is derived by the client (sub_42954F). This is exactly
    /// the ItemBox list — it deliberately does NOT fall back to the China DefaultItems/
    /// section lists, which would inject unwanted "default items" whenever the box is
    /// empty and make a purchase appear to wipe the box down to the one bought item.
    /// </summary>
    public IReadOnlyList<uint> ItemBoxItemIds() => ItemBox;

    public IReadOnlyList<TimedInventoryItem> ItemBoxItems() =>
        ItemBox.Select((itemId, index) => new TimedInventoryItem(
            itemId,
            index < ItemBoxValues.Count ? ItemBoxValues[index] : 0)).ToArray();

    public void SetItemBox(IEnumerable<TimedInventoryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        TimedInventoryItem[] entries = items.ToArray();
        if (entries.Length > OnPurchaseItemAckPacket.ItemBoxSlots)
        {
            throw new InvalidDataException(
                $"ItemBox cannot contain more than {OnPurchaseItemAckPacket.ItemBoxSlots} items.");
        }
        ItemBox = entries.Select(item => item.ItemId).ToList();
        ItemBoxValues = entries.Select(item => item.Expiration).ToList();
    }

    /// <summary>
    /// The 64-byte equipped mount loadout (8 slots × {itemId:u32, expiration:u32}),
    /// empty slots 0xFF-filled. This is what MountItemReq sends and OnMountItemAck
    /// echoes; sending it in the login block (net+893395) restores equipped gear at
    /// login.
    /// </summary>
    public byte[] MountLoadout()
    {
        const int slots = 8;
        byte[] loadout = new byte[slots * 8];
        loadout.AsSpan().Fill(0xFF);
        foreach (TimedInventorySlot item in MountItems)
        {
            if (item.Slot >= 0 && item.Slot < slots)
            {
                int off = item.Slot * 8;
                BinaryPrimitives.WriteUInt32LittleEndian(loadout.AsSpan(off), item.ItemId);
                BinaryPrimitives.WriteUInt32LittleEndian(loadout.AsSpan(off + 4), item.Expiration);
            }
        }
        return loadout;
    }

    public InventorySnapshot CreateSnapshot()
    {
        Validate();
        uint[] defaultItems = EmptyItemIds(OnInventoryInfoInfPacket.DefaultItemCount);
        uint[] eventItems = EmptyItemIds(OnInventoryInfoInfPacket.EventItemCount);
        TimedInventoryItem[] shopItems = EmptyTimedItems(
            OnInventoryInfoInfPacket.ShopItemCount);
        PresentInventoryItem[] presentItems = EmptyPresentItems(
            OnInventoryInfoInfPacket.PresentItemCount);
        TimedInventoryItem[] mountItems = EmptyTimedItems(
            OnInventoryInfoInfPacket.MountItemCount);

        foreach (InventoryItemSlot item in DefaultItems)
        {
            defaultItems[item.Slot] = item.ItemId;
        }
        foreach (InventoryItemSlot item in EventItems)
        {
            eventItems[item.Slot] = item.ItemId;
        }
        foreach (TimedInventorySlot item in ShopItems)
        {
            shopItems[item.Slot] = new TimedInventoryItem(item.ItemId, item.Expiration);
        }
        foreach (PresentInventorySlot item in PresentItems)
        {
            presentItems[item.Slot] = new PresentInventoryItem(
                item.ItemId, item.SenderUserId, item.Expiration);
        }
        foreach (TimedInventorySlot item in MountItems)
        {
            mountItems[item.Slot] = new TimedInventoryItem(item.ItemId, item.Expiration);
        }

        return new InventorySnapshot(
            defaultItems,
            eventItems,
            shopItems,
            presentItems,
            mountItems,
            State);
    }

    public void Validate()
    {
        ValidateSlots(DefaultItems, OnInventoryInfoInfPacket.DefaultItemCount, nameof(DefaultItems));
        ValidateSlots(EventItems, OnInventoryInfoInfPacket.EventItemCount, nameof(EventItems));
        ValidateSlots(ShopItems, OnInventoryInfoInfPacket.ShopItemCount, nameof(ShopItems));
        ValidateSlots(PresentItems, OnInventoryInfoInfPacket.PresentItemCount, nameof(PresentItems));
        ValidateSlots(MountItems, OnInventoryInfoInfPacket.MountItemCount, nameof(MountItems));
        if (ItemBox == null)
        {
            throw new InvalidDataException("Inventory section ItemBox cannot be null.");
        }
        if (ItemBoxValues == null)
        {
            throw new InvalidDataException("Inventory section ItemBoxValues cannot be null.");
        }
        if (ItemBox.Count > OnPurchaseItemAckPacket.ItemBoxSlots)
        {
            throw new InvalidDataException(
                $"ItemBox cannot contain more than {OnPurchaseItemAckPacket.ItemBoxSlots} items.");
        }
        if (ItemBoxValues.Count > ItemBox.Count)
        {
            throw new InvalidDataException(
                "ItemBoxValues cannot contain more entries than ItemBox.");
        }
        if (ItemBox.Any(itemId => itemId == 0 || (ushort)itemId == ushort.MaxValue))
        {
            throw new InvalidDataException(
                "ItemBox contains a zero or protocol-sentinel item id.");
        }
    }

    private static void SetSlot<T>(List<T> items, T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        int slot = value switch
        {
            InventoryItemSlot item => item.Slot,
            TimedInventorySlot item => item.Slot,
            PresentInventorySlot item => item.Slot,
            _ => throw new ArgumentException("Inventory entry has no slot.", nameof(value))
        };
        int existing = items.FindIndex(item => GetSlot(item) == slot);
        if (existing >= 0)
        {
            items[existing] = value;
        }
        else
        {
            items.Add(value);
        }
    }

    private static void ValidateSlots<T>(
        IReadOnlyCollection<T>? items,
        int slotCount,
        string name)
    {
        if (items == null)
        {
            throw new InvalidDataException($"Inventory section {name} cannot be null.");
        }

        HashSet<int> used = [];
        foreach (T item in items)
        {
            if (item == null)
            {
                throw new InvalidDataException($"Inventory section {name} contains a null entry.");
            }

            int slot = GetSlot(item);
            if (slot < 0 || slot >= slotCount)
            {
                throw new InvalidDataException(
                    $"Inventory section {name} slot {slot} is outside 0..{slotCount - 1}.");
            }
            if (!used.Add(slot))
            {
                throw new InvalidDataException(
                    $"Inventory section {name} contains slot {slot} more than once.");
            }
        }
    }

    private static int GetSlot<T>(T value) => value switch
    {
        InventoryItemSlot item => item.Slot,
        TimedInventorySlot item => item.Slot,
        PresentInventorySlot item => item.Slot,
        _ => throw new InvalidDataException("Unknown inventory entry type.")
    };

    private static uint[] EmptyItemIds(int count) =>
        Enumerable.Repeat(ProtocolPadding.UnusedUInt32, count).ToArray();

    private static TimedInventoryItem[] EmptyTimedItems(int count) =>
        Enumerable.Repeat(TimedInventoryItem.Empty, count).ToArray();

    private static PresentInventoryItem[] EmptyPresentItems(int count) =>
        Enumerable.Repeat(PresentInventoryItem.Empty, count).ToArray();
}

/// <summary>Reads and writes the editable player.json file used by the CLI.</summary>
public static class LocalPlayerProfileFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static LocalPlayerProfile LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            LocalPlayerProfile profile = LocalPlayerProfile.Default;
            Save(path, profile);
            return profile;
        }

        return Deserialize(File.ReadAllText(path));
    }

    public static void Save(string path, LocalPlayerProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string fullPath = Path.GetFullPath(path);
        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, Serialize(profile));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string Serialize(LocalPlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return JsonSerializer.Serialize(profile, JsonOptions) + Environment.NewLine;
    }

    public static LocalPlayerProfile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        LocalPlayerProfile profile = JsonSerializer.Deserialize<LocalPlayerProfile>(
            json, JsonOptions) ?? throw new InvalidDataException("Player profile is empty.");
        profile.Validate();
        return profile;
    }
}
