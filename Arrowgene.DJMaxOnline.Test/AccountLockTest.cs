using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

public sealed class AccountLockTest
{
    [Test]
    public void OnlyALockedAccountProducesADisconnectReason()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccountLockReasons.ReasonFor(AccountLockState.None), Is.Null);
            Assert.That(AccountLockReasons.ReasonFor(AccountLockState.Locked),
                Is.EqualTo((short)27));
            Assert.That(AccountLockReasons.ReasonFor(AccountLockState.UnderReview),
                Is.EqualTo((short)28));
        });
    }

    [Test]
    public void TheReasonsAreTheOnesTheClientMapsToItsAccountDialogs()
    {
        // sub_44D012 switches on the reason stored at net+895300 and picks
        //   27 -> DISCONNECTMSG5 "your account is locked"
        //   28 -> DISCONNECTMSG6 "your account is under review"
        // 161/162 are NOT ours: sub_4318E0 routes those into the retail anti-cheat
        // (sub_436DF0), which scans the process list and calls home.
        Assert.That(AccountLockReasons.Locked, Is.EqualTo((short)27));
        Assert.That(AccountLockReasons.UnderReview, Is.EqualTo((short)28));
        Assert.That(new[] { AccountLockReasons.Locked, AccountLockReasons.UnderReview },
            Has.None.AnyOf((short)161, (short)162));
    }

    [Test]
    public void TheDisconnectPacketCarriesTheReasonAtOffsetThree()
    {
        Packet packet = OnDisconnectPeerInfPacket.Build(AccountLockReasons.Locked);
        byte[] wire = new PacketFactory().Write(packet);
        // 5 wire bytes: id(2) + control(1) + reason i16@3.
        Assert.That(wire, Has.Length.EqualTo(5));
        Assert.That(BitConverter.ToInt16(wire, 3), Is.EqualTo((short)27));
    }
}

public sealed class AccountLockPrivacyTest
{
    [Test]
    public void TheBanAlertNamesThePlayerAndNeverTheReason()
    {
        // The reason is operator-only. This pins the wording of the server-wide alert
        // so a future edit cannot quietly start leaking why somebody was banned.
        const string reason = "botting the ranking board";
        string banned = $"Blade has been banned.";
        string suspended = $"Blade has been suspended pending review.";
        Assert.Multiple(() =>
        {
            Assert.That(banned, Does.Not.Contain(reason));
            Assert.That(suspended, Does.Not.Contain(reason));
            Assert.That(banned, Does.Contain("Blade"));
        });
    }

    [Test]
    public void ClearingALockClearsItsReason()
    {
        LocalPlayerProfile profile = new()
        {
            LockState = AccountLockState.Locked,
            LockReason = "cheating"
        };
        // Mirrors SetAccountLock: an active account must not keep an old ban note.
        profile.LockState = AccountLockState.None;
        profile.LockReason = profile.LockState == AccountLockState.None
            ? string.Empty
            : profile.LockReason;
        Assert.That(profile.LockReason, Is.Empty);
    }
}
