using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// Assembles a complete OnGameInfoInf payload from a raw (plaintext) PTFF chart,
/// reproducing exactly what the retail server sent:
///
///   [132-byte header] + scramble(LZO1X(PTFF), ~v44)
///
/// where the header carries, in its de-scrambled form, the disc id / difficulty
/// used for the key and a 112-byte gameplay-config block scrambled with v44.
/// Every transform here was reverse-engineered from and validated against the
/// retail client (see <see cref="GameInfoScrambler"/> and <see cref="PtffLzo"/>).
/// </summary>
public static class GameInfoBlobBuilder
{
    private const int ChartTypeOffset = 2;   // note-count / effect-loop bound
    private const int DifficultyOffset = 8;
    private const int ConfigBlockOffset = 16;
    private const int ConfigBlockLength = 112;
    private const int ChartDataSizeOffset = 128;
    private const int KeyHeaderOffset = GameplayProtocol.GameInfoDiscIdOffset; // 4
    private const int KeyHeaderLength = 6;                                     // discId + diff

    /// <summary>
    /// Gameplay config recovered byte-exact from a captured retail
    /// <c>OnGameInfoInf</c> packet. The previous values came from
    /// <c>sub_409200</c>'s local-file fallback branch and are not the values the
    /// retail server supplied for online play. Layout:
    /// [int 7][13 JudgmentDelta ms ints][13 GaugeUpDownRate floats][float 2.4].
    /// </summary>
    public static readonly byte[] DefaultConfigBlock =
        BuildConfigBlock(JudgmentWindows.Retail);

    /// <param name="windows">
    /// Judgment windows to ship with this chart, or null for the retail values. They are
    /// the only per-recipient part of the payload: the scramble key is derived from the
    /// disc, difficulty, session label and room descriptor, none of which depend on the
    /// player, so two clients in the same room can be handed the same chart with different
    /// timing and each decodes its own copy correctly.
    /// </param>
    public static byte[] Build(
        uint discId,
        ushort difficulty,
        ushort chartType,
        ReadOnlySpan<byte> ptff,
        string sessionLabel,
        short descriptorValue,
        JudgmentWindows? windows = null)
    {
        byte[] header = new byte[GameplayProtocol.GameInfoHeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ChartTypeOffset), chartType);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(GameplayProtocol.GameInfoDiscIdOffset), discId);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(DifficultyOffset), difficulty);

        uint v44 = GameInfoScrambler.ComputeV44(
            header.AsSpan(KeyHeaderOffset, KeyHeaderLength), sessionLabel, descriptorValue);

        // Config block de-scrambles with v44; chart body with ~v44.
        byte[] config = windows == null
            ? DefaultConfigBlock
            : BuildConfigBlock(windows);
        GameInfoScrambler.Scramble(config, GameInfoScrambler.KeyBytes(v44))
            .CopyTo(header.AsSpan(ConfigBlockOffset, ConfigBlockLength));

        byte[] lzo = PtffLzo.EncodeAllLiteral(ptff);
        byte[] chart = GameInfoScrambler.Scramble(lzo, GameInfoScrambler.KeyBytes(~v44));

        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(ChartDataSizeOffset), (uint)chart.Length);

        byte[] blob = new byte[header.Length + chart.Length];
        header.CopyTo(blob, 0);
        chart.CopyTo(blob, header.Length);

        GameInfoPayload.Parse(blob); // fail fast on framing inconsistency
        return blob;
    }

    /// <summary>Offset of the 13 JudgmentDelta ints inside the config block.</summary>
    private const int JudgmentDeltaOffset = 4;

    public static byte[] BuildConfigBlock(JudgmentWindows windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        // Decrypted from blade_stream_02's captured OnGameInfoInf using session
        // ".[5KEY] Classic" and room descriptor 1.
        // block[0]=7; [4:56]=JudgmentDelta (-1,51,50,48,...,16);
        // [56:108]=GaugeUpDownRate (0,-6,0,0.01,...,0.3); [108]=2.4f.
        const string gaugeUpDownRate =
            "000000000000c0c0000000000ad7233c0ad7a33c8fc2f53c0ad7233d" +
            "cdcc4c3d8fc2753d295c8f3d0ad7a33d9a99193e9a99993e";

        byte[] block = new byte[ConfigBlockLength];
        BinaryPrimitives.WriteInt32LittleEndian(block, 7);
        windows.WriteTo(block.AsSpan(JudgmentDeltaOffset));
        Convert.FromHexString(gaugeUpDownRate).CopyTo(block.AsSpan(56));
        BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(108), 2.4f);
        return block;
    }
}
