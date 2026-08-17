using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

public static class OnCourseListInfPacket
{
    private const int DynamicHeaderWireSize =
        DjMaxPacketBuilder.PacketIdSize + DjMaxPacketBuilder.HeaderSize;

    public static Packet Build(
        IReadOnlyCollection<ushort> courseIds,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(courseIds);
        int wireSize = checked(DynamicHeaderWireSize + courseIds.Count * sizeof(ushort));
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Dynamic(
            PacketMeta.OnCourseListInf, wireSize, control);
        foreach (ushort courseId in courseIds)
        {
            builder.WriteUInt16(courseId);
        }
        return builder.Build();
    }

    public static ushort[] Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        if ((reader.Remaining & 1) != 0)
        {
            throw new InvalidDataException(
                "Course list contains a partial two-byte course ID.");
        }

        uint actualWireSize = checked((uint)(DynamicHeaderWireSize + reader.Remaining));
        if (declaredWireSize != actualWireSize)
        {
            throw new InvalidDataException(
                $"Course list declares {declaredWireSize} bytes, actual is {actualWireSize}.");
        }

        ushort[] courseIds = new ushort[reader.Remaining / sizeof(ushort)];
        for (int i = 0; i < courseIds.Length; i++)
        {
            courseIds[i] = reader.ReadUInt16();
        }
        reader.EnsureComplete();
        return courseIds;
    }
}

/// <summary>
/// A course-mode request carrying a single 16-bit selector.
/// </summary>
public sealed record CourseSelection(ushort CourseId);

/// <summary>
/// The four JP course requests. Their sizes come from the client's own senders:
///   0x83 rank     sub_433590 -> 13 bytes, courseId u16@3
///   0x85 change   sub_433650 -> 13 bytes, courseId u16@3
///   0x87 continue sub_433710 -> 13 bytes, value    u16@3
///   0x8A postItem sub_433820 -> 11 bytes, no body
/// Each sender checks a pending flag first and will not send again until the matching
/// ack clears it, so leaving any of them unanswered blocks that request permanently.
/// </summary>
public static class CourseSelectionReqPacket
{
    public static CourseSelection Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        CourseSelection request = new(reader.ReadUInt16());
        reader.EnsureComplete();
        return request;
    }
}

public static class PostCourseItemReqPacket
{
    /// <summary>An 11-byte tail-padded signal with no structured body.</summary>
    public static void Parse(Packet packet) => ArgumentNullException.ThrowIfNull(packet);
}

/// <summary>
/// One row of the Course Club ranking board, 43 bytes on the wire.
///
/// Layout proven by the rank scene's two consumers, sub_487FC1 (list render) and the
/// copy-in at sub_488524. The client memcpy's 0x866 bytes from wire offset 5 into a
/// 2160-byte cache slot at +8, then walks it in 43-byte strides while `v8 &lt; 2150`,
/// so the board is exactly 50 rows:
///   +0  u32      user id  - compared against the local id to highlight the own row
///   +4  char[27] nickname - printed with "%s"; an empty string renders "----------"
///   +31 u32      score    - printed with "%8d"; zero renders "--------"
///   +35 u32      combo    - printed with "%5d"; zero renders "-----"
///   +39 u32      trailing dword no course-scene code reads
/// </summary>
public sealed record CourseRankEntry(
    uint UserId,
    string Nickname,
    uint Score,
    uint Combo)
{
    public static readonly CourseRankEntry Empty = new(0, string.Empty, 0, 0);
}

/// <summary>
/// Course ranking table (0x84, 2155 wire bytes). The client discards the two body bytes
/// at wire offset 3 - its copy starts at offset 5 - so only the 50-row table is read.
/// The 0x84 ack also clears the pending flag at net+895196; without it the client never
/// issues another CourseRankReq.
/// </summary>
public static class OnCourseRankAckPacket
{
    /// <summary>Rows the client walks; fixed by its `while (offset &lt; 2150)` loop.</summary>
    public const int EntryCount = 50;

    /// <summary>Stride of one row, from the client's `imul 43` indexing.</summary>
    public const int EntrySize = 43;

    /// <summary>Nickname field width including its terminator.</summary>
    public const int NicknameSize = 27;

    /// <summary>Body bytes ahead of the table that the client's copy skips.</summary>
    private const int LeadingIgnoredSize = 2;

    public static Packet Build(
        ushort courseId,
        IReadOnlyList<CourseRankEntry> entries,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > EntryCount)
        {
            throw new ArgumentException(
                $"A course ranking holds at most {EntryCount} rows, got {entries.Count}.",
                nameof(entries));
        }

        // Written for readability only: the client's memcpy starts past these two bytes.
        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Fixed(PacketMeta.OnCourseRankAck, control)
            .WriteUInt16(courseId);
        foreach (CourseRankEntry entry in entries)
        {
            builder
                .WriteUInt32(entry.UserId)
                .WriteFixedAscii(entry.Nickname, NicknameSize)
                .WriteUInt32(entry.Score)
                .WriteUInt32(entry.Combo)
                .WritePadding(sizeof(uint));
        }

        return builder
            .WritePadding((EntryCount - entries.Count) * EntrySize)
            .Build();
    }

    /// <summary>The body size this packet's meta must provide for the layout to fit.</summary>
    public static int BodySize =>
        LeadingIgnoredSize + EntryCount * EntrySize;
}

/// <summary>
/// Course selection ack (0x86, 5 bytes): course id at wire offset 3.
///
/// Echoing the requested id is mandatory, not cosmetic. sub_4914A3 stores this word into
/// the course scene's "current course" field (scene+892), and sub_49215D re-sends
/// ChangeCourseReq on every tick while the highlighted course differs from it - so a zero
/// or wrong echo puts the client in a permanent request loop.
/// </summary>
public static class OnChangeCourseAckPacket
{
    public static Packet Build(
        ushort courseId,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnChangeCourseAck, control)
            .WriteUInt16(courseId)
            .Build();
}

/// <summary>
/// Course continue ack (0x88, 5 bytes). No scene inspects the body - id 136 appears
/// nowhere in the client outside its size-table registration - so the packet's only
/// effect is clearing the pending flag at net+895204 in sub_432B40, which the client
/// needs before it will send ContinueCourseReq again. The requested value is echoed
/// so a capture reads sensibly.
/// </summary>
public static class OnContinueCourseAckPacket
{
    public static Packet Build(
        ushort value,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnContinueCourseAck, control)
            .WriteUInt16(value)
            .Build();
}

/// <summary>
/// Course reward hand-off (0x89, 11 wire bytes).
///
/// The Korean client's handler sub_432B90 reads two u32 values at wire offsets 3 and 7.
/// It stores the first at net+0xDA8F4, whose low word sub_494FFE resolves and draws as the
/// random item on the total-result screen. The second is stored at net+0xDA8F8. These are
/// the same packed item-id/expiration pair used by the inventory protocol.
/// </summary>
public static class OnPostCourseItemReqPacket
{
    public static Packet Build(
        uint itemId,
        uint expiration,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnPostCourseItemReq, control)
            .WriteUInt32(itemId)
            .WriteUInt32(expiration)
            .Build();
}

/// <summary>
/// Award-item inventory notice (0x8C, 7 wire bytes). The client handler sub_432C90 passes
/// its u32 body to the inventory updater; the course result icon itself comes from 0x89.
/// </summary>
public static class OnAwardItemInfPacket
{
    public static Packet Build(
        uint itemId = 0,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnAwardItemInf, control)
            .WriteUInt32(itemId)
            .Build();
}
