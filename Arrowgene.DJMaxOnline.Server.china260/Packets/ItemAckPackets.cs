using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

/// <summary>
/// One item-battle attack. The gameplay callback sub_40CE00 resolves the source
/// and target slots, applies <see cref="BattleItemUseEffect.EffectId"/> and its
/// parameters to the target, then consumes the source player's first queued item.
/// </summary>
public static class OnUseItemAckPacket
{
    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        Build(BattleItemUseEffect.Empty, control);

    public static Packet Build(
        BattleItemUseEffect effect,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnUseItemAck, control)
            .WriteByte(effect.SourceSlot)
            .WriteByte(effect.TargetSlot)
            .WriteInt16(effect.EffectId)
            .WriteByte(effect.Parameter0)
            .WriteInt16(effect.Parameter1)
            .WriteInt16(effect.Parameter2)
            .WriteInt16(effect.Parameter3)
            .WriteInt16(effect.Parameter4)
            .Build();
    }

    public static BattleItemUseEffect Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        BattleItemUseEffect effect = new(
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadInt16(),
            reader.ReadByte(),
            reader.ReadInt16(),
            reader.ReadInt16(),
            reader.ReadInt16(),
            reader.ReadInt16());
        reader.EnsureComplete();
        return effect;
    }
}

public sealed record BattleItemUseEffect(
    byte SourceSlot,
    byte TargetSlot,
    short EffectId,
    byte Parameter0,
    short Parameter1,
    short Parameter2,
    short Parameter3,
    short Parameter4)
{
    /// <summary>
    /// Structured zero response used only by the ordinary inventory-use path. Item
    /// battle always supplies real source/target/item fields.
    /// </summary>
    public static readonly BattleItemUseEffect Empty = new(0, 0, 0, 0, 0, 0, 0, 0);
}
