using System.Globalization;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

/// <summary>
/// IconSet.csv entries whose matching ItemStock.csv row has release=0. The two type-1
/// preset icons are intentionally omitted: they are default icons rather than hidden
/// event/operator content. IDs are the client's zero-based category-encoded values.
/// </summary>
public sealed record SpecialIconInfo(
    byte Type,
    byte Number,
    uint IconId,
    string Alias,
    string Description,
    bool OperatorOnly = false);

public static class SpecialIconCatalog
{
    public static readonly IReadOnlyList<SpecialIconInfo> Entries =
    [
        // IconSet type 2 / ItemStock section 2,4. All are release=0.
        new(2, 1,  0x1000, "levelup",    "event: DJ level-up goddess"),
        new(2, 2,  0x1001, "snowman",    "event: DJ snowman"),
        new(2, 3,  0x1002, "rudolph",    "event: DJ Rudolph"),
        new(2, 4,  0x1003, "santa",      "event: DJ Santa"),
        new(2, 5,  0x1004, "bear",       "event: DJ bear"),
        new(2, 6,  0x1005, "hurley",     "event: DJ Hurley"),
        new(2, 7,  0x1006, "ongame",     "OnGameNet mark"),
        new(2, 8,  0x1007, "mc-juhyun",  "OnGameNet MC Juhyun"),
        new(2, 9,  0x1008, "mc-daniel",  "OnGameNet MC Daniel"),
        new(2, 10, 0x1009, "djball",     "DJ BALL operator icon", OperatorOnly: true),
        new(2, 11, 0x100A, "done",       "V-MAX event: Done"),
        new(2, 12, 0x100B, "tungsten",   "V-MAX event: Tungsten"),
        new(2, 13, 0x100C, "noel",       "Christmas event: Noel"),
        new(2, 14, 0x100D, "golio",      "Christmas event: Golio"),

        // Other IconSet entries present in ItemStock but hidden by release=0.
        new(3, 29, 0x201C, "cosmicgirl", "unreleased cash-shop female 29"),
        new(3, 41, 0x2028, "lena",       "unreleased cash-shop female 41"),
        new(4, 17, 0xA410, "cosmicboy",  "unreleased cash-shop male 17"),
        new(4, 19, 0xA412, "outlaw",     "unreleased cash-shop male 19"),
        new(4, 23, 0xA416, "mice",       "unreleased cash-shop male 23"),
        new(5, 7,  0xA806, "snu",        "unreleased special avatar 7"),
        new(5, 10, 0xA809, "battler4",   "Battle Club fourth-place reward"),
        new(5, 11, 0xA80A, "battler3",   "Battle Club third-place reward"),
        new(5, 12, 0xA80B, "battler2",   "Battle Club second-place reward"),
        new(5, 13, 0xA80C, "battler1",   "Battle Club first-place reward")
    ];

    public static bool TryFind(string? text, out SpecialIconInfo icon)
    {
        icon = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        SpecialIconInfo? match = Entries.FirstOrDefault(entry =>
            entry.Alias.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            icon = match;
            return true;
        }

        int separator = value.IndexOf(':');
        if (separator > 0 &&
            byte.TryParse(value[..separator], NumberStyles.None,
                CultureInfo.InvariantCulture, out byte type) &&
            byte.TryParse(value[(separator + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out byte number))
        {
            match = Entries.FirstOrDefault(entry =>
                entry.Type == type && entry.Number == number);
            if (match != null)
            {
                icon = match;
                return true;
            }
        }

        NumberStyles style = NumberStyles.None;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            style = NumberStyles.AllowHexSpecifier;
        }
        if (uint.TryParse(value, style, CultureInfo.InvariantCulture, out uint iconId))
        {
            match = Entries.FirstOrDefault(entry => entry.IconId == iconId);
            if (match != null)
            {
                icon = match;
                return true;
            }
        }
        return false;
    }
}
