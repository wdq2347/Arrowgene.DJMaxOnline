using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Arrowgene.DJMaxOnline.Server;

/// <summary>One song of a course, already in the form the server serves charts in.</summary>
/// <param name="DiscId">Zero-based disc index, i.e. the catalog id minus one.</param>
/// <param name="Difficulty">Zero-based difficulty.</param>
public sealed record CourseStage(uint DiscId, byte Difficulty);

/// <summary>
/// One clear condition from a course's [Clear] block, e.g. <c>Correct = 80,1</c>.
/// </summary>
/// <param name="Value">Threshold; the condition is inactive when this is zero.</param>
/// <param name="AtLeast">
/// The file's trailing flag. The client's own evaluation in sub_4963E3 is
/// <c>atLeast ? actual &gt;= value : actual &lt; value</c>, and the server matches it so its
/// verdict always agrees with the SUCCESS/FAIL rows the player is shown.
/// </param>
public sealed record CourseCondition(uint Value, bool AtLeast)
{
    public static readonly CourseCondition Inactive = new(0, true);

    /// <summary>Accuracy uses 1, not 0, as its "no condition" value.</summary>
    public bool IsActive(uint inactiveValue = 0) =>
        Value != 0 && Value != inactiveValue;

    public bool IsMet(double actual) => AtLeast ? actual >= Value : actual < Value;
}

/// <summary>A course's [Clear] objectives, judged against the final stage's result.</summary>
public sealed record CourseObjectives(
    CourseCondition Accuracy,
    CourseCondition Score,
    CourseCondition Breaks,
    CourseCondition Combo)
{
    public static readonly CourseObjectives None = new(
        CourseCondition.Inactive, CourseCondition.Inactive,
        CourseCondition.Inactive, CourseCondition.Inactive);
}

/// <summary>
/// One <c>Itemnum = id,chance</c> line from [ClearRes].
///
/// The second field is a DROP CHANCE IN PERCENT, not a quantity. Across all 62 courses in
/// the script it never sums past 100 (five sum to exactly 100, seventeen to 50) and its
/// values are 1/2/3/5/8/10/15/20/25/30/40/50/60 - drop rates, not counts. The shortfall to
/// 100 is the documented "X: no item acquisition" outcome. Reading it as a count handed
/// out 30 HP boosters for clearing the very first course.
/// </summary>
public sealed record CourseItemReward(ushort CatalogId, ushort ChancePercent);

/// <summary>
/// A course's [ClearRes] payout. Max/Exp are percentages of what the run earned; Items are
/// granted outright. <c>Itemnum</c> repeats within the section, so it is the one key that
/// must collect every occurrence instead of keeping the first.
/// </summary>
public sealed record CourseRewards(
    uint MoneyPercent,
    uint ExperiencePercent,
    IReadOnlyList<CourseItemReward> Items,
    ushort DiscCode = 0)
{
    public static readonly CourseRewards None = new(0, 0, []);

    /// <summary>
    /// Whether <see cref="DiscCode"/> is a code the PRIZE/COLLECTION block can carry.
    /// sub_465AD1 reads that block as {code:u16, value:u16} pairs where 0x400..0x43F is
    /// the event/disc award range - and every DiscNum in the script (1056..) falls inside
    /// it, which is what identifies a reward "disc" as a collection entry.
    /// </summary>
    public bool HasDisc => DiscCode is >= 0x400 and <= 0x43F;
}

public sealed record CourseDefinition(
    ushort Id,
    string Name,
    uint ClubIndex,
    bool Premium,
    uint MaxPrice,
    IReadOnlyList<CourseStage> Stages,
    CourseObjectives Objectives,
    CourseRewards Rewards,
    ushort? RequiredCourseId = null)
{
    /// <summary>
    /// Whether this course is playable given the set of courses already cleared. A course
    /// with no prerequisite is always available; the very first course of the script
    /// points at itself in the file, which would otherwise lock the whole mode.
    /// </summary>
    public bool IsUnlocked(IReadOnlySet<ushort> clearedCourseIds) =>
        RequiredCourseId is not ushort required ||
        required == Id ||
        clearedCourseIds.Contains(required);
}

