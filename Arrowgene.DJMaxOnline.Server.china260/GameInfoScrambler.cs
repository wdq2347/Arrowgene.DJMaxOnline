using System.Buffers.Binary;
using System.Text;

namespace Arrowgene.DJMaxOnline.Server.China260;

/// <summary>
/// Reproduces the client-side game-info obfuscation (sub_49E115) and its key
/// derivation (sub_409520). The client stores each OnGameInfoInf chart body as
/// <c>scramble(LZO1X(PTFF))</c> and, at play time, de-scrambles then decompresses
/// it. The scramble key is bound to the current session, which is why captured
/// payloads are not portable between sessions.
///
/// Key = ~( CRC32(discId + difficulty, 6 bytes)   [game-info header +0x04]
///        + CRC32(sessionString)                  [account/channel label]
///        + (int)int16 )                          [room-descriptor field]
///
/// Verified byte-exact against a live client: reconstructing the served blob from
/// the paused de-scramble buffer with this key reproduced 1.bin exactly.
/// </summary>
public static class GameInfoScrambler
{
    // Permutation tables from the client (dword_55AA44 / byte_55AA74 /
    // byte_55AA8C / off_55AA98). Row selected by key[0] % 3.
    private static readonly byte[][] Perm16 =
    [
        [1, 3, 5, 7, 9, 11, 13, 15, 14, 12, 10, 8, 6, 4, 2, 0],
        [3, 6, 9, 12, 15, 2, 4, 8, 10, 14, 1, 0, 5, 7, 11, 13],
        [5, 10, 15, 7, 14, 0, 6, 12, 4, 8, 3, 9, 2, 1, 13, 11]
    ];

    private static readonly byte[][] Perm8 =
    [
        [1, 4, 7, 2, 3, 5, 6, 0],
        [7, 5, 3, 1, 0, 2, 4, 6],
        [3, 6, 0, 1, 2, 7, 5, 4]
    ];

    private static readonly byte[][] Perm4 =
    [
        [1, 2, 3, 0],
        [2, 3, 0, 1],
        [3, 0, 1, 2]
    ];

    private static readonly byte[][] Perm2 =
    [
        [1, 0],
        [0, 1],
        [1, 0]
    ];

    /// <summary>
    /// Computes the session accumulator <c>v44</c> the client derives in
    /// <c>sub_409520</c>. The chart body is de-scrambled with <c>~v44</c> and the
    /// header config block with <c>v44</c>.
    /// </summary>
    /// <param name="header">The 6-byte region at game-info body +0x04
    /// (disc id followed by the 2-byte difficulty selector).</param>
    /// <param name="sessionLabel">The account/channel label the client CRCs,
    /// e.g. ".[7KEY] Local".</param>
    /// <param name="descriptorValue">The signed 16-bit room-descriptor field.</param>
    public static uint ComputeV44(
        ReadOnlySpan<byte> header, string sessionLabel, short descriptorValue)
    {
        if (header.Length != 6)
        {
            throw new ArgumentException("Header key region must be 6 bytes.", nameof(header));
        }
        ArgumentNullException.ThrowIfNull(sessionLabel);

        return unchecked(
            Crc32.GetHash(header.ToArray()) +
            Crc32.GetHash(Encoding.ASCII.GetBytes(sessionLabel)) +
            (uint)(int)descriptorValue);
    }

    /// <summary>The four-byte little-endian de-scramble key for a given value.</summary>
    public static byte[] KeyBytes(uint value)
    {
        byte[] key = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(key, value);
        return key;
    }

    /// <summary>
    /// Scrambles a de-scrambled chart body (typically an LZO1X stream) so that
    /// the client's <c>sub_49E115</c> reproduces the original bytes. This is the
    /// exact inverse of the client's de-scramble.
    /// </summary>
    public static byte[] Scramble(ReadOnlySpan<byte> plain, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 4)
        {
            throw new ArgumentException("Key must be 4 bytes.", nameof(key));
        }
        if (plain.Length >= 0x100000)
        {
            throw new ArgumentException(
                "Chart body exceeds the client's 1 MiB scramble limit.", nameof(plain));
        }

        int n = plain.Length;
        // The client (sub_49E115) selects the permutation row with a SIGNED byte
        // modulo (idiv), mapping the remainder {0->0, 1->1, everything else->2}.
        // An unsigned key[0] % 3 diverges whenever key[0] >= 0x80 — e.g. 0xA6 gives
        // unsigned 166%3==1 but signed (-90)%3==0 — selecting the wrong permutation
        // and producing a corrupt chart that crashes the client's LZO decompressor.
        int remainder = (sbyte)key[0] % 3;
        int variant = remainder == 0 ? 0 : remainder == 1 ? 1 : 2;

        // Undo the subtract/complement stage: P[i] = (~plain[i]) + key[i % 4].
        byte[] p = new byte[n];
        for (int i = 0; i < n; i++)
        {
            p[i] = (byte)(((byte)~plain[i]) + key[i % 4]);
        }

        // Undo the byte permutation: scrambled[v5 + v7] = P[v5 + table[v7]].
        byte[] scrambled = new byte[n];
        byte[] t16 = Perm16[variant];
        byte[] t8 = Perm8[variant];
        byte[] t4 = Perm4[variant];
        byte[] t2 = Perm2[variant];
        int offset = 0;
        int remaining = n;
        while (remaining >= 16)
        {
            for (int j = 0; j < 16; j++)
            {
                scrambled[offset + j] = p[offset + t16[j]];
            }
            offset += 16;
            remaining -= 16;
        }
        if (remaining >= 8)
        {
            for (int j = 0; j < 8; j++)
            {
                scrambled[offset + j] = p[offset + t8[j]];
            }
            offset += 8;
            remaining -= 8;
        }
        if (remaining >= 4)
        {
            for (int j = 0; j < 4; j++)
            {
                scrambled[offset + j] = p[offset + t4[j]];
            }
            offset += 4;
            remaining -= 4;
        }
        if (remaining >= 2)
        {
            for (int j = 0; j < 2; j++)
            {
                scrambled[offset + j] = p[offset + t2[j]];
            }
            offset += 2;
            remaining -= 2;
        }
        if (remaining == 1)
        {
            scrambled[offset] = p[offset];
        }
        return scrambled;
    }
}
