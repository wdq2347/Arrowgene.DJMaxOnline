using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Roster stand-ins exist for one reason: the client draws a messenger or waiter row only
/// when it can resolve that id's 67-byte user record (sub_48AB44 and sub_48BE29 both skip
/// an id sub_433B80 cannot resolve). On a single-account server nothing but the player
/// could ever be resolved, so every "somebody else" feature was untestable.
/// </summary>
public class RosterTest
{
    [Test]
    public void WireIdentityIsTheSessionIdNotTheAccountKey()
    {
        // Every freshly created profile's account key is 1, so two players answered to the
        // same id and every cross-player feature - messenger routing, presence, the
        // profile view - collapsed onto whoever asked. The account key stays put because
        // every SQLite row is built on it; only the wire identity is randomised.
        LocalPlayerProfile profile = new() { UserId = 1, AccountId = "ACC" };

        Assert.Multiple(() =>
        {
            // Until a session is assigned the account key is used, so offline/CLI paths
            // keep working.
            Assert.That(profile.SessionUserId, Is.EqualTo(0));
            Assert.That(profile.WireUserId, Is.EqualTo(1));

            profile.SessionUserId = 0x4321;
            Assert.That(profile.WireUserId, Is.EqualTo(0x4321));
            Assert.That(profile.UserId, Is.EqualTo(1), "the account key must not move");
            // The waiter erase field is a u16, so an id above that could never remove its
            // own row from the user list.
            Assert.That(profile.WireUserId, Is.LessThanOrEqualTo(ushort.MaxValue));
        });
    }