/// <summary>
/// Reads System\CourseClub\CourseSection.ini, the client's own course script. Courses are
/// defined entirely client-side, so the server parses the same file to learn which song
/// each course stage plays - it has to serve the chart for a stage the client never names.
///
/// The three decrements below are not a convention this code invented; they mirror
/// sub_49039E exactly, which decrements CourseNo, ClubName, and every Songname/Songdiff
/// entry (clamping the latter two at zero) as it fills its 244-byte course records.
/// </summary>
public sealed class CourseCatalog
{
    // A quoted value is captured whole before the comment rule applies, because course
    // names legitimately contain the comment character ("Let's Begin").
    private static readonly Regex Assignment = new(
        @"^\s*(?<key>[A-Za-z]+)\s*=\s*(?:""(?<quoted>[^""]*)""|(?<value>[^']*))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<ushort, CourseDefinition> _courses;

    private CourseCatalog(string sourcePath, IReadOnlyList<CourseDefinition> courses)
    {
        SourcePath = sourcePath;
        Courses = courses;
        _courses = courses.ToDictionary(course => course.Id);
    }

    public string SourcePath { get; }
    public IReadOnlyList<CourseDefinition> Courses { get; }
    public int Count => Courses.Count;

    /// <summary>An empty catalog, used when the client's course script is not present.</summary>
    public static CourseCatalog Empty { get; } = new(string.Empty, []);

    public bool TryGet(ushort courseId, out CourseDefinition? course) =>
        _courses.TryGetValue(courseId, out course);

    /// <summary>Course ids in file order, for OnCourseListInf.</summary>
    public IReadOnlyList<ushort> Ids() =>
        Courses.Select(course => course.Id).ToArray();

    public static CourseCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string full = Path.GetFullPath(path);
        string[] lines = File.ReadAllLines(full, Encoding.GetEncoding(949));

        List<CourseDefinition> courses = [];
        Dictionary<string, string> block = [];
        List<string> repeated = [];
        bool inBlock = false;
        string section = "general";

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('{'))
            {
                inBlock = true;
                section = "general";
                block.Clear();
                repeated.Clear();
                continue;
            }

            if (trimmed.StartsWith('}'))
            {
                inBlock = false;
                if (TryBuild(block, repeated, out CourseDefinition? course))
                {
                    courses.Add(course);
                }
                continue;
            }

            // Section headers matter: key names repeat across sections with different
            // meanings - [Max] DiscNum is what a course COSTS, [ClearRes] DiscNum is what
            // it PAYS - so every key is stored qualified by the section it appeared under.
            if (trimmed.StartsWith('['))
            {
                int close = trimmed.IndexOf(']');
                if (close > 1)
                {
                    section = trimmed[1..close].Trim().ToLowerInvariant();
                }
                continue;
            }

            if (!inBlock || trimmed.Length == 0 || trimmed.StartsWith('\''))
            {
                continue;
            }

            Match match = Assignment.Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            string key = $"{section}.{match.Groups["key"].Value.ToLowerInvariant()}";
            Group quoted = match.Groups["quoted"];
            string value = quoted.Success
                ? quoted.Value
                : match.Groups["value"].Value.Trim();

