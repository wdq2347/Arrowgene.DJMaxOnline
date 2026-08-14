using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Arrowgene.DJMaxOnline.Server;

public enum ShopCurrency : byte
{
    Cash = 0,
    Money = 1,
    AwardPoints = 2
}

public enum ShopListingKind
{
    Regular,
    InGame,
    Award
}

/// <summary>One authoritative row from System/shop/ItemStock.csv.</summary>
public sealed record ShopItemDefinition(
    ushort CatalogId,
    byte Section1,
    byte Section2,
    byte Section3,
    string Name,
    ushort WItem,
    ushort Base,
    ushort Count,
    bool Countable,
    uint Price,
    ShopCurrency Currency,
    uint ChargingMaximum,
    uint ResalePrice,
    byte RequiredLevel,
    int Experience,
    int Max,
    int Hp,
    decimal HpBoost,
    decimal JudgmentBoost,
    ushort ExpireDays,
    byte EquipSlot,
    byte Alpha,
    bool Released)
{
    public uint PackedItemId => ((uint)Count << 16) | CatalogId;

    // ItemStock uses 99 as its unrestricted sentinel. Actual level gates are 0..98.
    public bool IsAllowedAtLevel(uint level) => RequiredLevel == 99 || level >= RequiredLevel;
}

public sealed record ShopListing(
    ushort CatalogId,
    ShopListingKind Kind,
    string SourceFile);

/// <summary>
/// Loads the retail shop catalog and the exact client-visible Goods lists. ItemStock
/// is authoritative for price/effects while list membership and release determine
/// whether a normal purchase request is legal. ItemSetInfo expands discounted set
/// products into their real gear/note components.
/// </summary>
public sealed class ShopCatalog
{
    private sealed record ListSpec(string FileName, ushort Base, ShopListingKind Kind);

    private static readonly ListSpec[] ListSpecs =
    [
        new("Goods_Musicshop_Coin.lst", 0x0300, ShopListingKind.Regular),
        new("Goods_Musicshop_Premium.lst", 0x0380, ShopListingKind.Regular),
        new("Goods_Avata_Woman.lst", 0x0400, ShopListingKind.Regular),
        new("Goods_Avata_Man.lst", 0x0800, ShopListingKind.Regular),
        new("Goods_Avata_Special.lst", 0x0C00, ShopListingKind.Regular),
        new("Goods_Avata_Makeup.lst", 0x1000, ShopListingKind.Regular),
        new("Goods_Avata_Slot.lst", 0x1400, ShopListingKind.Regular),
        new("Goods_Item_Battle.lst", 0x1800, ShopListingKind.Regular),
        new("Goods_Item_Special.lst", 0x1C00, ShopListingKind.Regular),
        // 0x1D00 is correct - confirmed against the client's own base table sub_4712A1,
        // where goods type 8 subList 2 yields 0x9D00 and the >= 5 branch subtracts 0x8000.
        // Its single product (0x1D01) has no ItemStock row, so the CLIENT drops it too;
        // that is missing client data, not a wrong base. Do not "fix" this to 0x2000.
        new("Goods_Item_Special2.lst", 0x1D00, ShopListingKind.Regular),
        new("Goods_Equip_Gear.lst", 0x2400, ShopListingKind.Regular),
        new("Goods_Equip_RythmNote.lst", 0x2800, ShopListingKind.Regular),
        new("Goods_Equip_GearSet.lst", 0x2C00, ShopListingKind.Regular),
        new("Goods_Equip_PlayQue.lst", 0x3000, ShopListingKind.Regular),
        new("Goods_Equip_Effect.lst", 0x3400, ShopListingKind.Regular),

        new("IG_Goods_Musicshop_Coin.lst", 0x8300, ShopListingKind.InGame),
        new("IG_Goods_Musicshop_Premium.lst", 0x8380, ShopListingKind.InGame),
        new("IG_Goods_Avata_Woman.lst", 0x8400, ShopListingKind.InGame),
        new("IG_Goods_Avata_Man.lst", 0x8800, ShopListingKind.InGame),
        new("IG_Goods_Avata_Special.lst", 0x8C00, ShopListingKind.InGame),
        new("IG_Goods_Avata_Makeup.lst", 0x9000, ShopListingKind.InGame),
        new("IG_Goods_Avata_Slot.lst", 0x9400, ShopListingKind.InGame),
        new("IG_Goods_Item_Battle.lst", 0x9800, ShopListingKind.InGame),
        new("IG_Goods_Item_Special.lst", 0x9C00, ShopListingKind.InGame),
        new("IG_Goods_Item_Special2.lst", 0x9D00, ShopListingKind.InGame),
        new("IG_Goods_Equip_Gear.lst", 0xA400, ShopListingKind.InGame),
        new("IG_Goods_Equip_RythmNote.lst", 0xA800, ShopListingKind.InGame),
        new("IG_Goods_Equip_GearSet.lst", 0xAC00, ShopListingKind.InGame),
        new("IG_Goods_Equip_PlayQue.lst", 0xB000, ShopListingKind.InGame),
        new("IG_Goods_Equip_Effect.lst", 0xB400, ShopListingKind.InGame),

        new("IG_Award_HPBooster.lst", 0xF400, ShopListingKind.Award),
        new("IG_Award_ExpBooster.lst", 0xF480, ShopListingKind.Award),
        new("IG_Award_MaxBooster.lst", 0xF500, ShopListingKind.Award),
        new("IG_Award_HPRecovery.lst", 0xF580, ShopListingKind.Award),
        new("IG_Award_Sight.lst", 0xF600, ShopListingKind.Award)
    ];

