using System.Buffers.Binary;
using System.Text;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

/// <summary>
/// Builds the logical packet buffer consumed by the original client. The client
/// begins most structures at raw wire offset 3, so a field is allowed to cross
/// from the five-byte clear header into the encrypted data without callers
/// having to split it by hand.
/// </summary>
public sealed class DjMaxPacketBuilder
{
    public const int PacketIdSize = 2;
    public const int HeaderSize = 5;
    public const int StructuredPayloadOffset = 3;

    private readonly PacketMeta _meta;
    private readonly byte[] _body;
    private int _position;

    private DjMaxPacketBuilder(PacketMeta meta, int wireSize, byte control)
    {
        // Most packets carry the full 5-byte clear header, but the Korean protocol also
        // has sub-header-size packets (e.g. OnStartInf=6, OnJoinEventInf=5) that are just
        // id + control + a couple of clear bytes. The minimum is id + control byte.
        if (wireSize < PacketIdSize + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(wireSize),
                "Packets require at least an id and a control byte.");
        }

        _meta = meta;
        _body = new byte[wireSize - PacketIdSize];
        _body[0] = control;
        _position = StructuredPayloadOffset - PacketIdSize;
    }

    public static DjMaxPacketBuilder Fixed(PacketMeta meta, byte control)
    {
        if (meta.IsDynamicSize)
        {
            throw new ArgumentException($"{meta.Name} has dynamic framing.", nameof(meta));
        }

        return new DjMaxPacketBuilder(meta, meta.Size, control);
    }

    public static DjMaxPacketBuilder Dynamic(PacketMeta meta, int wireSize, byte control)
    {
        if (!meta.IsDynamicSize)
        {
            throw new ArgumentException($"{meta.Name} does not have dynamic framing.", nameof(meta));
        }

        DjMaxPacketBuilder builder = new(meta, wireSize, control);
        builder.WriteUInt32((uint)wireSize);
        return builder;
    }

    public DjMaxPacketBuilder WriteByte(byte value)
    {
        EnsureAvailable(sizeof(byte));
        _body[_position++] = value;
        return this;
    }

    public DjMaxPacketBuilder WriteInt16(short value)
    {
        EnsureAvailable(sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(_body.AsSpan(_position), value);
        _position += sizeof(short);
        return this;
    }

    public DjMaxPacketBuilder WriteUInt16(ushort value)
    {
        EnsureAvailable(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(_body.AsSpan(_position), value);
        _position += sizeof(ushort);
        return this;
    }

    public DjMaxPacketBuilder WriteUInt32(uint value)
    {
        EnsureAvailable(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(_body.AsSpan(_position), value);
        _position += sizeof(uint);
        return this;
    }

    public DjMaxPacketBuilder WriteUInt32Array(IReadOnlyList<uint> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (uint value in values)
        {
            WriteUInt32(value);
        }
        return this;
    }

    public DjMaxPacketBuilder WriteBytes(ReadOnlySpan<byte> value)
    {
        EnsureAvailable(value.Length);
        value.CopyTo(_body.AsSpan(_position));
        _position += value.Length;
        return this;
    }

    public DjMaxPacketBuilder WriteFixedAscii(
        string value,
        int fieldSize,
        byte paddingValue = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldSize);
        byte[] encoded = Encoding.ASCII.GetBytes(value ?? string.Empty);
        if (encoded.Length >= fieldSize)
        {
            throw new ArgumentException(
                $"ASCII value requires {encoded.Length + 1} bytes including its terminator, " +
                $"but the field is {fieldSize} bytes.", nameof(value));
        }

        WriteBytes(encoded);
        WriteByte(0);
        return WritePadding(fieldSize - encoded.Length - 1, paddingValue);
    }

    public DjMaxPacketBuilder WritePadding(int count, byte value = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        EnsureAvailable(count);
        _body.AsSpan(_position, count).Fill(value);
        _position += count;
        return this;
    }

    public Packet Build()
    {
        if (_position != _body.Length)
        {
            throw new InvalidOperationException(
                $"{_meta.Name} is incomplete: wrote {_position + PacketIdSize} of " +
                $"{_body.Length + PacketIdSize} wire bytes.");
        }

        // Packets shorter than id + 5-byte header keep their whole body in the header
        // (no separate data section), matching how the client frames small clear packets.
        int headerLength = Math.Min(HeaderSize, _body.Length);
        Packet packet = new Packet(_meta, _body.AsSpan(headerLength).ToArray())
        {
            Header = _body.AsSpan(0, headerLength).ToArray()
        };
        return packet;
    }

    private void EnsureAvailable(int count)
    {
        if (count > _body.Length - _position)
        {
            throw new InvalidOperationException(
                $"Writing {count} bytes would exceed {_meta.Name}'s wire size.");
        }
    }
}
