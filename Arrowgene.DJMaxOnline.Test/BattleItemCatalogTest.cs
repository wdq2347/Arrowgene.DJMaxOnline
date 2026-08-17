using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

public sealed class BattleItemCatalogTest
{
    [Test]
    public void DropPoolMatchesShippedKoreanBattleIcons()
    {
        Assert.That(
            BattleItemCatalog.Drops.Select(item => item.PickupWireId),
            Is.EqualTo(Enumerable.Range(0, 11).Select(value => (short)value)));
        Assert.That(
            BattleItemCatalog.Drops.Select(item => item.PickupWireId),
            Is.Unique);
    }

    [TestCase((short)0, (short)0)]
    [TestCase((short)1, (short)1)]
    [TestCase((short)2, (short)2)]
    [TestCase((short)3, (short)3)]
    [TestCase((short)4, (short)4)]
    [TestCase((short)5, (short)5)]
    [TestCase((short)6, (short)6)]
    [TestCase((short)7, (short)7)]
    [TestCase((short)8, (short)8)]
    [TestCase((short)9, (short)9)]
    [TestCase((short)10, (short)10)]
    public void EveryDropMapsItsPickupIndexToTheClientEffect(
        short pickupId,
        short effectId)
    {
        bool supported = BattleItemCatalog.TryCreateUseEffect(
            pickupId, sourceSlot: 1, targetSlot: 2, level: 1,
            out BattleItemUseEffect? effect);

        Assert.Multiple(() =>
        {
            Assert.That(supported, Is.True);
            Assert.That(effect, Is.Not.Null);
            Assert.That(effect!.EffectId, Is.EqualTo(effectId));
            Assert.That(effect.SourceSlot, Is.EqualTo(1));
            Assert.That(effect.TargetSlot, Is.EqualTo(2));
        });
    }

    [TestCase(BattleItemPickupId.VariableSpeed, (byte)2, (byte)3, (short)3, (short)11)]
    [TestCase(BattleItemPickupId.LifeRecovery, (byte)0, (byte)0, (short)5, (short)0)]
    [TestCase(BattleItemPickupId.LifeRecovery, (byte)1, (byte)1, (short)10, (short)0)]
    [TestCase(BattleItemPickupId.LifeRecovery, (byte)2, (byte)2, (short)15, (short)0)]
    [TestCase(BattleItemPickupId.Bomb, (byte)2, (byte)2, (short)2, (short)0)]
    [TestCase(BattleItemPickupId.SpeedUp, (byte)1, (byte)1, (short)16, (short)0)]
    [TestCase(BattleItemPickupId.SpeedDown, (byte)1, (byte)1, (short)4, (short)0)]
    [TestCase(BattleItemPickupId.FadeIn, (byte)1, (byte)1, (short)1, (short)0)]
    [TestCase(BattleItemPickupId.FadeOut, (byte)1, (byte)1, (short)1, (short)0)]
    [TestCase(BattleItemPickupId.Blink, (byte)1, (byte)1, (short)1, (short)0)]
    [TestCase(BattleItemPickupId.Fog, (byte)1, (byte)1, (short)1, (short)0)]
    [TestCase(BattleItemPickupId.ChaosX, (byte)1, (byte)1, (short)1, (short)0)]
    [TestCase(BattleItemPickupId.HyperSpeed, (byte)1, (byte)1, (short)18, (short)0)]
    public void EveryShippedPickupBuildsItsVerifiedEffectParameters(
        BattleItemPickupId pickupId,
        byte level,
        byte expectedParameter0,
        short expectedParameter3,
        short expectedParameter4)
    {
        Assert.That(BattleItemCatalog.TryCreateUseEffect(
            (short)pickupId, 0, 1, level,
            out BattleItemUseEffect? effect), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(effect!.Parameter0, Is.EqualTo(expectedParameter0));
            Assert.That(effect.Parameter3, Is.EqualTo(expectedParameter3));
            Assert.That(effect.Parameter4, Is.EqualTo(expectedParameter4));
        });
    }

    [TestCase((byte)0, (short)5_000)]
    [TestCase((byte)1, (short)7_500)]
    [TestCase((byte)2, (short)10_000)]
    public void ItemLevelControlsTheEffectValueAndLifetime(
        byte level,
        short expectedDuration)
    {
        Assert.That(BattleItemCatalog.TryCreateUseEffect(
            (short)BattleItemPickupId.Blink, 0, 1, level,
            out BattleItemUseEffect? effect), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(effect!.Parameter0, Is.EqualTo(level));
            Assert.That(effect.Parameter1, Is.EqualTo(expectedDuration));
            Assert.That(effect.Parameter3, Is.EqualTo(level));
        });
    }

    [Test]
    public void UnknownProbeItemDoesNotInventAnEffect()
    {
        Assert.That(BattleItemCatalog.TryCreateUseEffect(
            1234, 0, 1, 0, out BattleItemUseEffect? effect), Is.False);
        Assert.That(effect, Is.Null);
    }
}