    private static readonly Regex NumberPattern = new(
        @"^\s*Number\s*=\s*(-?\d+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<ushort, ShopItemDefinition> _items;
    private readonly IReadOnlyDictionary<ushort, ShopListing> _listings;
    private readonly IReadOnlyDictionary<ushort, IReadOnlyList<ushort>> _sets;

    private ShopCatalog(
        string dataDirectory,
        IDictionary<ushort, ShopItemDefinition> items,
        IDictionary<ushort, ShopListing> listings,
        IDictionary<ushort, IReadOnlyList<ushort>> sets,
        IReadOnlyList<ushort> danglingListings)
    {
        DataDirectory = dataDirectory;
        _items = new ReadOnlyDictionary<ushort, ShopItemDefinition>(items);
        _listings = new ReadOnlyDictionary<ushort, ShopListing>(listings);
        _sets = new ReadOnlyDictionary<ushort, IReadOnlyList<ushort>>(sets);
        DanglingListings = danglingListings;
    }

    public string DataDirectory { get; }
    public int ItemCount => _items.Count;
    public int ListingCount => _listings.Count;
    public int SetCount => _sets.Count;
    public IEnumerable<ShopItemDefinition> Items => _items.Values.OrderBy(item => item.CatalogId);
    public IReadOnlyList<ushort> DanglingListings { get; }

    public static ShopCatalog Load(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        dataDirectory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Shop data directory was not found: {dataDirectory}");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(949);
        Dictionary<ushort, ShopItemDefinition> items = LoadItems(
            Path.Combine(dataDirectory, "ItemStock.csv"), encoding);
        Dictionary<ushort, ShopListing> listings = LoadListings(dataDirectory, encoding);
        Dictionary<ushort, IReadOnlyList<ushort>> sets = LoadSets(
            Path.Combine(dataDirectory, "ItemSetInfo.csv"), items);
        ushort[] dangling = listings.Keys
            .Where(id => !items.ContainsKey(id))
            .Order()
            .ToArray();
        return new ShopCatalog(dataDirectory, items, listings, sets, dangling);
    }

    public static string? FindDataDirectory(params string[] starts)
    {
        IEnumerable<string> roots = starts.Length == 0
            ? [Directory.GetCurrentDirectory(), AppContext.BaseDirectory]
            : starts;
        foreach (string start in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            for (DirectoryInfo? directory = new(Path.GetFullPath(start));
                 directory != null;
                 directory = directory.Parent)
            {
                if (IsDataDirectory(directory.FullName))
                {
                    return directory.FullName;
                }

                string child = Path.Combine(directory.FullName, "DATA");
                if (IsDataDirectory(child))
                {
                    return child;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// ItemStock section1 of the consumable boosters: 5/1 HP, 5/2 EXP, 5/3 MAX,
    /// 5/4 HP_RECOVERY, 5/5 SIGHT. Unlike avatars and gear these are used rather than
    /// worn, so they arm for one song instead of contributing while equipped.
    /// </summary>
    public const byte ConsumableSection = 5;

    public bool TryGet(ushort catalogId, out ShopItemDefinition item) =>
        _items.TryGetValue(catalogId, out item!);

    public ShopItemDefinition Get(ushort catalogId) =>
        TryGet(catalogId, out ShopItemDefinition? item)
            ? item
            : throw new KeyNotFoundException($"Unknown shop item 0x{catalogId:X4}.");

    public bool TryGetListing(ushort catalogId, out ShopListing listing) =>
        _listings.TryGetValue(catalogId, out listing!);

    public bool IsNormalPurchase(ShopItemDefinition item)
    {
        if (!item.Released || !_listings.TryGetValue(item.CatalogId, out ShopListing? listing))
        {
            return false;
        }

        return listing.Kind switch
        {
            ShopListingKind.Regular => item.Currency == ShopCurrency.Cash,
            ShopListingKind.InGame => item.Currency == ShopCurrency.Money,
            ShopListingKind.Award => false,
            _ => false
        };
    }

    public bool TryGetSetParts(ushort catalogId, out IReadOnlyList<ushort> parts) =>
        _sets.TryGetValue(catalogId, out parts!);

    private static bool IsDataDirectory(string path) =>
        File.Exists(Path.Combine(path, "ItemStock.csv")) &&
        File.Exists(Path.Combine(path, "ItemSetInfo.csv"));

    private static Dictionary<ushort, ShopItemDefinition> LoadItems(
        string path,
        Encoding encoding)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ItemStock.csv was not found.", path);
        }

        string[] lines = File.ReadAllLines(path, encoding);
        if (lines.Length < 2)
        {
            throw new InvalidDataException($"Shop catalog {path} contains no records.");
        }

        string[] expectedHeader =
        [
            "section1", "section2", "section3", "naming", "wItem", "base",
            "wCount", "countable", "price", "pricetype", "chargingmax",
            "resellingprice", "level", "exp", "max", "hp", "hpboost",
            "judgmentboost", "expire", "equip", "alpha", "release"
        ];
        string[] header = ParseCsvLine(lines[0]);
        if (!header.SequenceEqual(expectedHeader, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Shop catalog {path} has an unexpected header.");
        }

        Dictionary<ushort, ShopItemDefinition> result = [];
        for (int index = 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            string[] fields = ParseCsvLine(lines[index]);
            if (fields.Length != expectedHeader.Length)
            {
                throw new InvalidDataException(
                    $"ItemStock row {index + 1} has {fields.Length} fields; " +
                    $"expected {expectedHeader.Length}.");
            }

            ushort wItem = ParseUInt16(fields[4], index + 1, "wItem");
            ushort itemBase = ParseUInt16(fields[5], index + 1, "base");
            uint catalogValue = (uint)wItem + itemBase;
            if (catalogValue >= ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"ItemStock row {index + 1} produces invalid id 0x{catalogValue:X}.");
            }
            ushort catalogId = (ushort)catalogValue;
            ShopCurrency currency = ParseByte(fields[9], index + 1, "pricetype") switch
            {
                0 => ShopCurrency.Cash,
                1 => ShopCurrency.Money,
                2 => ShopCurrency.AwardPoints,
                byte value => throw new InvalidDataException(
                    $"ItemStock row {index + 1} has unknown price type {value}.")
            };

            ShopItemDefinition item = new(
                catalogId,
                ParseByte(fields[0], index + 1, "section1"),
                ParseByte(fields[1], index + 1, "section2"),
                ParseByte(fields[2], index + 1, "section3"),
                fields[3],
                wItem,
                itemBase,
                ParseUInt16(fields[6], index + 1, "wCount"),
                ParseBoolean(fields[7], index + 1, "countable"),
                ParseUInt32(fields[8], index + 1, "price"),
                currency,
                ParseUInt32(fields[10], index + 1, "chargingmax"),
                ParseUInt32(fields[11], index + 1, "resellingprice"),
                ParseByte(fields[12], index + 1, "level"),
                ParseInt32(fields[13], index + 1, "exp"),
                ParseInt32(fields[14], index + 1, "max"),
                ParseInt32(fields[15], index + 1, "hp"),
                ParseDecimal(fields[16], index + 1, "hpboost"),
                ParseDecimal(fields[17], index + 1, "judgmentboost"),
                ParseUInt16(fields[18], index + 1, "expire"),
                ParseByte(fields[19], index + 1, "equip"),
                ParseByte(fields[20], index + 1, "alpha"),
                ParseBoolean(fields[21], index + 1, "release"));
            if (item.Count == 0)
            {
                throw new InvalidDataException(
                    $"ItemStock row {index + 1} has a zero wCount.");
            }
            if (!result.TryAdd(catalogId, item))
            {
                throw new InvalidDataException(
                    $"ItemStock contains duplicate canonical id 0x{catalogId:X4}.");
            }
        }
        return result;
    }

    private static Dictionary<ushort, ShopListing> LoadListings(
        string dataDirectory,
        Encoding encoding)
    {
        Dictionary<ushort, ShopListing> result = [];
        foreach (ListSpec spec in ListSpecs)
        {
            string path = Path.Combine(dataDirectory, spec.FileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Required client shop list {spec.FileName} was not found.", path);
            }

            string text = File.ReadAllText(path, encoding);
            foreach (Match match in NumberPattern.Matches(text))
            {
                int number = ParseInt32(match.Groups[1].Value, 0, "Number");
                int value = spec.Base + number;
                if (number == 0 || value <= 0 || value >= ushort.MaxValue)
                {
                    throw new InvalidDataException(
                        $"Shop list {spec.FileName} has invalid Number {number}.");
                }
                ushort catalogId = (ushort)value;
                ShopListing listing = new(catalogId, spec.Kind, spec.FileName);
                if (result.TryAdd(catalogId, listing))
                {
                    continue;
                }

                ShopListing existing = result[catalogId];
                if (existing.Kind == listing.Kind)
                {
                    throw new InvalidDataException(
                        $"Shop item 0x{catalogId:X4} appears more than once in {listing.Kind} Goods lists.");
                }

                // The custom MAX catalog deliberately references the original retail
                // ID so the client selects that item's real icon group. Keep one
                // purchase identity and prefer the in-game listing/currency policy.
                if (listing.Kind == ShopListingKind.InGame)
                {
                    result[catalogId] = listing;
                }
            }
        }
        return result;
    }

    private static Dictionary<ushort, IReadOnlyList<ushort>> LoadSets(
        string path,
        IReadOnlyDictionary<ushort, ShopItemDefinition> items)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ItemSetInfo.csv was not found.", path);
        }

        string[] lines = File.ReadAllLines(path, Encoding.ASCII);
        string[] expectedHeader =
        ["set_wItem", "part1_wItem", "part2_wItem", "part3_wItem", "part4_wItem", "part5_wItem"];
        if (lines.Length < 2 ||
            !ParseCsvLine(lines[0]).SequenceEqual(expectedHeader, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Set catalog {path} has an unexpected header.");
        }

        Dictionary<ushort, IReadOnlyList<ushort>> result = [];
        for (int index = 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }
            string[] fields = ParseCsvLine(lines[index]);
            if (fields.Length != expectedHeader.Length)
            {
                throw new InvalidDataException(
                    $"ItemSetInfo row {index + 1} has {fields.Length} fields.");
            }

            ushort setId = ParseUInt16(fields[0], index + 1, "set_wItem");
            ushort[] parts = fields.Skip(1)
                .Select(value => ParseUInt16(value, index + 1, "part_wItem"))
                .Where(value => value != 0)
                .ToArray();
            if (parts.Length == 0 || !items.ContainsKey(setId) || parts.Any(id => !items.ContainsKey(id)))
            {
                throw new InvalidDataException(
                    $"ItemSetInfo row {index + 1} references an unknown or empty item.");
            }
            if (!result.TryAdd(setId, Array.AsReadOnly(parts)))
            {
                throw new InvalidDataException(
                    $"ItemSetInfo contains duplicate set 0x{setId:X4}.");
            }
        }
        return result;
    }

    private static string[] ParseCsvLine(string line)
    {
        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }
        if (quoted)
        {
            throw new InvalidDataException("CSV line contains an unterminated quote.");
        }
        fields.Add(field.ToString());
        return fields.ToArray();
    }

    private static bool ParseBoolean(string value, int row, string name) =>
        ParseByte(value, row, name) switch
        {
            0 => false,
            1 => true,
            byte parsed => throw new InvalidDataException(
                $"Catalog row {row} has invalid {name} flag {parsed}.")
        };

    private static byte ParseByte(string value, int row, string name) =>
        byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out byte parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Catalog row {row} has invalid {name} value '{value}'.");

    private static ushort ParseUInt16(string value, int row, string name) =>
        ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ushort parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Catalog row {row} has invalid {name} value '{value}'.");

    private static uint ParseUInt32(string value, int row, string name) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Catalog row {row} has invalid {name} value '{value}'.");

    private static int ParseInt32(string value, int row, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Catalog row {row} has invalid {name} value '{value}'.");

    private static decimal ParseDecimal(string value, int row, string name) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Catalog row {row} has invalid {name} value '{value}'.");
}
