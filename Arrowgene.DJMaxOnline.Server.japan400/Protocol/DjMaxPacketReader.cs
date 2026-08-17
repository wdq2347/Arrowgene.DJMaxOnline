using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

/// <summary>Sequential reader for the raw structure beginning at wire offset 3.</summary>
public sealed class DjMaxPacketReader
{
    private readonly byte[] _body;
    private int _position;

    public DjMaxPacketReader(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // The two directions split the body differently, so both have to be accepted.
        // DjMaxPacketBuilder puts the first five body bytes in Header and the rest in
        // Data, keeping the whole body in Header when it is shorter than that. The
        // receive path in PacketFactory does the opposite for those short packets: it
        // leaves Header null and hands the entire body over in Data. Concatenating the
        // two always yields the body as it appears on the wire after the packet id.
        byte[] header = packet.Header ?? [];
        if (header.Length > DjMaxPacketBuilder.HeaderSize)
        {
            throw new ArgumentException(
                $"{packet.Id} has a {header.Length}-byte header; the protocol allows at " +
                $"most {DjMaxPacketBuilder.HeaderSize}.", nameof(packet));
        }

        _body = new byte[header.Length + packet.Data.Length];
        header.CopyTo(_body, 0);
        packet.Data.CopyTo(_body, header.Length);
        if (_body.Length == 0)
        {
            throw new ArgumentException($"{packet.Id} has no body.", nameof(packet));
        }
        _position = DjMaxPacketBuilder.StructuredPayloadOffset - DjMaxPacketBuilder.PacketIdSize;
    }

    public byte Control => _body[0];
    public int Remaining => _body.Length - _position;

    public byte ReadByte()
    {
        EnsureAvailable(sizeof(byte));
        return _body[_position++];
    }

    public short ReadInt16()
    {
        EnsureAvailable(sizeof(short));
        short value = BinaryPrimitives.ReadInt16LittleEndian(_body.AsSpan(_position));
        _position += sizeof(short);
        return value;
    }

    public ushort ReadUInt16()
    {
        EnsureAvailable(sizeof(ushort));
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_body.AsSpan(_position));
        _position += sizeof(ushort);
        return value;
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(sizeof(uint));
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_body.AsSpan(_position));
        _position += sizeof(uint);
        return value;
    }

    public byte[] ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        EnsureAvailable(count);
        byte[] value = _body.AsSpan(_position, count).ToArray();
        _position += count;
        return value;
    }

    public string ReadFixedAscii(int fieldSize)
    {
        byte[] field = ReadBytes(fieldSize);
        int terminator = Array.IndexOf(field, (byte)0);
        int length = terminator >= 0 ? terminator : field.Length;
        return Encoding.ASCII.GetString(field, 0, length);
    }

    public IPAddress ReadIPv4() => new(ReadBytes(4));

    public void Skip(int count) => ReadBytes(count);

    public void EnsureComplete()
    {
        // The read-side mirror of DjMaxPacketBuilder.Build's tail fill.
        //
        // This client carries an 8-byte reserved tail Korea dropped, on what it SENDS as
        // well as what it receives, so a reader written against the Korean layout stops
        // exactly one tail short. Tolerating it here rather than in every reader keeps the
        // one framing rule in one place, and matches the builder, which fills exactly a
        // tail and no other shortfall.
        //
        // ONLY an exact tail is allowed. Any other leftover is a real layout error - a
        // field that was forgotten, or the wrong size - and still throws, so this cannot
        // paper over a wrong packet. LogInReq left 10, not 8, and that was a genuine
        // difference (a 32-byte seed plus an 8-byte token, not a tail).
        if (Remaining == DjMaxPacketBuilder.ReservedTailSize)
        {
            Skip(DjMaxPacketBuilder.ReservedTailSize);
            return;
        }

        if (Remaining != 0)
        {
            throw new InvalidDataException($"Packet has {Remaining} unread structured bytes.");
        }
    }

    private void EnsureAvailable(int count)
    {
        if (count > Remaining)
        {
            throw new InvalidDataException(
                $"Packet contains {Remaining} bytes, but {count} were requested.");
        }
    }
}
