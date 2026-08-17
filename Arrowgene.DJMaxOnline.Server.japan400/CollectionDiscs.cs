using System.Runtime.Serialization;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// A collection disc awarded for finishing a chart on an EXACT judgment percentage.
///
/// Retail's set (namu.wiki, DJMAX Online, footnote 44): Ruby 10%, Sapphire 1%,
/// Devil 66.6% (only songs whose note count is a multiple of 5), Rainbow Max 77.7%
/// (multiples of 10), Dragon 88.88% (China D-Mac only, not shipped here). The note-count
/// multiples exist so those percentages are actually reachable - "Devil and Rainbow discs
/// were not supposed to have decimal points below accuracy", i.e. the hit has to be exact.
/// </summary>
public sealed class AccuracyDiscRule
{
    [DataMember(Order = 1)] public string Name { get; set; } = string.Empty;

    /// <summary>The judgment percentage that must be hit exactly, e.g. 66.6.</summary>
    [DataMember(Order = 2)] public double Accuracy { get; set; }

    /// <summary>
    /// Only charts whose note count is a multiple of this can award it; 1 = any chart.
    /// </summary>
    [DataMember(Order = 3)] public int NoteMultiple { get; set; } = 1;

    /// <summary>
    /// PRIZE/COLLECTION code. Only 0x400..0x413 and 0x420..0x42D actually render - see
    /// <see cref="CollectionDiscs.Renders"/> - and which artwork each one draws is fixed
    /// by the switch in sub_465AD1.
    ///
    /// Reading Medal_S_1.png against that switch: frames 6/7/8 are the three MAX discs
    /// (codes 0x403/0x404/0x405), frames 9/10/11 are the G/S/B letter discs
    /// (0x406/0x407/0x408), and the plain coloured discs left over are Ruby (frame 1,
    /// 0x400), Rainbow (frame 2, 0x401), Sapphire (frame 3, 0x409) and Devil (frame 4,
    /// 0x40A). That last pairing is the weakest link - if a disc draws wrong, this is the
    /// field to correct.
    /// </summary>
    [DataMember(Order = 4)] public ushort Code { get; set; }
}

public static class CollectionDiscs
{
    /// <summary>
    /// How close the reported accuracy has to be to count as an exact hit. The client
    /// reports a float, so an equality test would never fire.
    /// </summary>
    public const double DefaultTolerance = 0.005;

    /// <summary>Configured from <c>Setting.AccuracyDiscTolerance</c> at startup.</summary>
    public static double Tolerance { get; private set; } = DefaultTolerance;

    public static void Configure(double tolerance) =>
        Tolerance = tolerance > 0 ? tolerance : DefaultTolerance;

    /// <summary>
    /// Whether the collection dialog can actually draw this code. sub_465AD1 maps
    /// 0x400..0x413 through an explicit switch and 0x420..0x42D through `code - 1035`;
    /// everything else in the 0x400..0x43F band resolves to no component and is silently
    /// dropped, so awarding one would be invisible.
    /// </summary>
    public static bool Renders(ushort code) =>
        code is (>= 0x400 and <= 0x413) or (>= 0x420 and <= 0x42D);

    /// <summary>Retail's set, minus the China-only Dragon disc.</summary>
    public static List<AccuracyDiscRule> Default() =>
    [
        new() { Name = "Sapphire Disc", Accuracy = 1.0, NoteMultiple = 1, Code = 0x409 },
        new() { Name = "Ruby Disc", Accuracy = 10.0, NoteMultiple = 1, Code = 0x400 },
        new() { Name = "Devil Disc", Accuracy = 66.6, NoteMultiple = 5, Code = 0x40A },
        new() { Name = "Rainbow MAX", Accuracy = 77.7, NoteMultiple = 10, Code = 0x401 },
    ];

    /// <summary>
    /// The disc this run earned, if any. <paramref name="totalNotes"/> is the chart's note
    /// count, which gates the two discs that need a reachable percentage.
    /// </summary>
    public static AccuracyDiscRule? Earned(
        IEnumerable<AccuracyDiscRule> rules,
        double accuracy,
        int totalNotes)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (AccuracyDiscRule rule in rules)
        {
            if (rule.NoteMultiple > 1 &&
                (totalNotes <= 0 || totalNotes % rule.NoteMultiple != 0))
            {
                continue;
            }
            if (Math.Abs(accuracy - rule.Accuracy) <= Tolerance && Renders(rule.Code))
            {
                return rule;
            }
        }
        return null;
    }
}
