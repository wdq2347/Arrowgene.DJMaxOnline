using System.Buffers.Binary;

namespace Arrowgene.DJMaxOnline.Server;

/// <summary>
/// The 13 judgment windows, in milliseconds, that the server ships to the client with
/// every chart.
///
/// They live in the game-info payload's 112-byte gameplay-config block (payload+16,
/// scrambled with v44), at block offset 4: <c>[int 7][13 JudgmentDelta ms][13
/// GaugeUpDownRate floats][float 2.4]</c>. The client has no judgment table of its own for
/// online play - it uses what arrives with the chart - so timing strictness is entirely
/// server-authored, per chart and per recipient.
///
/// Entry 0 is a sentinel (-1) and is never a window. The remaining twelve descend, tightest
/// last; a SMALLER number is a stricter game.
/// </summary>
public sealed record JudgmentWindows
{
    public const int Count = 13;

    /// <summary>Entry 0 is not a timing window and must stay -1.</summary>
    public const int Sentinel = -1;

    /// <summary>
    /// A window may never reach zero: a 0 ms window cannot be hit, and the ordering has to
    /// stay descending or the client's walk picks the wrong grade.
    /// </summary>
    public const int MinimumWindowMs = 1;

    private readonly int[] _windows;

    private JudgmentWindows(int[] windows) => _windows = windows;

    /// <summary>
    /// The retail values, recovered byte-exact from a captured OnGameInfoInf
    /// (blade_stream_02, session ".[5KEY] Classic", room descriptor 1).
    /// </summary>
    public static readonly JudgmentWindows Retail = new(
        [Sentinel, 51, 50, 48, 44, 42, 40, 38, 36, 32, 28, 24, 16]);

    public IReadOnlyList<int> Values => _windows;

    /// <summary>
    /// Widens (positive) or tightens (negative) every window by
    /// <paramref name="milliseconds"/>. A SIGHT booster's <c>judgmentboost</c> widens;
    /// a stricter ranking mode tightens. The sentinel is untouched and no window is
    /// allowed below <see cref="MinimumWindowMs"/>.
    /// </summary>
    public JudgmentWindows Adjust(int milliseconds)
    {
        if (milliseconds == 0)
        {
            return this;
        }

        int[] adjusted = new int[Count];
        adjusted[0] = Sentinel;
        for (int index = 1; index < Count; index++)
        {
            adjusted[index] = Math.Max(MinimumWindowMs, _windows[index] + milliseconds);
        }
        return new JudgmentWindows(adjusted);
    }

    /// <summary>Writes the windows into a config block at its JudgmentDelta offset.</summary>
    public void WriteTo(Span<byte> block)
    {
        for (int index = 0; index < Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                block.Slice(index * sizeof(int)), _windows[index]);
        }
    }

    public override string ToString() => string.Join(",", _windows.Skip(1));
}
