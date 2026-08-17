using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Japan400.Packets;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

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

        /// <param name="sumFromMt">
        /// Take the XTEA sum from the Mersenne Twister instead of from the seed bytes.
        /// This is what the JP client does: sub_44E404 seeds the MT and then immediately
        /// calls sub_44E101, which draws TWO genrand_int32 (sub_44CE27) and stores them as
        /// the 8-byte sum pair - before any keystream is taken. Korea instead derives the
        /// sum from an int16 in the seed and draws the keystream first, so the two orders
        /// are not interchangeable: getting it wrong shifts the whole stream.
        /// </param>
        public DjMaxCryptoState(byte[] mtSeed, uint sumSeed, bool sumFromMt = false)
        {
            Idx = 0;
            _sumBuffer = new byte[8];
            _rngBuffer = new byte[8];
            _clearBuffer = new byte[8];
            _mt = new MersenneTwister(mtSeed);
            if (sumFromMt)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_sumBuffer, _mt.NextUInt32());
                BinaryPrimitives.WriteUInt32LittleEndian(_sumBuffer[4..], _mt.NextUInt32());
                NextRngBuffer();
            }
            else
            {
                NextRngBuffer();
                BinaryPrimitives.WriteUInt32LittleEndian(_sumBuffer, sumSeed);
                BinaryPrimitives.WriteUInt32LittleEndian(_sumBuffer[4..], 0);
            }
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
    private readonly bool _sumFromMt;
    private readonly byte[] _mtSeed;

    public DjMaxCrypto(byte[] mtSeed, uint sumSeed)
        : this(mtSeed, sumSeed, requireKoreaSeedSize: true)
    {
    }

    private DjMaxCrypto(
        byte[] mtSeed, uint sumSeed, bool requireKoreaSeedSize, bool sumFromMt = false)
    {
        if (requireKoreaSeedSize && mtSeed.Length != OnConnectAckPacket.SeedSize)
        {
            throw new ArgumentException(
                $"Cipher seed must be {OnConnectAckPacket.SeedSize} bytes.", nameof(mtSeed));
        }

        _sumSeed = sumSeed;
        _mtSeed = mtSeed;
        _sumFromMt = sumFromMt;
        _enc = new DjMaxCryptoState(_mtSeed, _sumSeed, _sumFromMt);
        _dec = new DjMaxCryptoState(_mtSeed, _sumSeed, _sumFromMt);
    }

    /// <summary>
    /// Builds the cipher for the Japanese client's OnConnectAck (id 9). Its handler
    /// (client sub_430AB0) copies a 30-byte seed and seeds MT19937 via init_by_array
    /// over the first 7 uint32 words (28 bytes) — sub_44B40C. The XTEA sum seed is
    /// derived per sub_430A10 as (uint16)(word@28 + dword@24). Same MT+XTEA keystream
    /// as the Korea path, only the seed shape differs.
    /// </summary>
    /// <summary>
    /// Cipher seed length: all 32 wire bytes.
    ///
    /// Read off the client, not guessed. sub_4312C0 stores the 32 bytes from offset 7 and
    /// installs them with sub_44CD80(seed, 8), and sub_44CD80 is textbook Mersenne Twister
    /// init_by_array - init_genrand(19650218), then the 1664525 and 1566083941 rounds -
    /// where the second argument is the key length IN UINT32 WORDS. 8 words = 32 bytes.
    ///
    /// This was 30 (28 for the MT plus an int16 sum), carried over from Korea. Keying the
    /// MT on 7 words instead of 8 makes the keystream diverge from the client's on the
    /// very first byte, so everything the server enciphers after OnConnectAck is noise to
    /// it - which is silent, because packet ids are written outside the enciphered region
    /// and the connection therefore looks perfectly healthy.
    /// </summary>
    public const int JapaneseSeedSize = 32;

    /// <summary>
    /// A cipher context keyed on an arbitrary number of seed bytes.
    ///
    /// Key length is PER CONTEXT in this client, not global: sub_44E404 takes it as a
    /// uint32 word count, and the main stream is installed with 8 words (32 bytes) while
    /// OnCipherCommandInf (sub_433940) installs the same seed with 2 words (8 bytes).
    /// </summary>
    public static DjMaxCrypto InitJapaneseWithKey(ReadOnlySpan<byte> key)
    {
        if (key.Length == 0 || key.Length % 4 != 0)
        {
            throw new ArgumentException(
                "Key must be a whole number of uint32 words.", nameof(key));
        }
        return new DjMaxCrypto(
            key.ToArray(), 0, requireKoreaSeedSize: false, sumFromMt: true);
    }

    /// <summary>
    /// The XTEA sum this client starts from, derived from the seed - NOT drawn from the MT.
    ///
    /// sub_4312C0 calls sub_431270(seed+24 as uint32, seed+28 as int16), which is:
    ///     cmp [var_C], 20h / jnb ...      -> flag = (seed24 &lt; 0x20) ? 1 : 0
    ///     or  edx, [arg_4]               -> sum_lo = (int16)seed28 | flag
    ///     mov [ecx+0C1C6Ch], eax         -> sum_hi = 0 (eax was just xor'd)
    /// The int16 is read signed and pushed sign-extended, so a value with the high bit set
    /// fills the top half of sum_lo.
    /// </summary>
    private static uint DeriveJapaneseSumSeed(ReadOnlySpan<byte> seed32)
    {
        uint atTwentyFour = BinaryPrimitives.ReadUInt32LittleEndian(seed32[24..]);
        short atTwentyEight = BinaryPrimitives.ReadInt16LittleEndian(seed32[28..]);
        return (uint)(int)atTwentyEight | (atTwentyFour < 0x20 ? 1u : 0u);
    }

    public static DjMaxCrypto InitJapanese(ReadOnlySpan<byte> seed32)
    {
        if (seed32.Length != JapaneseSeedSize)
        {
            throw new ArgumentException(
                $"JP cipher seed must be {JapaneseSeedSize} bytes.", nameof(seed32));
        }

        // The whole seed is the MT key: 8 uint32 for init_by_array, per sub_44E404's word
        // count of 8.
        //
        // sumFromMt is FALSE, which is the opposite of what the layout first suggests.
        // sub_44E404 sets the sum from its arguments BEFORE seeding the MT, and the two
        // genrand draws that follow (sub_44E101) land in the context's 8-byte RNG buffer
        // at this[634] - they are the first KEYSTREAM block, not the sum. That is the same
        // ordering Korea uses; only the sum derivation differs.
        return new DjMaxCrypto(
            seed32.ToArray(), DeriveJapaneseSumSeed(seed32),
            requireKoreaSeedSize: false, sumFromMt: false);
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
        _enc = new DjMaxCryptoState(_mtSeed, _sumSeed, _sumFromMt);
        _dec = new DjMaxCryptoState(_mtSeed, _sumSeed, _sumFromMt);
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
