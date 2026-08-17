using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

public class DjMaxCrypto
{
    private const int SumSeedOffset = 28;

    private class DjMaxCryptoState
    {
        private readonly MersenneTwister _mt;
        private readonly byte[] _rngBuffer;
        private readonly byte[] _sumBuffer;
        private readonly byte[] _clearBuffer;

        public int Idx { get; set; }

        public DjMaxCryptoState(byte[] mtSeed, uint sumSeed)
        {
            Idx = 0;
            _sumBuffer = new byte[8];
            _rngBuffer = new byte[8];
            _clearBuffer = new byte[8];
            _mt = new MersenneTwister(mtSeed);
            NextRngBuffer();
            BinaryPrimitives.WriteUInt32LittleEndian(_sumBuffer, sumSeed);
            BinaryPrimitives.WriteUInt32LittleEndian(_sumBuffer[4..], 0);
        }

        public ReadOnlySpan<byte> NextRngBuffer()
        {
            Span<byte> span = _rngBuffer;
            BinaryPrimitives.WriteUInt32LittleEndian(span, _mt.NextUInt32());
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], _mt.NextUInt32());
            return new ReadOnlySpan<byte>(_rngBuffer);
        }

        public Span<byte> GetSumBuffer()
        {
            return new Span<byte>(_sumBuffer);
        }

        public ReadOnlySpan<byte> GetRngBuffer()
        {
            return new ReadOnlySpan<byte>(_rngBuffer);
        }

        public Span<byte> GetClearBuffer()
        {
            return new Span<byte>(_clearBuffer);
        }
    }

    private DjMaxCryptoState _enc;
    private DjMaxCryptoState _dec;
    private readonly uint _sumSeed;
    private readonly byte[] _mtSeed;

    public DjMaxCrypto(byte[] mtSeed, uint sumSeed)
        : this(mtSeed, sumSeed, requireKoreaSeedSize: true)
    {
    }

    private DjMaxCrypto(byte[] mtSeed, uint sumSeed, bool requireKoreaSeedSize)
    {
        if (requireKoreaSeedSize && mtSeed.Length != OnConnectAckPacket.SeedSize)
        {
            throw new ArgumentException(
                $"Cipher seed must be {OnConnectAckPacket.SeedSize} bytes.", nameof(mtSeed));
        }

        _sumSeed = sumSeed;
        _mtSeed = mtSeed;
        _enc = new DjMaxCryptoState(_mtSeed, _sumSeed);
        _dec = new DjMaxCryptoState(_mtSeed, _sumSeed);
    }

    /// <summary>
    /// Builds the cipher for the Japanese client's OnConnectAck (id 9). Its handler
    /// (client sub_430AB0) copies a 30-byte seed and seeds MT19937 via init_by_array
    /// over the first 7 uint32 words (28 bytes) — sub_44B40C. The XTEA sum seed is
    /// derived per sub_430A10 as (uint16)(word@28 + dword@24). Same MT+XTEA keystream
    /// as the Korea path, only the seed shape differs.
    /// </summary>
    public static DjMaxCrypto InitJapanese(ReadOnlySpan<byte> seed30)
    {
        if (seed30.Length != 30)
        {
            throw new ArgumentException("JP cipher seed must be 30 bytes.", nameof(seed30));
        }

        byte[] mtBytes = seed30[..28].ToArray(); // 7 uint32 for init_by_array
        // Verified by decrypting the client's first encrypted packet (its auth
        // echoes our seed back): the XTEA sum seed is the signed int16 at offset
        // 28 — identical to the Korea DeriveSumSeed, just on the 30-byte layout.
        short signedSum = BinaryPrimitives.ReadInt16LittleEndian(seed30[28..]);
        uint sumSeed = unchecked((uint)signedSum);
        return new DjMaxCrypto(mtBytes, sumSeed, requireKoreaSeedSize: false);
    }

    public static DjMaxCrypto Init()
    {
        byte[] mtSeed = new byte[OnConnectAckPacket.SeedSize];
        Random.Shared.NextBytes(mtSeed);
        return new DjMaxCrypto(mtSeed, DeriveSumSeed(mtSeed));
    }

    /// <summary>
    /// Matches the client's MOVSX of the signed 16-bit value at seed offset 28.
    /// Negative values are intentionally sign-extended before conversion to uint.
    /// </summary>
    public static uint DeriveSumSeed(ReadOnlySpan<byte> mtSeed)
    {
        if (mtSeed.Length != OnConnectAckPacket.SeedSize)
        {
            throw new ArgumentException(
                $"Cipher seed must be {OnConnectAckPacket.SeedSize} bytes.", nameof(mtSeed));
        }

        short signedSeed = BinaryPrimitives.ReadInt16LittleEndian(mtSeed[SumSeedOffset..]);
        return unchecked((uint)signedSeed);
    }

    public void Reset()
    {
        _enc = new DjMaxCryptoState(_mtSeed, _sumSeed);
        _dec = new DjMaxCryptoState(_mtSeed, _sumSeed);
    }

    public void Decrypt(ref Span<byte> data)
    {
        ReadOnlySpan<byte> rng = _dec.GetRngBuffer();
        Span<byte> sum = _dec.GetSumBuffer();
        Span<byte> clear = _dec.GetClearBuffer();

        for (int i = 0; i < data.Length; i++)
        {
            if (_dec.Idx > 7)
            {
                Update(ref sum, ref clear);
                _dec.Idx = 0;
                rng = _dec.NextRngBuffer();
            }

            byte key = (byte)(sum[_dec.Idx] ^ rng[_dec.Idx]);
            data[i] = (byte)(data[i] ^ key);
            clear[_dec.Idx] = data[i];
            _dec.Idx++;
        }
    }

    public void Encrypt(ref Span<byte> data)
    {
        ReadOnlySpan<byte> rng = _enc.GetRngBuffer();
        Span<byte> sum = _enc.GetSumBuffer();
        Span<byte> clear = _enc.GetClearBuffer();

        for (int i = 0; i < data.Length; i++)
        {
            if (_enc.Idx > 7)
            {
                Update(ref sum, ref clear);
                _enc.Idx = 0;
                rng = _enc.NextRngBuffer();
            }

            clear[_enc.Idx] = data[i];
            byte key = (byte)(sum[_enc.Idx] ^ rng[_enc.Idx]);
            data[i] = (byte)(data[i] ^ key);
            _enc.Idx++;
        }
    }

    private void Update(ref Span<byte> sum, ref Span<byte> clear)
    {
        uint edi = 0;
        uint eax = 0;
        uint esi = 0;
        uint edx = BinaryPrimitives.ReadUInt32LittleEndian(sum[4..]);
        uint ecx = BinaryPrimitives.ReadUInt32LittleEndian(sum);

        uint clearBlock2 = BinaryPrimitives.ReadUInt32LittleEndian(clear[4..]);
        uint clearBlock1 = BinaryPrimitives.ReadUInt32LittleEndian(clear);

        // Console.WriteLine($"clearBlock2:{clearBlock2}");
        // Console.WriteLine($"clearBlock1:{clearBlock1}");
        // Console.WriteLine($"edx:{edx}");
        // Console.WriteLine($"ecx:{ecx}");
        // Console.WriteLine($"---------");

        for (int j = 0; j < 32; j++)
        {
            eax = edx;
            eax = eax >> 5;
            eax = eax + clearBlock2;
            edi = edx;
            edi = edi << 4;
            edi = edi + clearBlock1;
            esi = esi - 0x61C88647;
            eax = eax ^ edi;
            edi = esi + edx;
            eax = eax ^ edi;
            ecx = ecx + eax;

            eax = ecx;
            eax = eax >> 5;
            eax = eax + clearBlock2;
            edi = ecx;
            edi = edi << 4;
            edi = edi + clearBlock1;
            eax = eax ^ edi;
            edi = esi + ecx;
            eax = eax ^ edi;
            edx = edx + eax;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(sum, ecx);
        BinaryPrimitives.WriteUInt32LittleEndian(sum[4..], edx);
    }

    public static DjMaxCrypto FromOnConnectAckPacket(Packet packet)
    {
        (_, _, byte[] mtSeed) = OnConnectAckPacket.Parse(packet);
        return new DjMaxCrypto(mtSeed, DeriveSumSeed(mtSeed));
    }

    public static DjMaxCrypto FromAuthenticateInSndAccReq(Packet packet)
    {
        byte[] mtSeed = AuthenticateInSndAccReqPacket.Parse(packet).CipherSeed;
        return new DjMaxCrypto(mtSeed, DeriveSumSeed(mtSeed));
    }

    public Packet ToOnConnectAckPacket(ushort assignedUserId)
    {
        return OnConnectAckPacket.Build(_mtSeed, assignedUserId);
    }
}
