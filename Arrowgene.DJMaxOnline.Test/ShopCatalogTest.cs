using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

public class ShopCatalogTest
{
    private ShopCatalog _catalog = null!;

    [OneTimeSetUp]
    public void LoadCatalog()
    {
        string directory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        _catalog = ShopCatalog.Load(directory);
    }

    [Test]
    public void ConsumableListBasesMatchTheClientsOwnBaseTable()
    {
        // sub_4712A1 is authoritative: goods type 8 (regular shop, item) maps subList
        // 0/1/2 to 0x9800/0x9C00/0x9D00, and the >= 5 branch subtracts 0x8000 - giving
        // 0x1800/0x1C00/0x1D00. Only the first two have ItemStock rows.
        Assert.Multiple(() =>
        {
            foreach (ushort id in new ushort[] { 0x1801, 0x1C01 })
            {
                Assert.That(_catalog.TryGet(id, out ShopItemDefinition? item), Is.True,
                    $"0x{id:X4} must resolve; a wrong list base silently drops the item");
                Assert.That(item!.Section1, Is.EqualTo(3), $"0x{id:X4} is an item product");
            }
        });
    }

    [Test]
    public void ExpiryNotificationsUseEightSlotsAndTheClientsSentinel()
    {
        // sub_437680 loops exactly 8 records at wire+3 and skips a slot whose leading u16
        // is 0xFFFF, then memcpy's the 64-byte mount block from wire+67.
        byte[] mountWire = new PacketFactory().Write(
            OnExpiredMountItemInfPacket.Build(
                [new TimedInventoryItem(0x1801, 12345)],
                new byte[64]));

        // sub_437730 reads the same 8 records but its item box starts at raw +243.
        byte[] shopWire = new PacketFactory().Write(
            OnExpiredShopItemInfPacket.Build(
                [new TimedInventoryItem(0x2401, 999)],
                [new TimedInventoryItem(0x0401, 0)]));

        Assert.Multiple(() =>
        {
            Assert.That(mountWire, Has.Length.EqualTo(131));
            Assert.That(BitConverter.ToUInt32(mountWire, 3), Is.EqualTo(0x1801u));
            Assert.That(BitConverter.ToUInt32(mountWire, 7), Is.EqualTo(12345u));
            Assert.That(BitConverter.ToUInt32(mountWire, 11), Is.EqualTo(0xFFFFFFFFu),
                "unused slots must carry the sentinel the client skips on");

            Assert.That(shopWire, Has.Length.EqualTo(483));
            Assert.That(BitConverter.ToUInt32(shopWire, 3), Is.EqualTo(0x2401u));
            Assert.That(BitConverter.ToUInt32(shopWire, 243), Is.EqualTo(0x0401u),
                "the item box begins at raw +243, after 176 reserved bytes");
        });
    }

    [Test]
    public void ExpirySweepRemovesOnlyItemsWhosePeriodHasPassed()
    {
        string directory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        LocalPlayerProfile profile = new() { UserId = 1, Nickname = "Blade" };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        profile.Inventory.SetItemBox(
        [
            new TimedInventoryItem(0x0401, 0),                                  // permanent
            new TimedInventoryItem(0x0402, (uint)now.AddDays(-1).ToUnixTimeSeconds()),
            new TimedInventoryItem(0x0403, (uint)now.AddDays(30).ToUnixTimeSeconds())
        ]);
        LocalPlayerStore store = new(profile, ShopCatalog.Load(directory));

        var (box, mount) = store.ExpireItems(now);

        Assert.Multiple(() =>
        {
            Assert.That(box, Has.Count.EqualTo(1), "only the past-dated item expires");
            Assert.That(box[0].ItemId, Is.EqualTo(0x0402u));
            Assert.That(mount, Is.Empty);
            // Expiration 0 means permanent and must never be swept.
            Assert.That(profile.Inventory.ItemBox, Does.Contain(0x0401));
            Assert.That(profile.Inventory.ItemBox, Does.Contain(0x0403));
            Assert.That(profile.Inventory.ItemBox, Does.Not.Contain(0x0402));
            Assert.That(store.ExpireItems(now).Box, Is.Empty, "sweep is idempotent");
        });
    }

