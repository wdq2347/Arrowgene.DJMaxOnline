using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// Effect ids consumed by the Korean client's sub_425260 effect switch. The
/// implemented retail item definitions occupy the same compact indices as the
/// pickup table. Numeric suffixes such as I35/I36 are asset names, not wire ids.
/// </summary>
public enum BattleItemId : short
{
    VariableSpeed = 0,
    LifeRecovery = 1,
    Bomb = 2,
    SpeedUp = 3,
    SpeedDown = 4,
    FadeIn = 5,
    FadeOut = 6,
    Blink = 7,
    Fog = 8,
    ChaosX = 9,
    HyperSpeed = 10
}

/// <summary>
/// Indices into the client's compact battle-item presentation table. The pickup
/// callback stores this value and the renderer uses it directly as an array index.
/// The Korean executable's two 0x60-byte presentation tables contain exactly these
/// entries in this order (0x55AEC0 and 0x55BC40). This is deliberately separate from
/// the larger 0..35 gameplay effect switch above.
/// </summary>
public enum BattleItemPickupId : short
{
    VariableSpeed = 0, // VAR.SPEED / I36_Icon_VR_Speed.png
    LifeRecovery = 1,  // BLU.RCAPSULE / ID04_RCapsule_Blue.png
    Bomb = 2,          // NOM.BOMB / I01_Icon_N_Bomb.png
    SpeedUp = 3,       // SUP.SPEED / I04_Icon_SU_Speed.png
    SpeedDown = 4,     // HLF.SPEED / I06_Icon_HF_Speed.png
    FadeIn = 5,        // F/I.SCOPE / I10_Icon_Fade_In.png
    FadeOut = 6,       // F/O.SCOPE / I11_Icon_Fade_Out.png
    Blink = 7,         // BLK.SCOPE / I35_Icon_Blink.png
    Fog = 8,           // FOG.SCOPE / I12_Icon_Fog.png
    ChaosX = 9,        // X.NOTE / I07_Icon_X_Dim.png
    HyperSpeed = 10    // HYP.SPEED / I04_Icon_SU_Speed.png
}

public sealed record BattleItemDefinition(
    BattleItemPickupId PickupId,
    BattleItemId EffectId,
    string Name)
{
    public short PickupWireId => (short)PickupId;
}

/// <summary>
/// Server-side drop pool and effect construction for item battle. Pickup ids come from
/// the client's 11-entry presentation table; effect ids and fixed parameters come from
/// sub_425260 and the executable's 0x70-byte effector descriptor table at 0x559640.
/// </summary>
public static class BattleItemCatalog
{
    // OnUseItemAck+8 is a signed 16-bit millisecond lifetime. Retail captures do
    // not contain an item-battle use, so keep the local tuning explicit and well
    // below the 32.767-second wire limit. Higher item levels earn longer effects.
    private static readonly short[] EffectDurationMillisecondsByLevel =
    [
        5_000,
        7_500,
        10_000
    ];

    private static readonly BattleItemDefinition[] DropPool =
    [
        new(BattleItemPickupId.VariableSpeed, BattleItemId.VariableSpeed, "Variable Speed"),
        new(BattleItemPickupId.LifeRecovery, BattleItemId.LifeRecovery, "Blue Life Capsule"),
        new(BattleItemPickupId.Bomb, BattleItemId.Bomb, "Bomb"),
        new(BattleItemPickupId.SpeedUp, BattleItemId.SpeedUp, "Speed Up"),
        new(BattleItemPickupId.SpeedDown, BattleItemId.SpeedDown, "Speed Down"),
        new(BattleItemPickupId.FadeIn, BattleItemId.FadeIn, "Fader In"),
        new(BattleItemPickupId.FadeOut, BattleItemId.FadeOut, "Fader Out"),
        new(BattleItemPickupId.Blink, BattleItemId.Blink, "Blink"),
        new(BattleItemPickupId.Fog, BattleItemId.Fog, "Fog"),
        new(BattleItemPickupId.ChaosX, BattleItemId.ChaosX, "Chaos X"),
        new(BattleItemPickupId.HyperSpeed, BattleItemId.HyperSpeed, "Hyper Speed")
    ];

    public static IReadOnlyList<BattleItemDefinition> Drops => DropPool;

    public static BattleItemDefinition RandomDrop() =>
        DropPool[Random.Shared.Next(DropPool.Length)];

    public static bool TryGet(short pickupId, out BattleItemDefinition? definition)
    {
        definition = DropPool.FirstOrDefault(item => item.PickupWireId == pickupId);
        return definition != null;
    }

    public static bool TryCreateUseEffect(
        short pickupId,
        byte sourceSlot,
        byte targetSlot,
        byte level,
        out BattleItemUseEffect? effect)
    {
        if (!TryGet(pickupId, out BattleItemDefinition? definition))
        {
            effect = null;
            return false;
        }

        byte safeLevel = Math.Min(level, (byte)2);
        short duration = EffectDurationMillisecondsByLevel[safeLevel];
        byte parameter0 = safeLevel;
        short parameter2 = 0;
        short parameter3 = safeLevel;
        short parameter4 = 0;

        switch (definition!.PickupId)
        {
            case BattleItemPickupId.VariableSpeed:
                // The SPEED BAT descriptor supplies {3, 11, 3, 0, 1000}: speed
                // range 3..11 and step/state value 3. Battle level controls only
                // how long the attack remains active.
                parameter0 = 3;
                parameter3 = 3;
                parameter4 = 11;
                break;
            case BattleItemPickupId.LifeRecovery:
                // sub_425260 case 1 passes Parameter3 straight to sub_424A20 as
                // a float life increment. The client's other instant-life effect
                // (case 25) establishes five life points as the base quantum.
                parameter3 = (short)(5 * (safeLevel + 1));
                break;
            case BattleItemPickupId.SpeedUp:
                // effector_su_speed.png descriptor selects speed index 16.
                parameter3 = 16;
                break;
            case BattleItemPickupId.SpeedDown:
                // effector_hf_speed.png descriptor selects speed index 4.
                parameter3 = 4;
                break;
            case BattleItemPickupId.HyperSpeed:
                // sub_425260's speed index is clamped to 0..18; HYP.SPEED is the
                // terminal attack above the ordinary SPEED UP descriptor (16).
                parameter3 = 18;
                break;
        }

        // sub_40CE00 maps BC+12 (Parameter3) to sub_425260's primary effect
        // value. Fixed-speed and life items need their actual gameplay value there;
        // the remaining visual attacks use retail item level 0..2. BC+7 is retained
        // as the secondary level/state field except for Variable Speed's fixed step.
        // Parameter1 maps from BC+8 to the duration checked by sub_425EE0.
        effect = new BattleItemUseEffect(
            sourceSlot,
            targetSlot,
            (short)definition.EffectId,
            parameter0,
            duration,
            parameter2,
            parameter3,
            parameter4);
        return true;
    }
}
