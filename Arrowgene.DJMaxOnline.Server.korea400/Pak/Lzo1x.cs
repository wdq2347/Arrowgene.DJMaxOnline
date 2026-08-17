namespace Arrowgene.DJMaxOnline.Server.Korea400.Pak;

/// <summary>
/// LZO1X decoder, the payload codec inside an XIP2 pak. A direct port of the reference
/// <c>lzo1x_d</c> state machine, which is what the client's own decoder (sub_496BC0)
/// implements - the two agree bit for bit.
///
/// Decode only: nothing here needs to write a pak.
/// </summary>
public static class Lzo1x
{
    private const int M2MaxOffset = 0x0800;

    private enum State
    {
        Top,
        Match,
        MatchNext,
        FirstLiteralRun,
        CopyMatch,
        MatchDone
    }

    public static byte[] Decompress(ReadOnlySpan<byte> source, int expectedSize)
    {
        if (source.Length < 3)
        {
            throw new InvalidDataException("lzo1x: input too short.");
        }

        // The hint is only a starting capacity; the loop still bounds-checks every write.
        List<byte> output = new(Math.Max(expectedSize, 64));
        int ip = 0;
        int t;
        int m = 0;
        State state;

        int first = source[0];
        if (first > 17)
        {
            t = first - 17;
            ip = 1;
            if (t < 4)
            {
                state = State.MatchNext;
            }
            else
            {
                CopyLiterals(output, source, ref ip, t);
                state = State.FirstLiteralRun;
            }
        }
        else
        {
            t = 0;
            state = State.Top;
        }

        while (true)
        {
            switch (state)
            {
                case State.Top:
                    t = source[ip++];
                    if (t >= 16)
                    {
                        state = State.Match;
                        continue;
                    }
                    if (t == 0)
                    {
                        while (source[ip] == 0)
                        {
                            t += 255;
                            ip++;
                        }
                        t += 15 + source[ip++];
                    }
                    t += 3;
                    CopyLiterals(output, source, ref ip, t);
                    state = State.FirstLiteralRun;
                    continue;

                case State.FirstLiteralRun:
                    t = source[ip++];
                    if (t >= 16)
                    {
                        state = State.Match;
                        continue;
                    }
                    m = output.Count - (1 + M2MaxOffset) - (t >> 2) - (source[ip++] << 2);
                    CopyMatch(output, m, 3);
                    state = State.MatchDone;
                    continue;

                case State.MatchNext:
                    CopyLiterals(output, source, ref ip, t);
                    t = source[ip++];
                    state = State.Match;
                    continue;

                case State.Match:
                    if (t >= 64)
                    {
                        m = output.Count - 1 - ((t >> 2) & 7) - (source[ip++] << 3);
                        t = (t >> 5) - 1;
                        state = State.CopyMatch;
                        continue;
                    }
                    if (t >= 32)
                    {
                        t &= 31;
                        if (t == 0)
                        {
                            while (source[ip] == 0)
                            {
                                t += 255;
                                ip++;
                            }
                            t += 31 + source[ip++];
                        }
                        m = output.Count - 1 - ((source[ip] | (source[ip + 1] << 8)) >> 2);
                        ip += 2;
                        state = State.CopyMatch;
                        continue;
                    }
                    if (t >= 16)
                    {
                        m = output.Count - ((t & 8) << 11);
                        t &= 7;
                        if (t == 0)
                        {
                            while (source[ip] == 0)
                            {
                                t += 255;
                                ip++;
                            }
                            t += 7 + source[ip++];
                        }
                        m -= (source[ip] | (source[ip + 1] << 8)) >> 2;
                        ip += 2;
                        if (m == output.Count)
                        {
                            return [.. output];   // end-of-stream marker
                        }
                        m -= 0x4000;
                        state = State.CopyMatch;
                        continue;
                    }
                    m = output.Count - 1 - (t >> 2) - (source[ip++] << 2);
                    CopyMatch(output, m, 2);
                    state = State.MatchDone;
                    continue;

                case State.CopyMatch:
                    CopyMatch(output, m, t + 2);
                    state = State.MatchDone;
                    continue;

                default:
                    t = source[ip - 2] & 3;
                    state = t == 0 ? State.Top : State.MatchNext;
                    continue;
            }
        }
    }

    private static void CopyLiterals(
        List<byte> output, ReadOnlySpan<byte> source, ref int ip, int count)
    {
        if (count < 0 || ip + count > source.Length)
        {
            throw new InvalidDataException("lzo1x: literal run runs past the input.");
        }

        for (int i = 0; i < count; i++)
        {
            output.Add(source[ip + i]);
        }
        ip += count;
    }

    /// <summary>
    /// Back-reference copy. The source may overlap the write position, in which case the
    /// reference decoder's byte-at-a-time copy repeats the available tail - so this has to
    /// copy one byte at a time rather than slicing.
    /// </summary>
    private static void CopyMatch(List<byte> output, int m, int count)
    {
        if (m < 0 || m >= output.Count)
        {
            throw new InvalidDataException("lzo1x: match offset out of range.");
        }

        for (int i = 0; i < count; i++)
        {
            output.Add(output[m + i]);
        }
    }
}
