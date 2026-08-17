using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>Serializes authoritative local-player operations and persists them.</summary>
public sealed class LocalPlayerStore
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(LocalPlayerStore));

    private readonly object _lock = new();
    private readonly string? _path;
    private readonly IPlayerRepository? _repository;
    private readonly ShopCatalog _shop;
    private readonly bool _itemsNeverExpire;

    /// <summary>
    /// The item catalog this profile's inventory is resolved against. Exposed because the
    /// lobby has to price an equipped loadout's HP without owning a catalog of its own.
    /// </summary>
    public ShopCatalog Shop => _shop;

    public LocalPlayerStore(
        LocalPlayerProfile profile,
        ShopCatalog shop,
        string? path = null,
        IPlayerRepository? repository = null,
        bool itemsNeverExpire = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(shop);
        profile.Validate();
        Profile = profile;
        _shop = shop;
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _repository = repository;
        _itemsNeverExpire = itemsNeverExpire;
        if (_path != null && _repository != null)
        {
            throw new ArgumentException(
                "Choose either legacy JSON persistence or a player repository, not both.");
        }
    }

    public LocalPlayerProfile Profile { get; }

    public T Read<T>(Func<LocalPlayerProfile, T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_lock)
        {
            return reader(Profile);
        }
    }

    public T Update<T>(Func<LocalPlayerProfile, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_lock)
        {
            T result = update(Profile);
            Profile.Validate();
            PersistProfile();
            return result;
        }
    }

    /// <summary>
    /// Applies a result-driven profile update and commits the complete score history row
    /// in the same SQLite transaction. Legacy/test stores still apply the profile update;
    /// they simply have no historical score repository.
    /// </summary>
    public T UpdateWithStageScore<T>(
        Func<LocalPlayerProfile, (T Result, StageScoreRecord Score)> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_lock)
        {
            (T result, StageScoreRecord score) = update(Profile);
            Profile.Validate();
            score.Validate();
            if (score.UserId != Profile.UserId)
            {
                throw new InvalidDataException(
                    $"Stage score user {score.UserId} does not match active profile " +
                    $"{Profile.UserId}.");
            }

            if (_repository != null)
            {
                _repository.SaveWithStageScore(Profile, score);
            }
            else if (_path != null)
            {
                LocalPlayerProfileFile.Save(_path, Profile);
            }
            return result;
        }
    }

    public IReadOnlyList<StageScoreRecord> StageScores(int limit = 100)
    {
        lock (_lock)
        {
            return _repository?.StageScores(Profile.UserId, limit) ?? [];
        }
    }

    private void PersistProfile()
    {
        if (_repository != null)
        {
            _repository.Save(Profile);
        }
        else if (_path != null)
        {
            LocalPlayerProfileFile.Save(_path, Profile);
        }
    }

    /// <summary>
    /// The shared top-50 ranking board for one course and channel. SQLite rows carry stable
    /// account keys; the requesting player's row is rewritten to their current wire/session
    /// id so the client can recognize it and draw "Your Rank". Legacy single-player stores
    /// still return their one local row.
    /// </summary>
    public IReadOnlyList<CourseRankEntry> CourseRanking(ushort courseId, byte keyMode) =>
        Read(profile =>
        {
            if (_repository != null)
            {
                return _repository
                    .CourseRanking(courseId, keyMode, OnCourseRankAckPacket.EntryCount)
                    .Select(entry => entry.UserId == profile.UserId
                        ? entry with { UserId = profile.WireUserId }
                        : entry)
                    .ToArray();
            }

            CourseRecord? record = profile.CourseRecords.FirstOrDefault(entry =>
                entry.CourseId == courseId && entry.KeyMode == keyMode);
            return record == null
                ? []
                : new CourseRankEntry[]
                {
                    // The client matches this row against the current login identity.
                    new(profile.WireUserId, profile.Nickname, record.Score, record.Combo)
                };
        });

    /// <summary>
    /// Records a course completion: keeps the better score and combo, and counts one more
    /// clear. Score and combo are what the ranking board draws; the clear count is not.
    /// </summary>
    /// <param name="cleared">
    /// Whether the run actually met the course's [Clear] objectives. The best score and
    /// combo record either way - the player did play it - but the CLEAR COUNT only moves
    /// on a real clear, or reaching the last stage of a course you kept failing would read
    /// as having beaten it.
    /// </param>
    /// <param name="keyMode">
    /// The channel the run happened on. SEOUL and TOKYO serve different charts, so their
    /// records and boards are kept apart.
    /// </param>
    public CourseRecord RecordCourseClear(
        ushort courseId, byte keyMode, uint score, uint combo, bool cleared = true) =>
        Update(profile =>
        {
            CourseRecord? record = profile.CourseRecords.FirstOrDefault(entry =>
                entry.CourseId == courseId && entry.KeyMode == keyMode);
            if (record == null)
            {
                record = new CourseRecord { CourseId = courseId, KeyMode = keyMode };
                profile.CourseRecords.Add(record);
            }

            record.Score = Math.Max(record.Score, score);
            record.Combo = Math.Max(record.Combo, combo);
            if (cleared && record.Clears < uint.MaxValue)
            {
                record.Clears++;
            }
            return record;
        });

    /// <summary>
    /// Adds a roster stand-in, choosing the lowest free user id above the real player's.
    /// Returns null when the nickname is already taken by the player or another entry.
    /// </summary>
    public RosterUser? AddRosterUser(string nickname, byte gender, uint level) =>
        Update(profile =>
        {
            if (string.Equals(nickname, profile.Nickname, StringComparison.Ordinal) ||
                profile.Roster.Any(user =>
                    string.Equals(user.Nickname, nickname, StringComparison.Ordinal)))
            {
                return null;
            }

            uint userId = profile.UserId + 1;
            while (profile.Roster.Any(user => user.UserId == userId))
            {
                userId++;
            }

            RosterUser created = new()
            {
                UserId = userId,
                AccountId = $"NPC{userId}",
                Nickname = nickname,
                Gender = gender,
                Level = level,
                // Female avatars start at 0x2000, male at 0xA400; picking by gender keeps
                // the drawn icon consistent with the gender byte at record+62.
                IconId = gender == 0 ? 0x2000u : LocalPlayerProfile.FirstUserIconId
            };
            profile.Roster.Add(created);
            return created;
        });

    /// <summary>Removes a roster stand-in and any contact or block entry pointing at it.</summary>
    public bool RemoveRosterUser(string nickname) =>
        Update(profile =>
        {
            RosterUser? user = profile.Roster.FirstOrDefault(entry =>
                string.Equals(entry.Nickname, nickname, StringComparison.Ordinal));
            if (user == null)
            {
                return false;
            }

            profile.Roster.Remove(user);
            // A contact whose record can no longer be resolved draws no row and leaves the
            // messenger able to crash on a stale selection, so clear the references too.
            profile.Messenger.Contacts.RemoveAll(c => c.UserId == user.UserId);
            profile.Messenger.BlockedUserIds.RemoveAll(id => id == user.UserId);
            return true;
        });

    /// <summary>Flips a roster stand-in's presence, or every entry when nickname is null.</summary>
    public int SetRosterPresence(string? nickname, bool online) =>
        Update(profile =>
        {
            RosterUser[] affected = nickname == null
                ? [.. profile.Roster]
                : [.. profile.Roster.Where(user =>
                    string.Equals(user.Nickname, nickname, StringComparison.Ordinal))];
            foreach (RosterUser user in affected)
            {
                user.Online = online;
            }
            return affected.Length;
        });

    /// <summary>
    /// Applies a messenger operation and reports the client's own result code. The result
    /// matters: OnMsgRegisterUserAck's u16 is what the client shows, and only 194 is
    /// treated as success - anything else leaves the entry out of its list.
    ///
    /// The local server hosts a single account, so the only resolvable target is that
    /// account itself; an unknown nickname or id has to come back UserNotFound rather than
    /// silently succeeding and creating a contact the client can never render.
    /// </summary>
    /// <param name="resolvedUserId">
    /// Target already resolved by the caller against everyone on the server. This store
    /// only knows ONE player's profile and roster, so without it a request naming another
    /// logged-in player can never be answered.
    /// </param>
    /// <param name="resolvedAccountId">
    /// The target's stable account id, stored alongside the contact so the entry survives
    /// the target's next login (user ids are randomised per session).
    /// </param>
    public MsgRegisterUserResult Messenger(
        MsgRegisterUserRequest request,
        uint? resolvedUserId = null,
        string? resolvedAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Update(profile =>
        {
            MessengerBook book = profile.Messenger;
            uint target = resolvedUserId is > 0
                ? resolvedUserId.Value
                : ResolveMessengerTarget(profile, request);

            switch (request.Operation)
            {
                case MessengerOperation.AddFriend:
                    if (target == 0)
                    {
                        return MsgRegisterUserResult.UserNotFound;
                    }
                    // Dedupe on the ACCOUNT, not the id. Session ids are randomised every
                    // login, so an id-only check let the same person be added over and
                    // over - a new row each time they reconnected.
                    if (book.Contacts.Any(c => c.UserId == target) ||
                        (!string.IsNullOrEmpty(resolvedAccountId) &&
                         book.Contacts.Any(c => string.Equals(
                             c.AccountId, resolvedAccountId, StringComparison.OrdinalIgnoreCase))))
                    {
                        return MsgRegisterUserResult.AlreadyFriend;
                    }
                    if (book.Contacts.Count >= OnMessengerInfoInfPacket.ContactCount)
                    {
                        return MsgRegisterUserResult.FriendListFull;
                    }
                    book.Contacts.Add(new MessengerContactEntry
                    {
                        UserId = target,
                        AccountId = resolvedAccountId ?? string.Empty,
                        GroupIndex = ClampGroup(request.GroupIndex)
                    });
                    return MsgRegisterUserResult.Success;

                case MessengerOperation.DeleteFriend:
                    return book.Contacts.RemoveAll(c => c.UserId == target) > 0
                        ? MsgRegisterUserResult.Success
                        : MsgRegisterUserResult.UserNotFound;

                case MessengerOperation.ChangeGroup:
                {
                    MessengerContactEntry? contact =
                        book.Contacts.FirstOrDefault(c => c.UserId == target);
                    if (contact == null)
                    {
                        return MsgRegisterUserResult.UserNotFound;
                    }
                    contact.GroupIndex = ClampGroup(request.GroupIndex);
                    return MsgRegisterUserResult.Success;
                }

                case MessengerOperation.BlockUser:
                    if (target == 0)
                    {
                        return MsgRegisterUserResult.UserNotFound;
                    }
                    if (book.BlockedUserIds.Contains(target))
                    {
                        return MsgRegisterUserResult.AlreadyBlocked;
                    }
                    if (book.BlockedUserIds.Count >= OnMessengerInfoInfPacket.BlockedUserCount)
                    {
                        return MsgRegisterUserResult.FriendListFull;
                    }
                    book.BlockedUserIds.Add(target);
                    return MsgRegisterUserResult.Success;

                case MessengerOperation.UnblockUser:
                    return book.BlockedUserIds.Remove(target)
                        ? MsgRegisterUserResult.Success
                        : MsgRegisterUserResult.UserNotFound;

                default:
                    return MsgRegisterUserResult.OperationFailed195;
            }
        });
    }

    /// <summary>
    /// Resolves the request's target. The client fills in the nickname when the player
    /// types one and the id when it already knows the user, so either may identify it.
    /// </summary>
    /// <summary>
    /// Maps a 친구추가 request to a user id. The client sends a nickname (or an id, when it
    /// already knows one), and only an account this server can produce a 67-byte record
    /// for may be returned - anything else becomes a contact that can never draw a row.
    /// That means the real player plus the roster stand-ins.
    /// </summary>
    private static uint ResolveMessengerTarget(
        LocalPlayerProfile profile,
        MsgRegisterUserRequest request)
    {
        if (request.UserId != 0)
        {
            if (request.UserId == profile.UserId)
            {
                return profile.UserId;
            }
            return profile.Roster.Any(user => user.UserId == request.UserId)
                ? request.UserId
                : 0;
        }

        if (string.IsNullOrEmpty(request.Nickname))
        {
            return 0;
        }
        if (string.Equals(request.Nickname, profile.Nickname, StringComparison.Ordinal))
        {
            return profile.UserId;
        }
        return profile.Roster
            .FirstOrDefault(user =>
                string.Equals(user.Nickname, request.Nickname, StringComparison.Ordinal))
            ?.UserId ?? 0;
    }

    private static ushort ClampGroup(ushort group) =>
        group < OnMessengerInfoInfPacket.GroupCount ? group : (ushort)0;

    /// <summary>
    /// Removes every timed item whose expiry has passed and reports what went, split by the
    /// notification that announces it. Expiration 0 means permanent; anything else is a
    /// unix timestamp written when the item was bought.
    /// </summary>
    public (IReadOnlyList<TimedInventoryItem> Box, IReadOnlyList<TimedInventoryItem> Mount)
        ExpireItems(DateTimeOffset now)
    {
        if (_itemsNeverExpire)
        {
            return ([], []);
        }

        return Update(profile =>
        {
            uint cutoff = now.ToUnixTimeSeconds() <= 0
                ? 0
                : (uint)Math.Min(now.ToUnixTimeSeconds(), uint.MaxValue);

            static bool HasExpired(TimedInventoryItem item, uint cutoff) =>
                item.Expiration != 0 && item.Expiration <= cutoff;

            List<TimedInventoryItem> box = profile.Inventory.ItemBoxItems().ToList();
            List<TimedInventoryItem> expiredBox =
                box.Where(item => HasExpired(item, cutoff)).ToList();
            List<TimedInventorySlot> mountedExpired = profile.Inventory.MountItems
                .Where(item => HasExpired(
                    new TimedInventoryItem(item.ItemId, item.Expiration), cutoff))
                .ToList();

            if (expiredBox.Count == 0 && mountedExpired.Count == 0)
            {
                return ((IReadOnlyList<TimedInventoryItem>)[],
                        (IReadOnlyList<TimedInventoryItem>)[]);
            }

            foreach (TimedInventoryItem item in expiredBox)
            {
                box.Remove(item);
                RemoveEquipped(profile.Inventory, item.ItemId, item.Expiration);
            }
            foreach (TimedInventorySlot slot in mountedExpired)
            {
                RemoveEquipped(profile.Inventory, slot.ItemId, slot.Expiration);
            }
            profile.Inventory.SetItemBox(box);

            IReadOnlyList<TimedInventoryItem> mount = mountedExpired
                .Select(slot => new TimedInventoryItem(slot.ItemId, slot.Expiration))
                .ToArray();
            return ((IReadOnlyList<TimedInventoryItem>)expiredBox, mount);
        });
    }

    /// <summary>
    /// Adds award items to the box, honouring the same stacking and capacity rules a
    /// purchase obeys. Returns what was actually granted: a full box or an id with no
    /// ItemStock row is skipped rather than silently corrupting the inventory.
    /// </summary>
    public IReadOnlyList<TimedInventoryItem> GrantItems(
        IReadOnlyList<(ushort CatalogId, ushort Count)> awards,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(awards);
        return Update(profile =>
        {
            List<TimedInventoryItem> box = profile.Inventory.ItemBoxItems().ToList();
            List<TimedInventoryItem> granted = [];

            foreach ((ushort catalogId, ushort count) in awards)
            {
                if (!_shop.TryGet(catalogId, out ShopItemDefinition? item) || item == null)
                {
                    continue;
                }

                uint value = item.ExpireDays == 0
                    ? 0
                    : checked((uint)now.AddDays(item.ExpireDays).ToUnixTimeSeconds());
                TimedInventoryItem addition = new(
                    ((uint)count << 16) | item.CatalogId, value);
                if (TryAddInventoryItem(box, item, addition))
                {
                    granted.Add(addition);
                }
            }

            if (granted.Count > 0)
            {
                profile.Inventory.SetItemBox(box);
            }
            return (IReadOnlyList<TimedInventoryItem>)granted;
        });
    }

    public PurchaseItemResponse Purchase(PurchaseItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Update(profile => PurchaseLocked(profile, request));
    }

    public ResaleItemResponse Resale(ResaleItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Update(profile =>
        {
            List<TimedInventoryItem> box = profile.Inventory.ItemBoxItems().ToList();
            int index = FindExact(box, request.ItemId, request.Value);
            if (index < 0 || !_shop.TryGet((ushort)request.ItemId, out ShopItemDefinition? item))
            {
                return ResaleFailure(profile);
            }

            if (!TryCredit(profile.Progress, item.Currency, item.ResalePrice))
            {
                return ResaleFailure(profile);
            }

            TimedInventoryItem removed = box[index];
            box.RemoveAt(index);
            profile.Inventory.SetItemBox(box);
            RemoveEquipped(profile.Inventory, removed.ItemId, removed.Expiration);
            return new ResaleItemResponse(
                ResaleItemResult.Success,
                profile.Progress.Money,
                profile.Inventory.ItemBoxItems());
        });
    }

    public DeleteItemResponse Delete(DeleteItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Update(profile =>
        {
            List<TimedInventoryItem> box = profile.Inventory.ItemBoxItems().ToList();
            int index = FindExact(box, request.ItemId, request.Value);
            if (index < 0)
            {
                return DeleteFailure(profile);
            }

            TimedInventoryItem removed = box[index];
            box.RemoveAt(index);
            profile.Inventory.SetItemBox(box);
            RemoveEquipped(profile.Inventory, removed.ItemId, removed.Expiration);
            return new DeleteItemResponse(
                DeleteItemResult.Success,
                profile.Inventory.ItemBoxItems());
        });
    }

    public GetPresentItemResponse GetPresent(GetPresentItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Update(profile =>
        {
            PresentInventorySlot? present = profile.Inventory.PresentItems.FirstOrDefault(item =>
                item.ItemId == request.ItemId &&
                item.SenderUserId == request.SenderUserId &&
                item.Expiration == request.Value);
            if (present == null ||
                !_shop.TryGet((ushort)present.ItemId, out ShopItemDefinition? item) ||
                (ushort)(present.ItemId >> 16) != item.Count)
            {
                return PresentFailure(profile);
            }

            List<TimedInventoryItem> box = profile.Inventory.ItemBoxItems().ToList();
            if (!TryAddInventoryItem(
                    box,
                    item,
                    new TimedInventoryItem(present.ItemId, present.Expiration)))
            {
                return PresentFailure(profile);
            }

            profile.Inventory.PresentItems.Remove(present);
            profile.Inventory.SetItemBox(box);
            return new GetPresentItemResponse(
                GetPresentItemResult.Success,
                PresentItems(profile.Inventory),
                profile.Inventory.ItemBoxItems());
        });
    }

    public MountItemResponse Mount(byte[] requestedLoadout)
    {
        ArgumentNullException.ThrowIfNull(requestedLoadout);
        if (requestedLoadout.Length != MountItemReqPacket.LoadoutSize)
        {
            throw new ArgumentException(
                $"Mount loadout must be {MountItemReqPacket.LoadoutSize} bytes.",
                nameof(requestedLoadout));
        }

        return Update(profile =>
        {
            IReadOnlyList<TimedInventoryItem> owned = profile.Inventory.ItemBoxItems();
            HashSet<(uint ItemId, uint Value)> equipped = [];
            List<TimedInventorySlot> requested = [];
            for (int slot = 0; slot < MountItemReqPacket.SlotCount; slot++)
            {
                int offset = slot * MountItemReqPacket.SlotSize;
                uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                    requestedLoadout.AsSpan(offset));
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(
                    requestedLoadout.AsSpan(offset + sizeof(uint)));
                if (itemId == uint.MaxValue && value == uint.MaxValue)
                {
                    continue;
                }
                if (itemId == uint.MaxValue || value == uint.MaxValue ||
                    !_shop.TryGet((ushort)itemId, out _) ||
                    FindExact(owned, itemId, value) < 0 ||
                    !equipped.Add((itemId, value)))
                {
                    // Equipping is validated against the item box, and a rejection used to
                    // be silent - the client keeps drawing the gear locally while the
                    // server stores nothing, so it never reaches anyone else.
                    Logger.Error(
                        $"{profile.AccountId} cannot equip item 0x{itemId:X} " +
                        $"(value 0x{value:X}) in slot {slot}: " +
                        $"{(_shop.TryGet((ushort)itemId, out _) ? "not owned" : "unknown item")}. " +
                        "Loadout unchanged.");
                    return new MountItemResponse(
                        MountItemResult.Failed,
                        profile.Inventory.MountLoadout());
                }
                requested.Add(new TimedInventorySlot(slot, itemId, value));
            }

            profile.Inventory.MountItems = requested;
            return new MountItemResponse(MountItemResult.Success, requestedLoadout.ToArray());
        });
    }

    public bool Use(UseItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Update(profile =>
        {
            List<TimedInventoryItem> box = profile.Inventory.ItemBoxItems().ToList();
            // The request carries only a slot index, so the slot has to be in range and
            // hold a countable item; anything else is a stale or malicious index.
            int index = request.Slot;
            if (index < 0 || index >= box.Count ||
                !_shop.TryGet((ushort)box[index].ItemId, out ShopItemDefinition? item) ||
                !item.Countable)
            {
                return false;
            }

            // Refuse rather than silently eat an item whose effect the server has no way
            // to deliver - today that is the SIGHT band, which carries only
            // `judgmentboost` and has no per-player wire field. The client shows its
            // "cannot use" failure, which is honest; consuming it for nothing is not.
            if (!EquipmentBonus.HasDeliverableEffect(item))
            {
                Logger.Info(
                    $"Refused to use '{item.Name}' (0x{item.CatalogId:X4}): its only stat " +
                    "is judgmentboost, which nothing on the wire can deliver.");
                return false;
            }

            // A consumable's entire effect is server-side, so using one has to be recorded
            // or it just disappears. Section 5 is the booster band: HP / EXP / MAX /
            // HP_RECOVERY, all spent by the next completed song.
            if (item.Section1 == ShopCatalog.ConsumableSection)
            {
                profile.Inventory.ActiveBoosters.Add(item.CatalogId);
            }

            ushort count = (ushort)(box[index].ItemId >> 16);
            if (count > 1)
            {
                uint decremented = ((uint)(count - 1) << 16) | item.CatalogId;
                box[index] = box[index] with { ItemId = decremented };
            }
            else
            {
                TimedInventoryItem removed = box[index];
                box.RemoveAt(index);
                RemoveEquipped(profile.Inventory, removed.ItemId, removed.Expiration);
            }
            profile.Inventory.SetItemBox(box);
            return true;
        });
    }

    private PurchaseItemResponse PurchaseLocked(
        LocalPlayerProfile profile,
        PurchaseItemRequest request)
    {
        List<TimedInventoryItem> requested = request.Items
            .Where(item => item != TimedInventoryItem.Empty)
            .ToList();
        if (requested.Count == 0 || request.Items.Any(item =>
                (item.ItemId == uint.MaxValue) != (item.Expiration == uint.MaxValue)))
        {
            return PurchaseFailure(profile);
        }

        List<TimedInventoryItem> box = profile.Inventory.ItemBoxItems().ToList();
        ulong cashCost = 0;
        ulong moneyCost = 0;
        ulong awardCost = 0;
        ulong moneyGrant = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (TimedInventoryItem requestItem in requested)
        {
            ushort catalogId = (ushort)requestItem.ItemId;
            if (!_shop.TryGet(catalogId, out ShopItemDefinition? item) ||
                (ushort)(requestItem.ItemId >> 16) != item.Count ||
                !_shop.IsNormalPurchase(item) ||
                !item.IsAllowedAtLevel(profile.Progress.Level))
            {
                return PurchaseFailure(profile);
            }

            switch (item.Currency)
            {
                case ShopCurrency.Cash:
                    cashCost += item.Price;
                    break;
                case ShopCurrency.Money:
                    moneyCost += item.Price;
                    break;
                case ShopCurrency.AwardPoints:
                    awardCost += item.Price;
                    break;
                default:
                    return PurchaseFailure(profile);
            }

            // Music-shop coin products exchange UBS cash for MAX and are not owned
            // inventory objects. wCount is the exact amount granted (30 or 100).
            if (item.Section1 == 1)
            {
                moneyGrant += item.Count;
                continue;
            }

            if (_shop.TryGetSetParts(catalogId, out IReadOnlyList<ushort>? parts))
            {
                foreach (ushort partId in parts)
                {
                    ShopItemDefinition part = _shop.Get(partId);
                    if (!TryAddInventoryItem(box, part, CreateInventoryItem(part, now)))
                    {
                        return PurchaseFailure(profile);
                    }
                }
            }
            else if (!TryAddInventoryItem(box, item, CreateInventoryItem(item, now)))
            {
                return PurchaseFailure(profile);
            }
        }

        if (cashCost > profile.Progress.Cash ||
            moneyCost > profile.Progress.Money ||
            awardCost > profile.Progress.AwardPoints)
        {
            return PurchaseFailure(profile);
        }

        ulong resultingMoney = (ulong)profile.Progress.Money - moneyCost + moneyGrant;
        if (resultingMoney > uint.MaxValue)
        {
            return PurchaseFailure(profile);
        }

        profile.Progress.Cash -= (uint)cashCost;
        profile.Progress.Money = (uint)resultingMoney;
        profile.Progress.AwardPoints -= (uint)awardCost;
        profile.Inventory.SetItemBox(box);
        return new PurchaseItemResponse(
            PurchaseItemResult.Success,
            profile.Progress.Money,
            profile.Inventory.ItemBoxItems());
    }

    private TimedInventoryItem CreateInventoryItem(
        ShopItemDefinition item,
        DateTimeOffset now)
    {
        uint value = _itemsNeverExpire || item.ExpireDays == 0
            ? 0
            : checked((uint)now.AddDays(item.ExpireDays).ToUnixTimeSeconds());
        return new TimedInventoryItem(item.PackedItemId, value);
    }

    private static bool TryAddInventoryItem(
        List<TimedInventoryItem> box,
        ShopItemDefinition item,
        TimedInventoryItem addition)
    {
        // A countable item of the SAME expiry merges into the existing stack. Anything
        // else - a non-countable duplicate, or the same item bought with a different
        // expiry - takes a slot of its own rather than failing.
        //
        // Refusing it soft-locked the client: the shop's duplicate guard is CARTMSG2,
        // an entirely client-side check against the cart, so by the time PurchaseItemReq
        // is sent the client considers the purchase valid. Its ack handler (sub_437C20)
        // only tests for success (result 173) and has no branch - and TextStock.ini has
        // no string - for "you already own this", because retail could never produce it.
        // So a rejection here returns an ack the client cannot act on and it hangs.
        int sameCatalog = box.FindIndex(existing =>
            (ushort)existing.ItemId == item.CatalogId);
        if (sameCatalog >= 0 &&
            item.Countable &&
            box[sameCatalog].Expiration == addition.Expiration)
        {
            uint existingCount = box[sameCatalog].ItemId >> 16;
            uint additionalCount = addition.ItemId >> 16;
            uint combined = existingCount + additionalCount;
            if (combined == 0 || combined > ushort.MaxValue)
            {
                return false;
            }
            box[sameCatalog] = box[sameCatalog] with
            {
                ItemId = (combined << 16) | item.CatalogId
            };
            return true;
        }

        if (box.Count >= OnPurchaseItemAckPacket.ItemBoxSlots)
        {
            return false;
        }
        box.Add(addition);
        return true;
    }

    private static bool TryCredit(
        LocalPlayerProgress progress,
        ShopCurrency currency,
        uint amount)
    {
        switch (currency)
        {
            case ShopCurrency.Cash when uint.MaxValue - progress.Cash >= amount:
                progress.Cash += amount;
                return true;
            case ShopCurrency.Money when uint.MaxValue - progress.Money >= amount:
                progress.Money += amount;
                return true;
            case ShopCurrency.AwardPoints when uint.MaxValue - progress.AwardPoints >= amount:
                progress.AwardPoints += amount;
                return true;
            default:
                return false;
        }
    }

    private static int FindExact(
        IReadOnlyList<TimedInventoryItem> items,
        uint itemId,
        uint value)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].ItemId == itemId && items[index].Expiration == value)
            {
                return index;
            }
        }
        return -1;
    }

    private static IReadOnlyList<PresentInventoryItem> PresentItems(
        LocalPlayerInventory inventory) =>
        inventory.PresentItems
            .OrderBy(item => item.Slot)
            .Select(item => new PresentInventoryItem(
                item.ItemId, item.SenderUserId, item.Expiration))
            .ToArray();

    private static void RemoveEquipped(
        LocalPlayerInventory inventory,
        uint itemId,
        uint value) =>
        inventory.MountItems.RemoveAll(item =>
            item.ItemId == itemId && item.Expiration == value);

    private static PurchaseItemResponse PurchaseFailure(LocalPlayerProfile profile) =>
        new(PurchaseItemResult.Failed, profile.Progress.Money, profile.Inventory.ItemBoxItems());

    private static ResaleItemResponse ResaleFailure(LocalPlayerProfile profile) =>
        new(ResaleItemResult.Failed, profile.Progress.Money, profile.Inventory.ItemBoxItems());

    private static DeleteItemResponse DeleteFailure(LocalPlayerProfile profile) =>
        new(DeleteItemResult.Failed, profile.Inventory.ItemBoxItems());

    private static GetPresentItemResponse PresentFailure(LocalPlayerProfile profile) =>
        new(
            GetPresentItemResult.Failed,
            PresentItems(profile.Inventory),
            profile.Inventory.ItemBoxItems());
}
