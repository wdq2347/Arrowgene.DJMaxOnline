using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

/// <summary>
/// Bit flags read by the retail client's account-class helpers at
/// sub_428B48 through sub_428BB1. Bit 0 is the ordinary-account value used by
/// the local profile; bits 1 through 5 enable the named privileged roles.
/// </summary>
[Flags]
public enum AccountClassFlags : uint
{
    None = 0,
    Normal = 0x01,
    Admin = 0x02,
    GameMaster = 0x04,
    Observer = 0x08,
    MasterOfCeremonies = 0x10,
    Challenger = 0x20,
    // Tested by sub_428563 and drives profile-icon element 5. Deliberately OUTSIDE the
    // 0x3E staff set, so it decorates the icon without granting privileged status.
    Jjang = 0x40,
    // Backwards-compatible name retained for existing callers/profiles.
    Badge40 = Jjang,

    // Subscription bits. The lobby profile card shows the "Premium" label instead of the
    // credit balance when either of these is set (client sub_428DCC / sub_428DE6).
    Premium = 0x10000,
    PremiumAlternate = 0x20000,
    // Counted by the client's tier test (sub_428E1A masks 0x70000) but NOT sufficient on
    // its own to show Premium — the second half of the check ignores it.
    Credit = 0x40000,
    TierBit18 = Credit,
    // Adds an extra badge element to the profile icon (client sub_428534).
    PcBang = 0x80000,
    IconBadge = PcBang
}

public static class AccountClassInfo
{
    public const uint PrivilegedMask =
        (uint)(AccountClassFlags.Admin |
               AccountClassFlags.GameMaster |
               AccountClassFlags.Observer |
               AccountClassFlags.MasterOfCeremonies |
               AccountClassFlags.Challenger);

    /// <summary>
    /// Turns named flags into the packed account-class value, so a config or a DB row can
    /// carry readable switches instead of a hand-computed number. Names match
    /// <see cref="AccountClassFlags"/> and are case-insensitive; "Premium" is the one most
    /// deployments want.
    /// </summary>
    /// <param name="unknown">Names that matched no flag, for the caller to report.</param>
    public static uint FromNames(
        IEnumerable<string>? names,
        out IReadOnlyList<string> unknown)
    {
        List<string> bad = [];
        uint value = 0;
        foreach (string name in names ?? [])
        {
            string trimmed = name.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (Enum.TryParse(trimmed, ignoreCase: true, out AccountClassFlags flag) &&
                Enum.IsDefined(typeof(AccountClassFlags), flag))
            {
                value |= (uint)flag;
            }
            else
            {
                bad.Add(trimmed);
            }
        }
        unknown = bad;
        return value;
    }

    /// <summary>The flag names a packed value carries, for logging and diagnostics.</summary>
    public static IReadOnlyList<string> ToNames(uint accountClass) =>
        [.. Enum.GetValues<AccountClassFlags>()
            .Where(flag => flag != AccountClassFlags.None &&
                           (accountClass & (uint)flag) == (uint)flag)
            .Select(flag => flag.ToString())
            .Distinct()];

    /// <summary>
    /// The individually-addressable flags, in a fixed order, each with the snake_case name
    /// its own database column uses. This is what lets an account's privileges be set by
    /// flipping a column to 1 instead of packing a bitmask by hand.
    /// </summary>
    public static readonly IReadOnlyList<(string Column, AccountClassFlags Flag)> Columns =
    [
        ("class_normal", AccountClassFlags.Normal),
        ("class_admin", AccountClassFlags.Admin),
        ("class_game_master", AccountClassFlags.GameMaster),
        ("class_observer", AccountClassFlags.Observer),
        ("class_master_of_ceremonies", AccountClassFlags.MasterOfCeremonies),
        ("class_challenger", AccountClassFlags.Challenger),
        ("class_jjang", AccountClassFlags.Jjang),
        ("class_premium", AccountClassFlags.Premium),
        ("class_premium_alternate", AccountClassFlags.PremiumAlternate),
        ("class_credit", AccountClassFlags.Credit),
        ("class_pc_bang", AccountClassFlags.PcBang),
    ];

    /// <summary>Every bit whose client-side effect is known, so the parser accepts it.</summary>
    public const uint KnownMask =
        (uint)(AccountClassFlags.Normal | AccountClassFlags.Jjang) |
        PrivilegedMask | SubscriptionMask;

