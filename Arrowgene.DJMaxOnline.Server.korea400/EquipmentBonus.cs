using System.Buffers.Binary;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// How much of the item catalog's stats the server actually hands out, per stat, as a
/// percentage. Every one of these is server-authored - the client neither reads nor
/// enforces them - so this is the whole balance surface for equipment, and none of it
/// needs a change to the client's ItemStock.csv.
/// </summary>
/// <param name="HpPercent">Starting-gauge bonus. 0 makes gear cosmetic.</param>
/// <param name="MaxPercent">Bonus MAX (money) credited per song.</param>
/// <param name="ExperiencePercent">Bonus EXP credited per song.</param>
public readonly record struct EquipmentBonusScale(
    int HpPercent,
    int MaxPercent,
    int ExperiencePercent)
{
    public static readonly EquipmentBonusScale Full = new(100, 100, 100);
}

/// <summary>
/// What a player's equipped loadout and armed boosters are worth for one song.
///
/// ItemStock carries five per-item stats and they land in three different places:
/// <list type="bullet">
///   <item><c>hp</c> and <c>hpboost</c> - the starting gauge, sent as
///     OnUseMountItemInf wire+4 (see <see cref="Packets.MountItemState"/>).</item>
///   <item><c>max</c> - MAX is the currency, so it is a bonus on the money the SERVER
///     credits at the end of a song.</item>
///   <item><c>exp</c> - likewise a bonus on the experience credited.</item>
/// </list>
/// <c>judgmentboost</c> (the SIGHT consumables) is absent from this record on purpose: it
/// is not a payout, it widens the judgment WINDOWS, which travel with the chart itself.
/// See <see cref="JudgmentWindows"/> and <c>LocalLobby.JudgmentBoostFor</c>.
/// </summary>
public sealed record EquipmentBonus(ushort Hp, uint Money, uint Experience)
{
    public static readonly EquipmentBonus None = new(0, 0, 0);

    /// <summary>
    /// Whether anything the server can deliver would come of using this item.
    ///
    /// Guards against burning an item for nothing. Every stat now has a delivery path -
    /// <c>judgmentboost</c> rides in the chart's own judgment windows - so this only bites
    /// if a future item carries a stat the server cannot yet act on.
    /// </summary>
    public static bool HasDeliverableEffect(ShopItemDefinition item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Hp != 0 || item.HpBoost != 0 || item.Max != 0 ||
            item.Experience != 0 || item.JudgmentBoost != 0;
    }

    /// <summary>
    /// The client's base gauge, <c>dword_55CB50[0]</c>. <c>hpboost</c> is a fraction
    /// (0.1/0.3/0.5 on the HP_RECOVERY consumables), and the starting gauge is the only
    /// HP lever the protocol exposes, so it is converted against this base. That is an
    /// APPROXIMATION - the items are named "recovery", which the wire cannot express.
    /// </summary>
    public const int BaseGauge = 100;

    /// <summary>
    /// Item ids in a 64-byte mount loadout (8 slots x {itemId:u32, expiration:u32},
    /// empty slots 0xFF-filled).
    /// </summary>
    public static IEnumerable<uint> EquippedItemIds(byte[] loadout)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        for (int offset = 0; offset + 4 <= loadout.Length; offset += 8)
        {
            uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                loadout.AsSpan(offset, 4));
            if (itemId != uint.MaxValue && itemId != 0)
            {
                yield return itemId;
            }
        }
    }

    public static EquipmentBonus For(
        IEnumerable<uint> itemIds,
        ShopCatalog? shop,
        EquipmentBonusScale scale)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (shop == null)
        {
            return None;
        }

        int hp = 0;
        long money = 0;
        long experience = 0;
        foreach (uint itemId in itemIds)
        {
            // The client resolves the LOW u16 against ItemStock (wItem + base), so the
            // server has to look items up exactly the same way.
            if (!shop.TryGet((ushort)itemId, out ShopItemDefinition? item) || item == null)
            {
                continue;
            }
            hp += item.Hp + (int)(item.HpBoost * BaseGauge);
            money += item.Max;
            experience += item.Experience;
        }

        return new EquipmentBonus(
            (ushort)Scale(hp, scale.HpPercent, ushort.MaxValue),
            (uint)Scale(money, scale.MaxPercent, uint.MaxValue),
            (uint)Scale(experience, scale.ExperiencePercent, uint.MaxValue));
    }

    private static long Scale(long value, int percent, long limit) =>
        percent <= 0 || value <= 0 ? 0 : Math.Clamp(value * percent / 100, 0, limit);
}
