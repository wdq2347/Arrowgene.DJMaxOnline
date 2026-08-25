using System.Numerics;

namespace Arrowgene.DJMaxOnline.Server.China260.Pak;

/// <summary>
/// The pak's "RSA-like" block cipher: each 8-byte little-endian cipher block decrypts to
/// a 4-byte plaintext word as <c>pow(block mod n, e, n)</c>, where n and e come from two
/// 256-entry uint64 tables lifted out of the client. The key index advances per block.
///
/// Only decryption is implemented - importing never writes a pak.
/// </summary>
public sealed class XipKeyPair
{
    /// <summary>
    /// The tables lifted out of a client, numbered rather than named after where they
    /// happened to sit in one particular build: 1.key is the modulus table, 2.key the
    /// exponent table (256 entries of uint64 each), xor.key the 256-byte rolling mask on
    /// file headers. All three are client data and none of them is embedded here.
    /// </summary>
    public const string ModulusFileName = "1.key";

    public const string ExponentFileName = "2.key";

    public const string XorFileName = "xor.key";

    private const int TableEntries = 256;
    private const int TableBytes = TableEntries * sizeof(ulong);

    private readonly ulong[] _modulus;
    private readonly ulong[] _exponent;
    private readonly byte[] _xor;

    private XipKeyPair(ulong[] modulus, ulong[] exponent, byte[] xor)
    {
        _modulus = modulus;
        _exponent = exponent;
        _xor = xor;
    }

    public static XipKeyPair Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return new XipKeyPair(
            ReadTable(Path.Combine(directory, ModulusFileName)),
            ReadTable(Path.Combine(directory, ExponentFileName)),
            ReadXor(Path.Combine(directory, XorFileName)));
    }

    /// <summary>True when <paramref name="directory"/> holds every key file.</summary>
    public static bool Available(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, ModulusFileName)) &&
        File.Exists(Path.Combine(directory, ExponentFileName)) &&
        File.Exists(Path.Combine(directory, XorFileName));

    /// <summary>
    /// Undoes the rolling XOR the client lays over every 0x11C file header. The offset
    /// wraps within the 256-byte key and is derived from the entry's position in the pak.
    /// </summary>
    public byte[] Xor(ReadOnlySpan<byte> data, int keyOffset)
    {
        byte[] result = new byte[data.Length];
        int offset = keyOffset & 0xFF;
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ _xor[offset]);
            offset = (offset + 1) & 0xFF;
        }
        return result;
    }

    private static byte[] ReadXor(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < TableEntries)
        {
            throw new InvalidDataException(
                $"{path} is {raw.Length} bytes; the XOR key is {TableEntries}.");
        }
        return raw;
    }

    private static ulong[] ReadTable(string path) =>
        ReadTable(File.ReadAllBytes(path), path);

    private static ulong[] ReadTable(ReadOnlySpan<byte> raw, string what)
    {
        if (raw.Length < TableBytes)
        {
            throw new InvalidDataException(
                $"{what} is {raw.Length} bytes; a key table is {TableBytes}.");
        }

        ulong[] table = new ulong[TableEntries];
        for (int i = 0; i < TableEntries; i++)
        {
            table[i] = BitConverter.ToUInt64(raw[(i * sizeof(ulong))..]);
        }
        return table;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> data, int keyIndex)
    {
        if (data.Length % 8 != 0)
        {
            throw new ArgumentException("Cipher length must be a multiple of 8.", nameof(data));
        }

        int blocks = data.Length / 8;
        byte[] result = new byte[blocks * 4];
        int index = keyIndex & 0xFF;
        for (int i = 0; i < blocks; i++)
        {
            BigInteger modulus = _modulus[index];
            BigInteger cipher = BitConverter.ToUInt64(data[(i * 8)..((i * 8) + 8)]);
            BigInteger plain = BigInteger.ModPow(cipher % modulus, _exponent[index], modulus);
            BitConverter.TryWriteBytes(result.AsSpan(i * 4), (uint)(plain & 0xFFFFFFFF));
            index = (index + 1) & 0xFF;
        }
        return result;
    }
}

/// <summary>
/// A second, additive dword mask the client lays over text-like members (sub_4AF230).
/// Without undoing it, every .ini pulled from a pak is garbage.
/// </summary>
public static class XipTextMask
{
    private static readonly string[] MaskedExtensions =
        [".ini", ".crc", ".cvs", ".vgi", ".gsi", ".txt", ".gds"];

    private static readonly uint[] Key = BuildKey();

    private static uint[] BuildKey()
    {
        // The key is the standard CRC-32 table, re-ordered by a fixed set of slices.
        uint[] crc = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0u);
            }
            crc[i] = value;
        }

        byte[] flat = new byte[1024];
        for (int i = 0; i < 256; i++)
        {
            BitConverter.TryWriteBytes(flat.AsSpan(i * 4), crc[i]);
        }

        (int Start, int End)[] slices =
        [
            (0x70, 0xA0), (0x30, 0x70), (0xA0, 0x100), (0x00, 0x30),
            (0x180, 0x200), (0x100, 0x180), (0x270, 0x2B0), (0x210, 0x270),
            (0x2B0, 0x300), (0x200, 0x210), (0x370, 0x400), (0x300, 0x370)
        ];

        byte[] ordered = new byte[1024];
        int written = 0;
        foreach ((int start, int end) in slices)
        {
            Array.Copy(flat, start, ordered, written, end - start);
            written += end - start;
        }

        uint[] key = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            key[i] = BitConverter.ToUInt32(ordered, i * 4);
        }
        return key;
    }

    public static bool IsMasked(string name) =>
        MaskedExtensions.Contains(Path.GetExtension(name.Replace('\\', '/')).ToLowerInvariant());

    /// <summary>Subtracts the key from every whole dword; a trailing 1-3 bytes pass through.</summary>
    public static byte[] Unmask(byte[] data)
    {
        int whole = data.Length / 4;
        int keyOffset = data.Length % 0x100;
        byte[] result = (byte[])data.Clone();
        for (int i = 0; i < whole; i++)
        {
            uint value = BitConverter.ToUInt32(result, i * 4);
            BitConverter.TryWriteBytes(
                result.AsSpan(i * 4), unchecked(value - Key[(keyOffset + i) % 256]));
        }
        return result;
    }

    public static byte[] UnmaskIfNeeded(string name, byte[] data) =>
        IsMasked(name) ? Unmask(data) : data;
}
