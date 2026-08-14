using Arrowgene.Buffers;
using Arrowgene.DJMaxOnline.Server.Protocol;
using Arrowgene.Logging;
using System.Buffers.Binary;

namespace Arrowgene.DJMaxOnline.Server;

public class PacketFactory
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(PacketFactory));

    private const int PacketHeaderSize = DjMaxPacketBuilder.HeaderSize;
    private const int PacketIdSize = DjMaxPacketBuilder.PacketIdSize;

    private readonly IBuffer _buffer;
    private bool _readPacketId;
    private bool _readDynamicHeader;
    private byte[]? _dynamicHeader;
    private int _dataSize;
    private DjMaxCrypto? _crypto;
    private PacketMeta _packetMeta;

    public PacketFactory()
    {
        _readPacketId = false;
        _readDynamicHeader = false;
        _dynamicHeader = null;
        _dataSize = 0;
        _buffer = new StreamBuffer();
        _crypto = null;
        _packetMeta = null!;
    }

    public void InitCrypto(DjMaxCrypto crypto)
    {
        _crypto = crypto;
    }

    public int BufferedByteCount => _buffer.Size - _buffer.Position;

    public bool HasPendingPacket => _readPacketId || BufferedByteCount != 0;

    public byte[] Write(Packet packet)
    {
        byte[] packetData = packet.GetDataCopy();

        if (_crypto != null && packet.Header != null)
        {
            Span<byte> packetDataView = packetData;
            _crypto.Encrypt(ref packetDataView);
            packet.Encrypted = packetDataView.ToArray();
        }
        
        IBuffer buffer = new StreamBuffer();
        buffer.WriteUInt16((ushort)packet.Id);
        if (packet.Header != null)
        {
            buffer.WriteBytes(packet.Header);
        }

        buffer.WriteBytes(packetData);
        byte[] b = buffer.GetAllBytes();
        if (!packet.Meta.IsDynamicSize && b.Length != packet.Meta.Size)
        {
            Logger.Error(
                $"Packet Size mismatch. Expected: {packet.Meta.Size}, actual: {b.Length}.{Environment.NewLine}" +
                $"PacketMeta:{packet.Meta.ToLog()} Raw:{Environment.NewLine}" +
                Util.HexDump(b)
            );
        }

        return b;
    }

    public void FillReadBuffer(byte[] data)
    {
        if (_buffer.Position == _buffer.Size)
        {
            _buffer.SetPositionStart();
            _buffer.SetSize(0);
            _buffer.WriteBytes(data);
            _buffer.SetPositionStart();
        }
        else
        {
            int pos = _buffer.Position;
            _buffer.SetPositionEnd();
            _buffer.WriteBytes(data);
            _buffer.Position = pos;
        }
    }

    public Packet? ReadPacket()
    {
        if (!_readPacketId && _buffer.Size - _buffer.Position >= PacketIdSize)
        {
            ushort packetIdNum = _buffer.ReadUInt16();

            if (!Enum.IsDefined(typeof(PacketId), packetIdNum))
            {
                // TODO err
                Logger.Error($"packetIdNum: {packetIdNum}(0x{packetIdNum:X}) is not a defined PacketId");
            }

            PacketId packetId = (PacketId)packetIdNum;

            if (!PacketMeta.TryGet(packetId, out _packetMeta))
            {
                string reverseEngineeringContext =
                    ReceivePacketCatalog.TryGet(packetId, out ReceivePacketDefinition receive)
                        ? $" sub_42FB20 dispatches it to 0x{receive.HandlerAddress:X}, " +
                          "but its wire framing is still unresolved."
                        : string.Empty;
                throw new InvalidDataException(
                    $"PacketMeta not defined for packetId: {packetId}(0x{packetIdNum:X})." +
                    reverseEngineeringContext);
            }

            if (_packetMeta.IsDynamicSize)
            {
                _dataSize = PacketHeaderSize;
                _readDynamicHeader = true;
            }
            else
            {
                _dataSize = _packetMeta.Size - PacketIdSize;
                _readDynamicHeader = false;
            }
            _readPacketId = true;
        }

        if (_readPacketId && _readDynamicHeader &&
            _buffer.Size - _buffer.Position >= PacketHeaderSize)
        {
            _dynamicHeader = _buffer.ReadBytes(PacketHeaderSize);
            uint totalWireSize = BinaryPrimitives.ReadUInt32LittleEndian(_dynamicHeader.AsSpan(1));
            if (totalWireSize < PacketIdSize + PacketHeaderSize)
            {
                throw new InvalidDataException(
                    $"Invalid dynamic packet size {totalWireSize} for {_packetMeta.Id}");
            }

            _dataSize = checked((int)totalWireSize - PacketIdSize - PacketHeaderSize);
            _readDynamicHeader = false;
        }

        if (_readPacketId && !_readDynamicHeader &&
            _buffer.Size - _buffer.Position >= _dataSize)
        {
            byte[]? header;
            if (_dynamicHeader != null)
            {
                header = _dynamicHeader;
                _dynamicHeader = null;
            }
            else if (_dataSize >= PacketHeaderSize)
            {
                header = _buffer.ReadBytes(PacketHeaderSize);
                _dataSize -= PacketHeaderSize;
            }
            else
            {
                header = null;
            }

            byte[] packetData = _buffer.ReadBytes(_dataSize);
            byte[] encrypted = new byte[packetData.Length];
            packetData.CopyTo(encrypted, 0);
            if (_crypto != null && header != null)
            {
                Span<byte> packetDataView = packetData;
                _crypto.Decrypt(ref packetDataView);

            }

            Packet packet = new Packet(_packetMeta, packetData);
            packet.Encrypted = encrypted;
            if (header != null)
            {
                packet.Header = header;
            }

            _readPacketId = false;
            return packet;
        }

        return null;
    }

    public List<Packet> ReadPackets()
    {
        List<Packet> packets = new List<Packet>();
        while (true)
        {
            Packet? p = ReadPacket();
            if (p == null)
            {
                break;
            }

            packets.Add(p);
        }

        return packets;
    }
}
