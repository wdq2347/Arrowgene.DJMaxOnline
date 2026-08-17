using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

public enum UbsAccountAuthenticationResult : byte
{
    Success = 0
}

public sealed record UbsAccountAuthenticationResponse(
    UbsAccountAuthenticationResult Result,
    uint Cash);

public static class UbsAccountAuthenticationReqPacket
{
    public const int ReservedSize = 8;

    public static void Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        reader.Skip(ReservedSize);
        reader.EnsureComplete();
    }
}

public static class OnUbsAccountAuthenResAckPacket
{
    // The client reads cash first (u32 at wire offset 3, i.e. the clear header) then
    // the result byte (at wire offset 7), followed by 8 bytes it ignores. Total wire
    // size is 16 — see PacketMeta.OnUbsAccountAuthenResAck.
    private const int TrailingPadding = 8;

    public static Packet Build(
        UbsAccountAuthenticationResult result,
        uint cash,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnUbsAccountAuthenResAck, control)
            .WriteUInt32(cash)
            .WriteByte((byte)result)
            .WritePadding(TrailingPadding, ProtocolPadding.Unused)
            .Build();

    public static UbsAccountAuthenticationResponse Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint cash = reader.ReadUInt32();
        UbsAccountAuthenticationResult result = (UbsAccountAuthenticationResult)reader.ReadByte();
        reader.Skip(TrailingPadding);
        reader.EnsureComplete();
        return new UbsAccountAuthenticationResponse(result, cash);
    }
}

/// <summary>
/// Final ack of the shop-entry handshake (client id 304). The client registers it as a
/// 5-byte fixed, unencrypted packet (id + a 3-byte clear header, no data) and forwards
/// it to the shop dialog to finish the "Entering the shop" wait. Its handler
/// (sub_432440) reads no payload fields, so the two header bytes carry no meaning.
/// </summary>
public static class OnUbsAwardAuthenAckPacket
{
    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        new(PacketMeta.OnUbsAwardAuthenAck, [])
        {
            Header = [control, ProtocolPadding.Unused, ProtocolPadding.Unused],
        };
}

/// <summary>
/// UBS award-point list sent as the second half of the shop-entry handshake. The
/// shop's "connecting" dialog waits for this after OnUbsAccountAuthenResAck before it
/// opens, so omitting it leaves the shop stuck connecting on first entry. We send an
/// empty list (wire size 7 → the client derives 0 award entries).
/// </summary>
public static class OnUbsAwardInfoInfPacket
{
    // sub_4321D0 reads status@7 and optional text@8. Status 0 is empty/success.
    private const int EmptyWireSize = 8;

    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Dynamic(PacketMeta.OnUbsAwardInfoInf, EmptyWireSize, control)
            .WriteByte(0)
            .Build();
}

public sealed record PurchaseItemRequest(IReadOnlyList<TimedInventoryItem> Items);

public sealed record ResaleItemRequest(
    uint ItemId,
    uint Value);

// Korean shop-ack success codes, taken from the client handlers that gate on the
// result WORD@3: purchase sub_437C20 == 173, resale sub_437D00 == 180.
public enum PurchaseItemResult : ushort
{
    Failed = 0,
    Success = 173
}

public enum ResaleItemResult : ushort
{
    Failed = 0,
    Success = 180
}

// The Korean shop acks carry the whole 30-slot item box (net+893035 = login
// block+320), not a China-style ShopItems list, because the client memcpy's the
// 240-byte block straight into the item box.
public sealed record PurchaseItemResponse(
    PurchaseItemResult Result,
    uint Money,
    IReadOnlyList<TimedInventoryItem> ItemBoxItems)
{
    public IReadOnlyList<uint> ItemBoxIds =>
        ItemBoxItems.Select(item => item.ItemId).ToArray();
}

public sealed record ResaleItemResponse(
    ResaleItemResult Result,
    uint Money,
    IReadOnlyList<TimedInventoryItem> ItemBoxItems)
{
    public IReadOnlyList<uint> ItemBoxIds =>
        ItemBoxItems.Select(item => item.ItemId).ToArray();
}

// Item-box "throw away" (소지품 삭제). Client handler sub_437600 gates on result@3==171.
public enum DeleteItemResult : ushort
{
    Failed = 0,
    Success = 171
}

public sealed record DeleteItemRequest(uint ItemId, uint Value);

public sealed record DeleteItemResponse(
    DeleteItemResult Result,
    IReadOnlyList<TimedInventoryItem> ItemBoxItems)
{
    public IReadOnlyList<uint> ItemBoxIds =>
        ItemBoxItems.Select(item => item.ItemId).ToArray();
}

public enum GetPresentItemResult : ushort
{
    Failed = 0,
    Success = 168
}

public sealed record GetPresentItemRequest(
    uint ItemId,
    uint SenderUserId,
    uint Value);

public sealed record GetPresentItemResponse(
    GetPresentItemResult Result,
    IReadOnlyList<PresentInventoryItem> PresentItems,
    IReadOnlyList<TimedInventoryItem> ItemBoxItems);

