using System.Buffers.Binary;

namespace Arrowgene.DJMaxOnline.Server.China260;

/// <summary>
/// Structural sanity check for a decompressed PTFF chart, replicating how the
/// DJMax Online client's loader (<c>sub_4BF130</c>) walks the file:
///
///   [24-byte header] [voiceCount × 66-byte voice records] [trackCount tracks]
///
/// where <c>trackCount = u16[12]</c>, <c>voiceCount = u16[22]</c>, and each track
/// is a 78-byte header followed by <c>u32[hdr+74]</c> bytes of event data
/// (11 bytes per note event). Most charts consume the file exactly to EOF. A small set
/// of retail patterns retains an additional sequence of complete 11-byte events after
/// its declared tracks (Letgo 5K SC is one); that is safe to preserve. A partial or
/// oversized tail still signals a truncated/foreign dump and is refused so it cannot
/// make the client read past its data or misparse the note stream.
/// </summary>
public static class PtffValidator
{
    private const int HeaderSize = 24;
    private const int VoiceRecordSize = 66;
    private const int TrackHeaderSize = 78;
    private const int TrackDataSizeOffset = 74;
    private const int EventSize = 11;
    private const int OnlineVersion = 6;
    private const uint MaxTrackDataSize = 5_000_000;

    public static bool IsValid(ReadOnlySpan<byte> ptff, out string reason)
    {
        if (ptff.Length < HeaderSize ||
            ptff[0] != (byte)'P' || ptff[1] != (byte)'T' ||
            ptff[2] != (byte)'F' || ptff[3] != (byte)'F')
        {
            reason = "missing PTFF magic";
            return false;
        }

        // Byte 0x5 is the version the client validates; only 6 (DJMax Online) parses.
        if (ptff[5] != OnlineVersion)
        {
            reason = $"PTFF version {ptff[5]} is not DJMax Online (6)";
            return false;
        }

        int trackCount = BinaryPrimitives.ReadUInt16LittleEndian(ptff.Slice(12));
        int voiceCount = BinaryPrimitives.ReadUInt16LittleEndian(ptff.Slice(22));

        long offset = HeaderSize + (long)voiceCount * VoiceRecordSize;
        if (offset > ptff.Length)
        {
            reason = $"{voiceCount} voice records overrun the chart";
            return false;
        }

        for (int track = 0; track < trackCount; track++)
        {
            if (offset + TrackHeaderSize > ptff.Length)
            {
                reason = $"track {track}/{trackCount} header is truncated";
                return false;
            }

            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(
                ptff.Slice((int)offset + TrackDataSizeOffset));
            if (dataSize > MaxTrackDataSize || dataSize % EventSize != 0)
            {
                reason = $"track {track} has an invalid event block ({dataSize} bytes)";
                return false;
            }

            offset += TrackHeaderSize + dataSize;
            if (offset > ptff.Length)
            {
                reason = $"track {track} event data overruns the chart";
                return false;
            }
        }

        // Retail dumps can retain full event records after their declared tracks.  Keep
        // those intact (they occur in valid SC/MX charts), but a partial record or an
        // unbounded tail is still corrupt data.
        long leftover = ptff.Length - offset;
        if (leftover > MaxTrackDataSize || leftover % EventSize != 0)
        {
            reason = $"{leftover} invalid trailing bytes after {trackCount} tracks";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