    /// <summary>
    /// Admin|GameMaster. The client tests this pair on its own (sub_428573 with an
    /// accountClass argument, sub_428EAD reading it directly) as a coarser "operator"
    /// check than the full 0x3E staff mask.
    /// </summary>
    public const uint OperatorMask =
        (uint)(AccountClassFlags.Admin | AccountClassFlags.GameMaster);

    public const uint SubscriptionPremiumMask =
        (uint)(AccountClassFlags.Premium | AccountClassFlags.PremiumAlternate);

    public const uint SubscriptionMask =
        (uint)(AccountClassFlags.Premium |
               AccountClassFlags.PremiumAlternate |
               AccountClassFlags.Credit |
               AccountClassFlags.PcBang);

    /// <summary>
    /// Exactly what the client requires to render the profile card's "Premium" label in
    /// place of the credit balance (sub_405790):
    ///   sub_42922E() &amp;&amp; (sub_428DCC() || sub_428DE6() || sub_428E59())
    /// = ((ac &amp; 0x70000) || (ac &amp; 0x3E)) &amp;&amp; ((ac &amp; 0x30000) || (ac &amp; 0x3E))
    /// which reduces to a single mask. Note any PRIVILEGED bit (0x3E) satisfies it, so a
    /// staff account shows Premium as a side effect; 0x40000 alone does not.
    /// </summary>
    public const uint PremiumMask =
        PrivilegedMask |
        (uint)(AccountClassFlags.Premium | AccountClassFlags.PremiumAlternate);

    public static bool IsPrivileged(uint value) =>
        (value & PrivilegedMask) != 0;

    /// <summary>
    /// True only for an authenticated account carrying the dedicated administrator bit.
    /// Broader staff roles deliberately do not satisfy server-administration checks.
    /// </summary>
    public static bool IsAdmin(uint value) =>
        (value & (uint)AccountClassFlags.Admin) != 0;

    public static bool IsPremium(uint value) =>
        (value & PremiumMask) != 0;

    public static bool IsOperator(uint value) =>
        (value & OperatorMask) != 0;

    /// <summary>
    /// Which profile-icon elements the client will show, per sub_461DC5. Element 2 and
    /// element 3 are mutually exclusive: element 3 appears ONLY when 0x40000 is set and
    /// neither a premium bit nor a staff bit is, so adding premium/staff hides it again.
    /// </summary>
    public static string DescribeBadges(uint value)
    {
        bool premium = (value & SubscriptionPremiumMask) != 0;
        bool staff = IsPrivileged(value);
        bool tier = (value & (uint)AccountClassFlags.Credit) != 0;

        List<string> elements = [];
        if (premium || staff)
        {
            elements.Add("2 (premium/staff)");
        }
        else if (tier)
        {
            elements.Add("3 (credit only)");
        }
        if ((value & (uint)AccountClassFlags.PcBang) != 0)
        {
            elements.Add("4 (PC bang)");
        }
        if ((value & (uint)AccountClassFlags.Jjang) != 0)
        {
            elements.Add("5 (jjang)");
        }
        if ((value & (uint)AccountClassFlags.Challenger) != 0)
        {
            elements.Add("6 (challenger)");
        }
        return elements.Count == 0 ? "none" : string.Join(", ", elements);
    }