    [Test]
    public void WaiterRowIsKeyedByTheIdTheEraseWillName()
    {
        // sub_4333F0 keys the waiter map on *(WORD *)(record + 71) and sub_4334A0 erases
        // by that same 16-bit value. Sending 0 there filed EVERY player under key 0, so
        // rows overwrote each other and no erase ever matched - a player who left stayed
        // in the list forever.
        LocalPlayerProfile profile = new()
        {
            UserId = 1,
            AccountId = "ACC",
            Nickname = "PLAYER",
            SessionUserId = 0x2345
        };

        LobbyWaiterInfo waiter = LobbyWaiterInfo.CreateLocal(profile);
        byte[] wire = new PacketFactory().Write(OnWaiterInfoUpdateInfPacket.Build(waiter));

        // The SAME account logging in again gets a new session id...
        profile.SessionUserId = 0x6789;
        LobbyWaiterInfo relogin = LobbyWaiterInfo.CreateLocal(profile);

        Assert.Multiple(() =>
        {
            // record+0 is the ROW IDENTITY: sub_44342B updates in place when
            // sub_49EF30(*record) already exists and otherwise CREATES A NEW ROW.
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(waiter.UserId));
            Assert.That(waiter.UserId, Is.Not.Zero);
            // The erase only carries 16 bits, so the identity has to fit one.
            Assert.That(waiter.UserId, Is.LessThanOrEqualTo(ushort.MaxValue));
            // record starts at wire 3, so record+71 is wire 74.
            Assert.That(BitConverter.ToUInt16(wire, 3 + 71), Is.EqualTo(waiter.MapKey));

            // A RELOGIN must reuse the same identity, or it builds another row that no
            // erase can ever name - which is exactly how one player stacked up three rows.
            Assert.That(relogin.UserId, Is.EqualTo(waiter.UserId));
            Assert.That(relogin.MapKey, Is.EqualTo(waiter.MapKey));

            // ...and the erase names that same value in BOTH of its fields. The visible
            // row is removed by the u32 at wire+5 (sub_441657 -> sub_4A03F0), NOT by the
            // u16 at wire+3 that drops the underlying record - leaving wire+5 as padding
            // erased row id 0, so the player never left the list.
            byte[] erase = new PacketFactory().Write(
                OnWaiterInfoEraseInfPacket.Build(profile.WaiterKey));
            Assert.That(erase, Has.Length.EqualTo(9));
            Assert.That(BitConverter.ToUInt16(erase, 3), Is.EqualTo((ushort)waiter.UserId),
                "wire+3 drops the record");
            Assert.That(BitConverter.ToUInt32(erase, 5), Is.EqualTo(waiter.UserId),
                "wire+5 removes the visible row");
        });
    }

    [Test]
    public void ProfilePanelReadsTheNameAtBlockOffset29()
    {
        // sub_463D0C (CCommonUI::ShowUserInfo) reads the name at block+29, gender at +54,
        // icon at +57, level at +65, exp at +69 and accountClass at +129 - the SAME
        // 135-byte block OnLogInAck carries. The old layout put a userId at +0 and the
        // name at +4, so the panel drew whatever sat at +29 (the ACCOUNT ID) as the name.
        LocalPlayerProfile profile = new()
        {
            UserId = 7,
            AccountId = "SECRETLOGIN",
            Nickname = "Rival",
            Gender = 1,
            AccountClass = 0x10000
        };
        profile.Progress.Level = 27;

        byte[] wire = new PacketFactory().Write(
            OnUserInfoAckPacket.Build(7, profile.Nickname, profile));

        // Block starts at wire 3, so block+29 is wire 32.
        string name = System.Text.Encoding.ASCII.GetString(wire, 3 + 29, 5);
        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("Rival"));
            Assert.That(wire[3 + 54], Is.EqualTo(1), "gender at +54");
            Assert.That(BitConverter.ToUInt32(wire, 3 + 65), Is.EqualTo(27u), "level at +65");
            Assert.That(BitConverter.ToUInt32(wire, 3 + 129), Is.EqualTo(0x10000u),
                "accountClass at +129");
            // The login name must not appear anywhere on the wire.
            Assert.That(System.Text.Encoding.ASCII.GetString(wire),
                Does.Not.Contain("SECRETLOGIN"));
        });
    }

    [Test]
    public void ContactsAreSavedAgainstTheAccountNotTheSessionId()
    {
        // A friends list stored by user id alone would point at nobody next login - or at
        // whoever inherited that number - now that ids are randomised per session.
        MessengerContactEntry contact = new()
        {
            UserId = 0x4321,
            AccountId = "FRIEND",
            GroupIndex = 0
        };

        Assert.Multiple(() =>
        {
            Assert.That(contact.AccountId, Is.EqualTo("FRIEND"));
            // The id is only a cache; re-pointing it must not lose the identity.
            contact.UserId = 0x5678;
            Assert.That(contact.AccountId, Is.EqualTo("FRIEND"));
        });
    }

    private static LocalPlayerStore NewStore() => new(
        new LocalPlayerProfile { Nickname = "Blade", UserId = 1 },
        ShopCatalog.Load(
            ShopCatalog.FindDataDirectory(
                Directory.GetCurrentDirectory(),
                TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.")));

    [Test]
    public void AddedUserGetsAnIdThatCannotCollideWithThePlayer()
    {
        LocalPlayerStore store = NewStore();

        RosterUser? first = store.AddRosterUser("Rival", gender: 1, level: 12);
        RosterUser? second = store.AddRosterUser("Second", gender: 0, level: 3);

        Assert.Multiple(() =>
        {
            Assert.That(first!.UserId, Is.Not.EqualTo(1u), "must not shadow the player");
            Assert.That(second!.UserId, Is.Not.EqualTo(first.UserId));
            Assert.That(first.Online, Is.True);
            // Female avatars live at a different icon base than male ones, so the drawn
            // icon has to follow the gender byte the same record carries at +62.
            Assert.That(second.Gender, Is.EqualTo(0));
            Assert.That(second.IconId, Is.Not.EqualTo(first.IconId));
        });

        Assert.That(store.AddRosterUser("Rival", 1, 1), Is.Null, "duplicate nickname");
        Assert.That(store.AddRosterUser("Blade", 1, 1), Is.Null, "the player's nickname");
    }

    [Test]
    public void RosterUserProducesACompleteUserRecord()
    {
        LocalPlayerStore store = NewStore();
        RosterUser user = store.AddRosterUser("Rival", gender: 0, level: 12)!;

        LobbyUserIdentity identity = user.ToIdentity();
        byte[] wire = new PacketFactory().Write(
            OnUserIdInfoAckPacket.Build([identity]));

        // One 67-byte record behind the 2-byte id and 5-byte header.
        Assert.That(wire, Has.Length.EqualTo(7 + OnUserIdInfoInfPacket.EntrySize));
        const int record = 7;
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt32(wire, record), Is.EqualTo(user.UserId));
            Assert.That(
                System.Text.Encoding.ASCII.GetString(wire, record + 29, 5),
                Is.EqualTo("Rival"), "nickname at +29 is what the list column draws");
            Assert.That(BitConverter.ToUInt32(wire, record + 58), Is.EqualTo(12u), "level");
            Assert.That(wire[record + 62], Is.EqualTo(0), "gender");
            // sub_4339F0 decrements record+63 on receipt, so the icon goes out one-based.
            Assert.That(
                BitConverter.ToUInt32(wire, record + 63),
                Is.EqualTo(user.IconId!.Value + 1));
        });
    }

    [Test]
    public void RemovingAUserAlsoDropsTheContactPointingAtIt()
    {
        // A contact whose record can no longer be resolved draws no row, which both hides
        // it and leaves sub_48C60C able to null-deref on a stale selection index.
        LocalPlayerStore store = NewStore();
        RosterUser user = store.AddRosterUser("Rival", 1, 5)!;
        Assert.That(
            store.Messenger(new MsgRegisterUserRequest(
                "Rival", 0, 0, MessengerOperation.AddFriend)),
            Is.EqualTo(MsgRegisterUserResult.Success),
            "a roster nickname must resolve, or it could never be befriended");

        Assert.That(
            store.Read(profile => profile.Messenger.Contacts.Select(c => c.UserId)),
            Does.Contain(user.UserId));

        Assert.That(store.RemoveRosterUser("Rival"), Is.True);
        Assert.That(
            store.Read(profile => profile.Messenger.Contacts), Is.Empty,
            "the dangling contact must go with it");
    }

    [Test]
    public void PresenceCanBeFlippedPerUserAndInBulk()
    {
        LocalPlayerStore store = NewStore();
        store.AddRosterUser("Rival", 1, 5);
        store.AddRosterUser("Second", 1, 5);

        Assert.That(store.SetRosterPresence("Rival", online: false), Is.EqualTo(1));
        Assert.That(
            store.Read(profile => profile.Roster.Count(user => user.Online)),
            Is.EqualTo(1));

        Assert.That(store.SetRosterPresence(null, online: false), Is.EqualTo(2));
        Assert.That(
            store.Read(profile => profile.Roster.Any(user => user.Online)), Is.False);
    }

    [Test]
    public void ProfileRejectsARosterThatWouldShadowThePlayer()
    {
        LocalPlayerProfile profile = new() { Nickname = "Blade", UserId = 1 };
        profile.Roster.Add(new RosterUser { UserId = 1, Nickname = "Fake" });
        Assert.Throws<InvalidDataException>(profile.Validate);

        profile.Roster.Clear();
        profile.Roster.Add(new RosterUser { UserId = 2, Nickname = "A" });
        profile.Roster.Add(new RosterUser { UserId = 2, Nickname = "B" });
        Assert.Throws<InvalidDataException>(profile.Validate);
    }

    [Test]
    public void RosterSurvivesASaveAndLoad()
    {
        // The contact list persists ids, so the accounts those ids name have to persist
        // too - otherwise a reconnect leaves contacts that resolve to nothing.
        LocalPlayerProfile profile = new() { Nickname = "Blade", UserId = 1 };
        profile.Roster.Add(new RosterUser
        {
            UserId = 2,
            AccountId = "NPC2",
            Nickname = "Rival",
            Gender = 0,
            Level = 12,
            Online = false
        });

        LocalPlayerProfile loaded = LocalPlayerProfileFile.Deserialize(
            LocalPlayerProfileFile.Serialize(profile));

        Assert.That(loaded.Roster, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Roster[0].Nickname, Is.EqualTo("Rival"));
            Assert.That(loaded.Roster[0].UserId, Is.EqualTo(2u));
            Assert.That(loaded.Roster[0].Online, Is.False);
        });
    }
}