// DeleteItemReq (0xDB, 11 bytes): {itemId u32@3, value u32@7}. The itemId sits in the
// clear header region so it is readable even before decryption.
public static class DeleteItemReqPacket
{
    public static DeleteItemRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        DeleteItemRequest request = new(reader.ReadUInt32(), reader.ReadUInt32());
        reader.EnsureComplete();
        return request;
    }
}

// OnDeleteItemAck (0xDC, 245 bytes): result u16@3 (171 = success), then the whole
// 30-slot item box @5 (no money field). sub_437600 memcpy's the 240-byte box into
// net+893035 only when the result is 171.
public static class OnDeleteItemAckPacket
{
    public static Packet Build(
        DeleteItemResponse response,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(response);
        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Fixed(PacketMeta.OnDeleteItemAck, control)
            .WriteUInt16((ushort)response.Result);
        OnPurchaseItemAckPacket.WriteItemBox(builder, response.ItemBoxItems);
        return builder.Build();
    }
}

public static class GetPresentItemReqPacket
{
    public static GetPresentItemRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        GetPresentItemRequest request = new(
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        reader.EnsureComplete();
        return request;
    }
}

public static class PurchaseItemReqPacket
{
    public const int ItemCount = 4;

    public static PurchaseItemRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        TimedInventoryItem[] items = new TimedInventoryItem[ItemCount];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new TimedInventoryItem(reader.ReadUInt32(), reader.ReadUInt32());
        }
        reader.EnsureComplete();
        return new PurchaseItemRequest(items);
    }
}

public static class ResaleItemReqPacket
{
    public static ResaleItemRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        ResaleItemRequest request = new(
            reader.ReadUInt32(),
            reader.ReadUInt32());
        reader.EnsureComplete();
        return request;
    }
}

public static class OnPurchaseItemAckPacket
{
    // The item box is a 30-slot array of {itemId:u32, value:u32}; empty slots use the
    // 0xFFFFFFFF sentinel in both words, matching the OnLogInAck block+320 layout.
    public const int ItemBoxSlots = 30;

    // Korean OnPurchaseItemAck (0xDE, 249 bytes). Client handler sub_437C20 gates on the
    // result WORD@3 == 173, stores money@5 into net+794169, then memcpy's 240 bytes @9
    // into the item box (net+893035). Layout: result u16@3, money u32@5, 30×8 item box@9.
    public static Packet Build(
        PurchaseItemResponse response,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(response);
        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Fixed(PacketMeta.OnPurchaseItemAck, control)
            .WriteUInt16((ushort)response.Result)
            .WriteUInt32(response.Money);
        WriteItemBox(builder, response.ItemBoxItems);
        return builder.Build();
    }

    // Writes the 30-slot item box (240 bytes). Filled slots are {itemId, 0} like the
    // login block; empty slots are the {0xFFFFFFFF, 0xFFFFFFFF} sentinel.
    internal static void WriteItemBox(
        DjMaxPacketBuilder builder,
        IReadOnlyList<TimedInventoryItem> itemBoxItems)
    {
        ArgumentNullException.ThrowIfNull(itemBoxItems);
        if (itemBoxItems.Count > ItemBoxSlots)
        {
            throw new ArgumentException(
                $"Item box cannot exceed {ItemBoxSlots} entries.", nameof(itemBoxItems));
        }
        for (int i = 0; i < ItemBoxSlots; i++)
        {
            if (i < itemBoxItems.Count)
            {
                builder
                    .WriteUInt32(itemBoxItems[i].ItemId)
                    .WriteUInt32(itemBoxItems[i].Expiration);
            }
            else
            {
                builder.WriteUInt32(0xFFFFFFFF).WriteUInt32(0xFFFFFFFF);
            }
        }
    }
}

public static class OnResaleItemAckPacket
{
    // Korean OnResaleItemAck (0xE0, 249 bytes). Identical shape to the purchase ack but
    // the client handler sub_437D00 gates on result WORD@3 == 180.
    public static Packet Build(
        ResaleItemResponse response,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(response);
        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Fixed(PacketMeta.OnResaleItemAck, control)
            .WriteUInt16((ushort)response.Result)
            .WriteUInt32(response.Money);
        OnPurchaseItemAckPacket.WriteItemBox(builder, response.ItemBoxItems);
        return builder.Build();
    }
}

public static class OnGetPresentItemAckPacket
{
    public static Packet Build(
        GetPresentItemResponse response,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.PresentItems.Count > OnInventoryInfoInfPacket.PresentItemCount)
        {
            throw new ArgumentException(
                $"Present list cannot exceed {OnInventoryInfoInfPacket.PresentItemCount} entries.",
                nameof(response));
        }

        // 365 bytes: result u16@3, the 120-byte present box @5 (-> net+893275) and the
        // 240-byte 30-slot item box @125 (-> net+893035). sub_437510 only applies either
        // when the result is 168, and it clears the request's pending flag (net+895176)
        // unconditionally, so the ack has to go out even on a refusal.
        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Fixed(PacketMeta.OnGetPresentItemAck, control)
            .WriteUInt16((ushort)response.Result);
        OnInventoryInfoInfPacket.WritePresentBox(builder, response.PresentItems);
        OnPurchaseItemAckPacket.WriteItemBox(builder, response.ItemBoxItems);
        return builder.Build();
    }
}
