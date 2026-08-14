using System.Text;

namespace Arrowgene.DJMaxOnline.Server.Pak;

/// <summary>One member of an XIP2 pak.</summary>
public sealed record XipEntry(
    int Index,
    int HeaderOffset,
    string Name,
    int BlockSize,
    int UncompressedSize,
    uint Crc32,
    int CryptKeyIndex,
    bool Deleted)
{
    /// <summary>The archive path with forward slashes, for comparing across paks.</summary>
    public string NormalisedName => Name.Replace('\\', '/');

    public string FileName => NormalisedName[(NormalisedName.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Reader for the client's XIP2 pak container.
///
/// Layout, for reference: a 12-byte header whose bytes encode the offset of "secret A"
/// and a skip size; secret A decrypts (key index 0x0c) to 12 bytes giving the first file
/// offset and the file count; then a run of 0x11C-byte file headers, each XOR'd with the
/// Japanese key at offset <c>fileCount - index</c> and followed by its payload. The
/// payload begins with two scrambled size dwords, then an RSA-like encrypted prefix, then
/// the raw remainder; the whole thing is one LZO1X stream once reassembled.
/// </summary>
public sealed class XipArchive
{
    public const int FileHeaderLength = 0x11C;
    private static readonly byte[] Magic = "XIP2"u8.ToArray();

    static XipArchive() =>
        // Shift-JIS and EUC-KR are not in the default .NET Core encoding set.
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    private readonly byte[] _buffer;
    private readonly XipKeyPair _keys;

    private XipArchive(byte[] buffer, XipKeyPair keys, IReadOnlyList<XipEntry> entries)
    {
        _buffer = buffer;
        _keys = keys;
        Entries = entries;
    }

    public IReadOnlyList<XipEntry> Entries { get; }

    public static XipArchive Open(string path, XipKeyPair keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return Parse(File.ReadAllBytes(path), keys, path);
    }

    public static XipArchive Parse(byte[] buffer, XipKeyPair keys, string label)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(keys);

        if (buffer.Length < 12 || !buffer.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{label} is not an XIP2 pak.");
        }

        // The header interleaves the two values across fixed byte positions.
        uint secretOffset = (uint)(buffer[4] | (buffer[6] << 8) | (buffer[8] << 16) |
                                   (buffer[11] << 24));
        uint secretSkip = (uint)(buffer[9] | (buffer[7] << 8));

        if (secretOffset + 24 > buffer.Length)
        {
            throw new InvalidDataException($"{label}: secret A runs past the end of the file.");
        }

        byte[] secret = keys.Decrypt(buffer.AsSpan((int)secretOffset, 24), 0x0C);
        if (secret.Length < 12)
        {
            throw new InvalidDataException($"{label}: truncated secret A.");
        }

        int firstFileOffset = BitConverter.ToInt32(secret, 2);
        int fileCount = BitConverter.ToInt32(secret, 6);
        if (firstFileOffset is <= 0 or > 0x3000 || fileCount is < 0 or > 100_000)
        {
            throw new InvalidDataException(
                $"{label}: secret A looks wrong (offset {firstFileOffset}, count {fileCount}). " +
                "The key tables probably belong to a different client build.");
        }

        List<XipEntry> entries = new(fileCount);
        int offset = firstFileOffset;
        for (int index = 0; index < fileCount; index++)
        {
            if (offset == secretOffset)
            {
                offset += (int)secretSkip;
            }
            if (offset + FileHeaderLength > buffer.Length)
            {
                throw new InvalidDataException(
                    $"{label}: truncated header for entry {index} at 0x{offset:X}.");
            }

            byte[] header = keys.Xor(
                buffer.AsSpan(offset, FileHeaderLength), fileCount - index);

            int blockSize = BitConverter.ToInt32(header, 0);
            int uncompressedSize = BitConverter.ToInt32(header, 4);
            string name = DecodeName(header.AsSpan(0x0C, 0x104));
            uint crc32 = BitConverter.ToUInt32(header, 0x110);
            int cryptKeyIndex = header[0x118];

            // Tombstones: a repack blanks an entry rather than rewriting every offset.
            bool deleted = name.Length == 0 || (blockSize == 0 && uncompressedSize == 0);
            entries.Add(new XipEntry(index, offset, name, blockSize, uncompressedSize,
                crc32, cryptKeyIndex, deleted));

            offset += FileHeaderLength + blockSize;
        }

        return new XipArchive(buffer, keys, entries);
    }

    /// <summary>
    /// Names are stored in whatever code page the region's packer used. Shift-JIS covers
    /// the Japanese and Korean builds' ASCII paths; the fallbacks are for the rest.
    /// </summary>
    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        int zero = raw.IndexOf((byte)0);
        ReadOnlySpan<byte> chunk = zero >= 0 ? raw[..zero] : raw;
        if (chunk.Length == 0)
        {
            return string.Empty;
        }

        // us-ascii, Shift-JIS, EUC-KR, UTF-8. Each is asked to throw rather than
        // substitute, so a wrong code page is rejected instead of yielding "????".
        foreach (int codePage in (int[])[20127, 932, 949, 65001])
        {
            string decoded;
            try
            {
                decoded = Encoding
                    .GetEncoding(codePage, EncoderFallback.ExceptionFallback,
                        DecoderFallback.ExceptionFallback)
                    .GetString(chunk);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or DecoderFallbackException)
            {
                continue;
            }
            if (decoded.Length > 0 && !decoded.Any(c => c <= 8))
            {
                return decoded.TrimEnd('\0');
            }
        }

        return Encoding.Latin1.GetString(chunk);
    }

    public byte[] Extract(XipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Deleted)
        {
            throw new InvalidOperationException($"{entry.Name} is a deleted entry.");
        }

        int payload = entry.HeaderOffset + FileHeaderLength;
        uint a1 = BitConverter.ToUInt32(_buffer, payload);
        uint a2 = BitConverter.ToUInt32(_buffer, payload + 4);
        (int cryptChunkSize, int rsaPlainSize, int loops) = UnscrambleSizes(a1, a2);
        if (loops != 0)
        {
            throw new InvalidDataException(
                $"{entry.Name}: unsupported multi-pass crypt (timesLoopX={loops}).");
        }

        int cryptOffset = payload + 8;
        byte[] decrypted = _keys.Decrypt(
            _buffer.AsSpan(cryptOffset, rsaPlainSize * 2), entry.CryptKeyIndex);

        int restOffset = cryptOffset + cryptChunkSize;
        int restLength = entry.BlockSize - 8 - cryptChunkSize;
        byte[] stream = restLength > 0
            ? [.. decrypted, .. _buffer.AsSpan(restOffset, restLength)]
            : decrypted;

        byte[] data = Lzo1x.Decompress(stream, entry.UncompressedSize);
        if (data.Length > entry.UncompressedSize)
        {
            // The decoder can overshoot into the final match; the header size is truth.
            data = data[..entry.UncompressedSize];
        }
        else if (data.Length < entry.UncompressedSize)
        {
            throw new InvalidDataException(
                $"{entry.Name}: decoded {data.Length} bytes, header says {entry.UncompressedSize}.");
        }

        return XipTextMask.UnmaskIfNeeded(entry.Name, data);
    }

    /// <summary>The two payload size dwords are byte-shuffled into each other.</summary>
    private static (int CryptChunkSize, int RsaPlainSize, int Loops) UnscrambleSizes(
        uint a1, uint a2)
    {
        uint cryptChunkSize =
            (((((((a2 >> 8) & 0xFF) << 8) | (a1 & 0xFF)) << 8) | ((a1 >> 24) & 0xFF)) << 8) |
            ((a1 >> 8) & 0xFF);
        uint rsaPlainSize =
            (((((((a1 >> 16) & 0xFF) << 8) | ((a2 >> 24) & 0xFF)) << 8) | (a2 & 0xFF)) << 8) |
            ((a2 >> 16) & 0xFF);
        return ((int)cryptChunkSize, (int)rsaPlainSize, (int)((a2 >> 16) & 3));
    }
}