    public static bool TryParse(string text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                       trimmed[2..],
                       NumberStyles.AllowHexSpecifier,
                       CultureInfo.InvariantCulture,
                       out value) &&
                   (value & ~KnownMask) == 0;
        }
        if (uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture,
                out value))
        {
            return (value & ~KnownMask) == 0;
        }

        string[] names = trimmed.Split(
            [',', '+', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            return false;
        }

        foreach (string name in names)
        {
            uint flag = name.ToLowerInvariant() switch
            {
                "none" => (uint)AccountClassFlags.None,
                "normal" => (uint)AccountClassFlags.Normal,
                "admin" => (uint)AccountClassFlags.Admin,
                "gm" or "gamemaster" => (uint)AccountClassFlags.GameMaster,
                "observer" => (uint)AccountClassFlags.Observer,
                "mc" or "masterofceremonies" =>
                    (uint)AccountClassFlags.MasterOfCeremonies,
                "challenger" => (uint)AccountClassFlags.Challenger,
                "jjang" or "badge40" => (uint)AccountClassFlags.Jjang,
                "operator" => OperatorMask,
                "premium" => (uint)AccountClassFlags.Premium,
                "premium2" or "premiumalt" or "premiumalternate" =>
                    (uint)AccountClassFlags.PremiumAlternate,
                "credit" or "tier18" => (uint)AccountClassFlags.Credit,
                "pcbang" or "pc" or "badge" => (uint)AccountClassFlags.PcBang,
                "staff" or "all" => PrivilegedMask,
                _ => uint.MaxValue
            };
            if (flag == uint.MaxValue)
            {
                value = 0;
                return false;
            }
            value |= flag;
        }
        return true;
    }

    public static string Format(uint value)
    {
        if (value == 0)
        {
            return "none";
        }

        List<string> names = [];
        Add(AccountClassFlags.Normal, "normal");
        Add(AccountClassFlags.Admin, "admin");
        Add(AccountClassFlags.GameMaster, "gm");
        Add(AccountClassFlags.Observer, "observer");
        Add(AccountClassFlags.MasterOfCeremonies, "mc");
        Add(AccountClassFlags.Challenger, "challenger");
        Add(AccountClassFlags.Jjang, "jjang");
        Add(AccountClassFlags.Premium, "premium");
        Add(AccountClassFlags.PremiumAlternate, "premium2");
        Add(AccountClassFlags.Credit, "credit");
        Add(AccountClassFlags.PcBang, "pcbang");
        uint unknown = value & ~KnownMask;
        if (unknown != 0)
        {
            names.Add($"unknown:0x{unknown:X}");
        }
        return string.Join("+", names);

        void Add(AccountClassFlags flag, string name)
        {
            if ((value & (uint)flag) != 0)
            {
                names.Add(name);
            }
        }
    }
}

/// <summary>
/// Keeps the wire-facing account class as a uint while making player.json editable by
/// named client features. Legacy numeric values remain accepted.
/// </summary>
public sealed class AccountClassJsonConverter : JsonConverter<uint>
{
    public override uint Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt32(out uint number))
        {
            return number;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string text = reader.GetString() ?? string.Empty;
            if (AccountClassInfo.TryParse(text, out uint value))
            {
                return value;
            }
            throw new JsonException($"Invalid accountClass value '{text}'.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                "accountClass must be a legacy uint, a named value, or an object of named flags.");
        }

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        uint result = 0;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("unknownBits", StringComparison.OrdinalIgnoreCase))
            {
                result |= ReadRawUInt32(property.Value, property.Name);
                continue;
            }

            if (!AccountClassInfo.TryParse(property.Name, out uint flag) || flag == 0)
            {
                throw new JsonException($"Unknown accountClass element '{property.Name}'.");
            }
            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException(
                    $"accountClass element '{property.Name}' must be true or false.");
            }

            if (property.Value.GetBoolean())
            {
                result |= flag;
            }
        }
        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        uint value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteFlag("normal", AccountClassFlags.Normal);
        WriteFlag("admin", AccountClassFlags.Admin);
        WriteFlag("gameMaster", AccountClassFlags.GameMaster);
        WriteFlag("observer", AccountClassFlags.Observer);
        WriteFlag("masterOfCeremonies", AccountClassFlags.MasterOfCeremonies);
        WriteFlag("challenger", AccountClassFlags.Challenger);
        WriteFlag("jjang", AccountClassFlags.Jjang);
        WriteFlag("premium", AccountClassFlags.Premium);
        WriteFlag("premiumAlternate", AccountClassFlags.PremiumAlternate);
        WriteFlag("credit", AccountClassFlags.Credit);
        WriteFlag("pcBang", AccountClassFlags.PcBang);
        writer.WriteNumber("unknownBits", value & ~AccountClassInfo.KnownMask);
        writer.WriteEndObject();

        void WriteFlag(string name, AccountClassFlags flag) =>
            writer.WriteBoolean(name, (value & (uint)flag) != 0);
    }

    private static uint ReadRawUInt32(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out uint number))
        {
            return number;
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            string text = element.GetString() ?? string.Empty;
            NumberStyles style = NumberStyles.None;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
                style = NumberStyles.AllowHexSpecifier;
            }
            if (uint.TryParse(text, style, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }
        throw new JsonException($"accountClass {name} must be a uint or 0x-prefixed string.");
    }
}
