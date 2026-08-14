using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// OnUseMountItemInf wire+4 is the equipped loadout's HP BONUS, not an item id.
///
/// sub_4368E0 stores it at net+894916 + 4*slot; sub_422250 reads it back through
/// sub_428A0C as <c>net[894916 + 4*slot] + dword_55CB50[..]</c>, where dword_55CB50 is the
/// base gauge table { 100, 100, 102, 105, ... }, and scales the result by the
/// OnStartParameterInf gauge. Writing an item id there (0x840A = 33802) hands that player a
/// gauge of ~33900 and makes them unfailable, which is exactly what happened.
/// </summary>
public class EquipmentHpTest
{
    private static ShopCatalog Shop() => ShopCatalog.Load(
        ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
        throw new DirectoryNotFoundException("Test shop DATA was not found."));

    /// <summary>Blade's set: 아리 avatar (hp 10) + 캔디걸기어 (hp 40) + 크리스탈노트 (hp 0).</summary>
    private static byte[] Loadout(params uint[] itemIds)
    {
        byte[] loadout = new byte[GameplayProtocol.MountSnapshotSize];
        loadout.AsSpan().Fill(0xFF);
        for (int slot = 0; slot < itemIds.Length; slot++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                loadout.AsSpan(slot * 8, 4), itemIds[slot]);
        }
        return loadout;
    }

    [Test]
    public void AnEquippedLoadoutIsWorthTheSumOfItsItemsHp()
    {
        ShopCatalog shop = Shop();
        byte[] blade = Loadout(0x1840A, 0x1A407, 0x1A804);

        Assert.Multiple(() =>
        {
            // The client resolves the LOW u16 against ItemStock (wItem + base), so the
            // server must too - 0x1840A is looked up as 0x840A.
            Assert.That(
                MountItemState.HpBonusFor(blade, shop), Is.EqualTo(50),
                "10 (아리) + 40 (캔디걸기어) + 0 (크리스탈노트)");
            // Nothing equipped is the base gauge and no more.
            Assert.That(MountItemState.HpBonusFor(Loadout(), shop), Is.EqualTo(0));
            // Never the item id: that is the bug this test exists for.
            Assert.That(MountItemState.HpBonusFor(blade, shop), Is.Not.EqualTo(0x840A));
        });
    }

    [Test]
    public void TheServerScalesTheBonusSoGearCanBeMadeCosmetic()
    {
        ShopCatalog shop = Shop();
        byte[] blade = Loadout(0x1840A, 0x1A407, 0x1A804);

        Assert.Multiple(() =>
        {
            Assert.That(MountItemState.HpBonusFor(blade, shop, 100), Is.EqualTo(50));
            Assert.That(MountItemState.HpBonusFor(blade, shop, 50), Is.EqualTo(25));
            // 0 = every player starts on the same gauge whatever they are wearing.
            Assert.That(MountItemState.HpBonusFor(blade, shop, 0), Is.EqualTo(0));
        });
    }

    [Test]
    public void WornGearAlsoPaysBonusMaxAndExperience()
    {
        ShopCatalog shop = Shop();
        EquipmentBonus bonus = EquipmentBonus.For(
            EquipmentBonus.EquippedItemIds(Loadout(0x1840A, 0x1A407, 0x1A804)),
            shop,
            EquipmentBonusScale.Full);

        Assert.Multiple(() =>
        {
            // exp: 아리 10 + 캔디걸기어 35 + 크리스탈노트 7.
            Assert.That(bonus.Experience, Is.EqualTo(52));
            // MAX is the currency, so `max` is money credited at the end of a song.
            Assert.That(bonus.Money, Is.GreaterThan(0));
            Assert.That(bonus.Hp, Is.EqualTo(50));
        });
    }

    [Test]
    public void ConsumableBoostersPayOutThroughTheSameThreeChannels()
    {
        ShopCatalog shop = Shop();
        // Section 5 boosters, resolved by the same low-u16 rule: HP LV3 (hp 200),
        // EXP LV3 (exp 500), MAX LV3 (max 500), HP_RECOVERY LV3 (hpboost 0.5).
        EquipmentBonus hp = EquipmentBonus.For([0xF403], shop, EquipmentBonusScale.Full);
        EquipmentBonus exp = EquipmentBonus.For([0xF483], shop, EquipmentBonusScale.Full);
        EquipmentBonus max = EquipmentBonus.For([0xF503], shop, EquipmentBonusScale.Full);
        EquipmentBonus recovery =
            EquipmentBonus.For([0xF583], shop, EquipmentBonusScale.Full);

        Assert.Multiple(() =>
        {
            Assert.That(hp.Hp, Is.EqualTo(200));
            Assert.That(exp.Experience, Is.EqualTo(500));
            Assert.That(max.Money, Is.EqualTo(500));
            // hpboost is a fraction, converted against the client's base gauge because the
            // starting gauge is the only HP lever the protocol has.
            Assert.That(recovery.Hp, Is.EqualTo(EquipmentBonus.BaseGauge / 2));
        });
    }

    [Test]
    public void EachBonusHasItsOwnDial()
    {
        ShopCatalog shop = Shop();
        uint[] items = [0x1840A, 0x1A407, 0x1A804];
        EquipmentBonus off = EquipmentBonus.For(items, shop, new(0, 0, 0));
        EquipmentBonus half = EquipmentBonus.For(items, shop, new(50, 50, 50));

        Assert.Multiple(() =>
        {
            Assert.That(off, Is.EqualTo(EquipmentBonus.None));
            Assert.That(half.Hp, Is.EqualTo(25));
            Assert.That(half.Experience, Is.EqualTo(26));
        });
    }

    [Test]
    public void TheBonusIsSentAtWirePlusFour()
    {
        ShopCatalog shop = Shop();
        byte[] blade = Loadout(0x1840A, 0x1A407, 0x1A804);
        MountItemState state = new(MountItemState.HpBonusFor(blade, shop), blade);
        byte[] wire = new PacketFactory().Write(
            OnUseMountItemInfPacket.Build(slot: 2, state));

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(70));
            Assert.That(wire[3], Is.EqualTo(2), "slot");
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(4)),
                Is.EqualTo(50), "HP bonus");
            Assert.That(wire.AsSpan(6, 64).ToArray(), Is.EqualTo(blade), "snapshot");
        });
    }
}