            // Itemnum legitimately repeats inside [ClearRes] - a course can award several
            // items - so it accumulates. Every other key keeps its first occurrence.
            if (key == "clearres.itemnum")
            {
                repeated.Add(value);
            }
            else if (!block.ContainsKey(key))
            {
                block[key] = value;
            }
        }

        return new CourseCatalog(full, courses);
    }

    /// <summary>
    /// Searches upward from the given starting points for the client's course script.
    /// </summary>
    public static string? FindCourseScript(params string[] starts)
    {
        // DATA first: that is the server's own copy of the client data (alongside
        // ItemStock/DiscStock), so a client reinstall or repack cannot move it.
        // The old "game-info" and "client/outFiles/system.pak/..." candidates are gone:
        // the server's data all lives in DATA now, and pointing at a client install made
        // the course list depend on which client happened to be sitting beside the repo.
        string[] relatives =
        [
            Path.Combine("DATA", "CourseSection.ini"),
            Path.Combine("System", "courseclub", "CourseSection.ini")
        ];

        IEnumerable<string> roots = starts.Length == 0
            ? [Directory.GetCurrentDirectory(), AppContext.BaseDirectory]
            : starts;
        foreach (string start in roots)
        {
            for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
            {
                foreach (string relative in relatives)
                {
                    string candidate = Path.Combine(dir.FullName, relative);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static bool TryBuild(
        IReadOnlyDictionary<string, string> block,
        IReadOnlyList<string> itemRewards,
        out CourseDefinition course)
    {
        course = null!;
        if (!TryUInt(block, "general.courseno", out uint courseNo) || courseNo == 0)
        {
            return false;
        }

        uint[] songs = Numbers(block, "general.songname");
        uint[] diffs = Numbers(block, "general.songdiff");
        TryUInt(block, "general.song", out uint declared);
        if (songs.Length == 0)
        {
            return false;
        }

        // The client trusts Song for its loop bound, so a mismatch would leave it reading
        // uninitialised stages; treat the shorter of the two as the real length.
        int stageCount = declared == 0
            ? songs.Length
            : (int)Math.Min(declared, (uint)songs.Length);
        List<CourseStage> stages = [];
        for (int index = 0; index < stageCount; index++)
        {
            // Both fields are one-based in the file and decremented by the client, which
            // clamps at zero rather than wrapping.
            uint disc = songs[index] == 0 ? 0 : songs[index] - 1;
            uint diff = index < diffs.Length && diffs[index] > 0 ? diffs[index] - 1 : 0;
            stages.Add(new CourseStage(disc, (byte)Math.Min(diff, byte.MaxValue)));
        }

        TryUInt(block, "general.clubname", out uint club);
        TryUInt(block, "general.premium", out uint premium);
        TryUInt(block, "max.maxprice", out uint maxPrice);
        block.TryGetValue("general.coursename", out string? name);

        // [Prerequisite] Switch = kind,course. Every block in the script declares
        // Number = 1 and exactly one Switch, and the course is one-based like CourseNo,
        // so it needs the same decrement.
        uint[] gate = Numbers(block, "prerequisite.switch");
        ushort? requiredCourseId = gate.Length >= 2 && gate[1] > 0
            ? checked((ushort)(gate[1] - 1))
            : null;

        // [ClearRes] DiscNum is a PRIZE/COLLECTION award code, not a disc index.
        TryUInt(block, "clearres.discnum", out uint discCode);

        course = new CourseDefinition(
            checked((ushort)(courseNo - 1)),
            string.IsNullOrWhiteSpace(name) ? $"Course {courseNo}" : name,
            club == 0 ? 0 : club - 1,
            premium != 0,
            maxPrice,
            stages,
            new CourseObjectives(
                Condition(block, "clear.correct"),
                Condition(block, "clear.score"),
                Condition(block, "clear.break"),
                Condition(block, "clear.combo")),
            new CourseRewards(
                TryUInt(block, "clearres.max", out uint moneyPercent) ? moneyPercent : 0,
                TryUInt(block, "clearres.exp", out uint expPercent) ? expPercent : 0,
                ItemRewards(itemRewards),
                discCode <= ushort.MaxValue ? (ushort)discCode : (ushort)0),
            requiredCourseId);
        return true;
    }

    /// <summary>Parses the accumulated <c>Itemnum = id,count</c> lines.</summary>
    private static IReadOnlyList<CourseItemReward> ItemRewards(IReadOnlyList<string> lines)
    {
        List<CourseItemReward> rewards = [];
        foreach (string line in lines)
        {
            string[] parts = line.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 ||
                !uint.TryParse(parts[0], out uint id) ||
                !uint.TryParse(parts[1], out uint count) ||
                id == 0 || id > ushort.MaxValue || count == 0)
            {
                continue;
            }
            rewards.Add(new CourseItemReward(
                (ushort)id, (ushort)Math.Clamp(count, 0, 100)));
        }
        return rewards;
    }

    /// <summary>Reads a "value,flag" pair from the [Clear] block.</summary>
    private static CourseCondition Condition(
        IReadOnlyDictionary<string, string> block,
        string key)
    {
        uint[] parts = Numbers(block, key);
        return parts.Length == 0
            ? CourseCondition.Inactive
            : new CourseCondition(parts[0], parts.Length < 2 || parts[1] == 1);
    }

    private static bool TryUInt(
        IReadOnlyDictionary<string, string> block,
        string key,
        out uint value)
    {
        value = 0;
        return block.TryGetValue(key, out string? raw) &&
               uint.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                   out value);
    }

    private static uint[] Numbers(IReadOnlyDictionary<string, string> block, string key)
    {
        if (!block.TryGetValue(key, out string? raw))
        {
            return [];
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => uint.TryParse(part, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out uint value) ? value : 0u)
            .ToArray();
    }
}