    [Test]
    public void InventoryPushLandsOnTheSameItemBoxTheShopAcksWrite()
    {
        // sub_4361E0 does memcpy(net+893035, packet+7, 240) - the identical 30x8 region
        // OnPurchaseItemAck fills. Empty slots must use the same 0xFFFFFFFF sentinel, or
        // the client renders phantom items in the free slots.
        byte[] wire = new PacketFactory().Write(
            OnUpdateUserInventoryShopItemInfPacket.BuildItemBox(
                userId: 9,
                [new TimedInventoryItem(100359, 0), new TimedInventoryItem(101378, 77)]));

        Assert.That(wire, Has.Length.EqualTo(247));
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(9u), "user id");
            Assert.That(BitConverter.ToUInt32(wire, 7), Is.EqualTo(100359u));
            Assert.That(BitConverter.ToUInt32(wire, 15), Is.EqualTo(101378u));
            Assert.That(BitConverter.ToUInt32(wire, 19), Is.EqualTo(77u));
            // Slot 2 onward is the empty sentinel; the box is 30 slots of 8 bytes.
            Assert.That(BitConverter.ToUInt32(wire, 23), Is.EqualTo(0xFFFFFFFFu));
            Assert.That(BitConverter.ToUInt32(wire, 243), Is.EqualTo(0xFFFFFFFFu));
        });
    }

    [Test]
    public void LoadsStockListingsAndSetsFromClientData()
    {
        Assert.Multiple(() =>
        {
            // Deliberately not exact counts. ItemStock.csv and the Goods lists are live
            // game data - items get added and shop pages get rebuilt - so a hard-coded
            // total just fails on the next content edit without revealing a parser bug.
            Assert.That(_catalog.ItemCount, Is.GreaterThan(100), "items parsed");
            Assert.That(_catalog.ListingCount, Is.GreaterThan(100), "listings parsed");
            Assert.That(_catalog.SetCount, Is.GreaterThan(0), "sets parsed");
        });

        // A dangling listing is a shop entry whose item has no ItemStock row - the client
        // drops those, so they are invisible in game. Their COUNT is content-dependent,
        // but the invariant is not: every id reported as dangling must genuinely be
        // absent, or the loader is hiding items that should have been sold.
        Assert.Multiple(() =>
        {
            foreach (uint dangling in _catalog.DanglingListings)
            {
                Assert.That(_catalog.TryGet((ushort)dangling, out _), Is.False,
                    $"0x{dangling:X} is reported dangling but does resolve");
            }
        });

        ShopItemDefinition avatar = _catalog.Get(0x0401);
        Assert.Multiple(() =>
        {
            // Structure, not pricing: the price and currency are content and get retuned.
            Assert.That(avatar.PackedItemId, Is.EqualTo(0x00010401));
            Assert.That(avatar.Price, Is.GreaterThan(0u));
            Assert.That(_catalog.IsNormalPurchase(avatar), Is.True);
            Assert.That(_catalog.TryGetSetParts(0x2C01, out IReadOnlyList<ushort>? parts),
                Is.True);
            Assert.That(parts, Is.EqualTo(new ushort[] { 0x2401, 0x2801 }));
        });
    }

    /// <summary>
    /// Buying charges the item's OWN currency by its OWN price, and selling it back
    /// refunds the resale value to that same currency - leaving the other untouched.
    ///
    /// The amounts are read from the catalog rather than written in here on purpose. The
    /// prices in ItemStock.csv are live game data that gets retuned; pinning "3000 cash"
    /// in a test only means the test fails the next time someone rebalances an avatar,
    /// which says nothing about whether the store logic is right.
    /// </summary>
    [Test]
    public void PurchaseAndResaleChargeTheCatalogCurrencyAndPreserveValues()
    {
        LocalPlayerProfile profile = CreateProfile();
        LocalPlayerStore store = new(profile, _catalog);

        ShopItemDefinition item = _catalog.Get(0x0401);
        uint cashBefore = profile.Progress.Cash;
        uint moneyBefore = profile.Progress.Money;
        bool paysCash = item.Currency == ShopCurrency.Cash;

        PurchaseItemResponse purchase = store.Purchase(Request(0x00010401));

        Assert.Multiple(() =>
        {
            Assert.That(purchase.Result, Is.EqualTo(PurchaseItemResult.Success));
            Assert.That(profile.Progress.Cash,
                Is.EqualTo(paysCash ? cashBefore - item.Price : cashBefore),
                "cash");
            Assert.That(profile.Progress.Money,
                Is.EqualTo(paysCash ? moneyBefore : moneyBefore - item.Price),
                "money");
            Assert.That(purchase.ItemBoxItems,
                Is.EqualTo(new[] { new TimedInventoryItem(0x00010401, 0) }));
        });

        ResaleItemResponse resale = store.Resale(new ResaleItemRequest(0x00010401, 0));
        Assert.Multiple(() =>
        {
            Assert.That(resale.Result, Is.EqualTo(ResaleItemResult.Success));
            // The refund goes back to the currency that was charged, and only that one.
            Assert.That(profile.Progress.Cash,
                Is.EqualTo(paysCash ? cashBefore - item.Price + item.ResalePrice : cashBefore),
                "cash after resale");
            Assert.That(profile.Progress.Money,
                Is.EqualTo(paysCash ? moneyBefore : moneyBefore - item.Price + item.ResalePrice),
                "money after resale");
            Assert.That(resale.ItemBoxItems, Is.Empty);
            Assert.That(PacketMeta.ResaleItemReq.Size, Is.EqualTo(11));
        });
    }

    [Test]
    public void PurchaseIsAtomicAndRejectsIdsMissingFromItemStock()
    {
        LocalPlayerProfile profile = CreateProfile();
        LocalPlayerStore store = new(profile, _catalog);
        PurchaseItemRequest request = new(new[]
        {
            new TimedInventoryItem(0x00010401, 0),
            new TimedInventoryItem(0x00010381, 0),
            TimedInventoryItem.Empty,
            TimedInventoryItem.Empty
        });

        PurchaseItemResponse response = store.Purchase(request);
        Assert.Multiple(() =>
        {
            Assert.That(response.Result, Is.EqualTo(PurchaseItemResult.Failed));
            Assert.That(profile.Progress.Cash, Is.EqualTo(10000));
            Assert.That(profile.Inventory.ItemBox, Is.Empty);
        });
    }

    [Test]
    public void SetPurchaseExpandsToGearAndNoteAtomically()
    {
        LocalPlayerProfile profile = CreateProfile();
        profile.Progress.Level = 10;
        LocalPlayerStore store = new(profile, _catalog);

        PurchaseItemResponse response = store.Purchase(Request(0x00012C01));
        Assert.Multiple(() =>
        {
            Assert.That(response.Result, Is.EqualTo(PurchaseItemResult.Success));
            Assert.That(profile.Progress.Cash, Is.EqualTo(2500));
            Assert.That(response.ItemBoxIds,
                Is.EqualTo(new uint[] { 0x00012401, 0x00012801 }));
            Assert.That(response.ItemBoxIds, Does.Not.Contain(0x00012C01));
        });
    }

    [Test]
    public void IgPurchaseUsesMoneyAndTimedPurchaseGetsServerExpiry()
    {
        LocalPlayerProfile profile = CreateProfile();
        LocalPlayerStore store = new(profile, _catalog);
        uint beforeMoney = profile.Progress.Money;

        PurchaseItemResponse ig = store.Purchase(Request(0x0001A401));
        Assert.Multiple(() =>
        {
            Assert.That(ig.Result, Is.EqualTo(PurchaseItemResult.Success));
            Assert.That(profile.Progress.Money, Is.LessThan(beforeMoney));
            Assert.That(profile.Progress.Cash, Is.EqualTo(10000));
        });

        // 0x1801 is the released seven-day HP extension product.
        PurchaseItemResponse timed = store.Purchase(Request(0x00011801));
        Assert.Multiple(() =>
        {
            Assert.That(timed.Result, Is.EqualTo(PurchaseItemResult.Success));
            Assert.That(timed.ItemBoxItems.Single(item => item.ItemId == 0x00011801).Expiration,
                Is.GreaterThan((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        });
    }

    [Test]
    public void PresentMountDeleteAndConsumableOperationsValidateOwnership()
    {
        LocalPlayerProfile profile = CreateProfile();
        profile.Inventory.SetPresentItem(0, 0x00010401, 77, 0);
        profile.Inventory.SetItemBox(new[] { new TimedInventoryItem(0x0002F401, 0) });
        LocalPlayerStore store = new(profile, _catalog);

        GetPresentItemResponse claimed = store.GetPresent(
            new GetPresentItemRequest(0x00010401, 77, 0));
        Assert.Multiple(() =>
        {
            Assert.That(claimed.Result, Is.EqualTo(GetPresentItemResult.Success));
            Assert.That(claimed.PresentItems, Is.Empty);
            Assert.That(claimed.ItemBoxIds(), Does.Contain(0x00010401));
        });

        byte[] loadout = EmptyLoadout();
        BinaryPrimitives.WriteUInt32LittleEndian(loadout, 0x00010401);
        BinaryPrimitives.WriteUInt32LittleEndian(loadout.AsSpan(4), 0);
        Assert.That(store.Mount(loadout).Result, Is.EqualTo(MountItemResult.Success));

        byte[] invalidLoadout = EmptyLoadout();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidLoadout, 0x00010402);
        BinaryPrimitives.WriteUInt32LittleEndian(invalidLoadout.AsSpan(4), 0);
        Assert.That(store.Mount(invalidLoadout).Result, Is.EqualTo(MountItemResult.Failed));

        // Slot 0 still holds the booster stack seeded above; the request names only a slot.
        Assert.That(store.Use(new UseItemRequest(Slot: 0)), Is.True);
        Assert.That(profile.Inventory.ItemBox, Does.Contain(0x0001F401));
        // An index past the end of the box must be rejected, not throw.
        Assert.That(store.Use(new UseItemRequest(Slot: 200)), Is.False);

        DeleteItemResponse deleted = store.Delete(new DeleteItemRequest(0x00010401, 0));
        Assert.Multiple(() =>
        {
            Assert.That(deleted.Result, Is.EqualTo(DeleteItemResult.Success));
            Assert.That(profile.Inventory.MountItems, Is.Empty);
        });
    }

    [Test]
    public void FullShopAcksHaveRecoveredWireSizes()
    {
        IReadOnlyList<TimedInventoryItem> box =
            [new TimedInventoryItem(0x00010401, 0x12345678)];
        PacketFactory factory = new();
        Assert.Multiple(() =>
        {
            Assert.That(factory.Write(OnPurchaseItemAckPacket.Build(new PurchaseItemResponse(
                PurchaseItemResult.Success, 1, box))), Has.Length.EqualTo(249));
            Assert.That(factory.Write(OnResaleItemAckPacket.Build(new ResaleItemResponse(
                ResaleItemResult.Success, 1, box))), Has.Length.EqualTo(249));
            Assert.That(factory.Write(OnDeleteItemAckPacket.Build(new DeleteItemResponse(
                DeleteItemResult.Success, box))), Has.Length.EqualTo(245));
            Assert.That(factory.Write(OnGetPresentItemAckPacket.Build(new GetPresentItemResponse(
                GetPresentItemResult.Success, [], box))), Has.Length.EqualTo(365));
            Assert.That(factory.Write(OnUseItemAckPacket.Build()), Has.Length.EqualTo(16));
        });
    }

    /// <summary>
    /// sub_472FA5 resolves the SENDER from present entry +8 and the numeric column from +4.
    /// Serialising the record's declaration order put them the other way round, so the gift
    /// row printed the expiration as the sender's name.
    /// </summary>
    [Test]
    public void PresentBoxPutsTheSenderIdAtOffsetEightOfItsEntry()
    {
        const uint ItemId = 0x00010401;
        const uint SenderUserId = 0xAABBCCDD;
        const uint Expiration = 0x11223344;
        byte[] wire = new PacketFactory().Write(OnGetPresentItemAckPacket.Build(
            new GetPresentItemResponse(
                GetPresentItemResult.Success,
                [new PresentInventoryItem(ItemId, SenderUserId, Expiration)],
                [])));

        // result u16@3, then the present box: slot 0 occupies wire+5..+16.
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt16(wire, 3), Is.EqualTo(168),
                "sub_437510 only applies the box when the result is 168");
            Assert.That(BitConverter.ToUInt32(wire, 5), Is.EqualTo(ItemId));
            Assert.That(BitConverter.ToUInt32(wire, 9),
                Is.EqualTo(Expiration), "entry+4 is the value column");
            Assert.That(BitConverter.ToUInt32(wire, 13),
                Is.EqualTo(SenderUserId),
                "entry+8 is what sub_433B80 looks the nickname up by");
        });
    }

    private static LocalPlayerProfile CreateProfile() => new()
    {
        Progress = new LocalPlayerProgress
        {
            Level = 99,
            Money = 200000,
            Cash = 10000,
            AwardPoints = 10000
        }
    };

    private static PurchaseItemRequest Request(uint itemId) => new(new[]
    {
        new TimedInventoryItem(itemId, 0),
        TimedInventoryItem.Empty,
        TimedInventoryItem.Empty,
        TimedInventoryItem.Empty
    });

    private static byte[] EmptyLoadout()
    {
        byte[] loadout = new byte[MountItemReqPacket.LoadoutSize];
        loadout.AsSpan().Fill(0xFF);
        return loadout;
    }
}

internal static class ShopTestExtensions
{
    public static IReadOnlyList<uint> ItemBoxIds(this GetPresentItemResponse response) =>
        response.ItemBoxItems.Select(item => item.ItemId).ToArray();
}
