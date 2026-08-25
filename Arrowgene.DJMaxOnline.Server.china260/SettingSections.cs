using System.Runtime.Serialization;

namespace Arrowgene.DJMaxOnline.Server.China260;

/// <summary>
/// Password hashing cost and length limits. These are deployment policy, not protocol:
/// nothing the client sends depends on them, so they are safe to tune per install.
///
/// Iterations and salt size only affect credentials created AFTER the change - every
/// credential stores the iteration count it was hashed with, so raising the cost does not
/// invalidate existing accounts; they simply keep verifying at their recorded cost until
/// the password is next set.
/// </summary>
public sealed class PasswordPolicySetting
{
    /// <summary>PBKDF2 iteration count. OWASP's 2023 floor for PBKDF2-SHA256 is 600,000.</summary>
    [DataMember(Order = 1)] public int Iterations { get; set; } = 600_000;

    [DataMember(Order = 2)] public int SaltSize { get; set; } = 16;

    [DataMember(Order = 3)] public int MinimumLength { get; set; } = 10;

    [DataMember(Order = 4)] public int MaximumLength { get; set; } = 128;

    /// <summary>
    /// Clamps every field to something the crypto primitives will actually accept, so a
    /// hand-edited settings file cannot produce a zero-iteration hash or a negative salt.
    /// </summary>
    public PasswordPolicySetting Validated() => new()
    {
        Iterations = Math.Max(1_000, Iterations),
        SaltSize = Math.Clamp(SaltSize, 8, 64),
        MinimumLength = Math.Clamp(MinimumLength, 1, 1024),
        MaximumLength = Math.Clamp(Math.Max(MaximumLength, MinimumLength), 1, 4096)
    };

    // Deliberately NOT configurable: the login ticket's length is derived from its random
    // byte count and independently re-validated by shape, so a setting that moved one
    // without the other would silently reject every ticket. Its LIFETIME is the real
    // policy knob - see Setting.LoginTicketLifetimeSeconds.
}

/// <summary>
/// How a finished song converts into money and experience. Pure server policy - the retail
/// economy is not encoded anywhere in the client - except that the level-up THRESHOLDS
/// cannot move, because the client draws its EXP bar from its own copy of that table.
/// </summary>
public sealed class RewardRateSetting
{
    [DataMember(Order = 1)] public uint MoneyPerNoteHit { get; set; } = 1;

    /// <summary>
    /// Experience is one point per this many notes hit. The client's curve is shallow early
    /// (40/60/80/100 for levels 1-4) but climbs hard - level 30 wants 27,440 - so a rate
    /// tuned for the first few levels stalls later.
    /// </summary>
    [DataMember(Order = 2)] public uint NotesHitPerExperience { get; set; } = 3;

    [DataMember(Order = 3)] public uint FullComboMoneyBonus { get; set; } = 100;

    [DataMember(Order = 4)] public uint FullComboExperienceBonus { get; set; } = 50;

    /// <summary>A failed run still pays, at this fraction, so practice is not worthless.</summary>
    [DataMember(Order = 5)] public uint FailureDivisor { get; set; } = 2;

    public RewardRateSetting Validated() => new()
    {
        MoneyPerNoteHit = MoneyPerNoteHit,
        NotesHitPerExperience = Math.Max(1, NotesHitPerExperience),
        FullComboMoneyBonus = FullComboMoneyBonus,
        FullComboExperienceBonus = FullComboExperienceBonus,
        FailureDivisor = Math.Max(1, FailureDivisor)
    };
}
