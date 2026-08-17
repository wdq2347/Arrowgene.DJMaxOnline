namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// Produces an LZO1X stream that the client's decompressor (sub_49FB70) accepts.
/// The client always LZO-decompresses a game-info chart body before parsing the
/// PTFF, so a raw PTFF cannot be sent directly. Rather than reimplement LZO1X
/// match-finding, this emits a single all-literal run followed by the
/// end-of-stream marker — a valid LZO1X stream that decodes back to the exact
/// input. The client's decompress output buffer is ~98 KiB, which bounds the
/// supported PTFF size.
/// </summary>
public static class PtffLzo
{
    public const int MaxDecompressedSize = 24576 * 4; // client stack buffer (v52)

    public static byte[] EncodeAllLiteral(ReadOnlySpan<byte> data)
    {
        // A literal run copies (token + 3) bytes; the extended form encodes the
        // count as 15 + 255*z + b (z zero bytes then a non-zero b).
        if (data.Length < 19)
        {
            throw new ArgumentException(
                "PTFF payload is too small to encode as a literal run.", nameof(data));
        }
        if (data.Length > MaxDecompressedSize)
        {
            throw new ArgumentException(
                $"PTFF payload {data.Length} exceeds the client's " +
                $"{MaxDecompressedSize}-byte decompression buffer.", nameof(data));
        }

        List<byte> output = new(data.Length + 8) { 0x00 };
        int value = data.Length - 3 - 15;
        while (value > 255)
        {
            output.Add(0x00);
            value -= 255;
        }
        output.Add((byte)value);
        output.AddRange(data);

        // End-of-stream: match token 0x11 with a zero distance word.
        output.Add(0x11);
        output.Add(0x00);
        output.Add(0x00);
        return output.ToArray();
    }
}
