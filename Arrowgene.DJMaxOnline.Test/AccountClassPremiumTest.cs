using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Mirrors the client's premium test so the server's model cannot drift from it:
///   sub_405790 shows "Premium" when
///   sub_42922E() && (sub_428DCC() || sub_428DE6() || sub_428E59())
/// with sub_42922E = (ac & 0x70000) != 0 || (ac & 0x3E) != 0,
///      sub_428DCC = ac & 0x10000, sub_428DE6 = ac & 0x20000, sub_428E59 = ac & 0x3E.
/// </summary>
public class AccountClassPremiumTest
{
    [Test]
    public void NamedFlagsBuildTheAccountClassValue()
    {
        // The point of the named form is that a config carries readable switches instead
        // of a hand-computed number.
        uint premium = AccountClassInfo.FromNames(
            ["Premium"], out IReadOnlyList<string> unknown);

        Assert.Multiple(() =>
        {
            Assert.That(premium, Is.EqualTo((uint)AccountClassFlags.Premium));
            Assert.That(unknown, Is.Empty);
            // Premium alone satisfies the client's premium test...
            Assert.That(premium & AccountClassInfo.PremiumMask, Is.Not.Zero);
            // ...without making anyone staff.
            Assert.That(premium & AccountClassInfo.PrivilegedMask, Is.Zero);
            // Case-insensitive, and several flags combine.
            Assert.That(
                AccountClassInfo.FromNames(["premium", "PCBANG"], out _),
                Is.EqualTo((uint)(AccountClassFlags.Premium | AccountClassFlags.PcBang)));
            Assert.That(
                AccountClassInfo.ToNames((uint)AccountClassFlags.Premium),
                Does.Contain("Premium"));
        });

        // Unknown names are reported, not silently dropped.
        AccountClassInfo.FromNames(["Premium", "Nonsense"], out IReadOnlyList<string> bad);
        Assert.That(bad, Is.EqualTo(new[] { "Nonsense" }));
    }

    private static bool ClientShowsPremium(uint ac)
    {
        bool tier = (ac & 0x70000) != 0 || (ac & 0x3E) != 0;
        bool grants = (ac & 0x10000) != 0 || (ac & 0x20000) != 0 || (ac & 0x3E) != 0;
        return tier && grants;
    }

    [TestCase(0x00000u, false, TestName = "no flags")]
    [TestCase(0x00001u, false, TestName = "Normal alone is not premium")]
    [TestCase(0x00002u, true, TestName = "Admin incidentally shows premium")]
    [TestCase(0x00020u, true, TestName = "Challenger incidentally shows premium")]
    [TestCase(0x10000u, true, TestName = "Premium bit")]
    [TestCase(0x20000u, true, TestName = "PremiumAlternate bit")]
    [TestCase(0x40000u, false, TestName = "TierBit18 alone is NOT premium")]
    [TestCase(0x80000u, false, TestName = "IconBadge alone is not premium")]
    [TestCase(0x10001u, true, TestName = "Normal + Premium")]
    public void PremiumMaskMatchesTheClient(uint accountClass, bool expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccountClassInfo.IsPremium(accountClass), Is.EqualTo(expected));
            Assert.That(
                ClientShowsPremium(accountClass),
                Is.EqualTo(expected),
                "the reference implementation of the client's own check disagrees");
        });
    }

    [Test]
    public void EveryBitAgreesWithTheClientCheck()
    {
        // Exhaustive over each single bit plus a few combinations.
        for (int bit = 0; bit < 32; bit++)
        {
            uint value = 1u << bit;
            Assert.That(
                AccountClassInfo.IsPremium(value),
                Is.EqualTo(ClientShowsPremium(value)),
                $"bit 0x{value:X} disagrees");
        }
    }

    /// <summary>
    /// The complete set of accountClass masks the client ever tests, enumerated from the
    /// predicate block at 0x4284FB-0x428EB4 (every `and`/`test` against the field). If a
    /// future bit is discovered, add it here and to AccountClassFlags together.
    /// </summary>
    [Test]
    public void KnownMaskCoversEveryBitTheClientTests()
    {
        uint[] clientTestedBits =
        [
            0x02, 0x04, 0x08, 0x10, 0x20, 0x40,          // roles + the icon-only flag
            0x10000, 0x20000, 0x40000, 0x80000           // subscription/badge bits
        ];
        // 0x01 is never masked by any predicate — it is only a "not blank" marker.
        uint composites = 0x06u | 0x3Eu | 0x70000u;      // operator, staff, tier

        Assert.Multiple(() =>
        {
            foreach (uint bit in clientTestedBits)
            {
                Assert.That(
                    AccountClassInfo.KnownMask & bit,
                    Is.EqualTo(bit),
                    $"bit 0x{bit:X} is tested by the client but missing from KnownMask");
            }
            Assert.That(AccountClassInfo.OperatorMask, Is.EqualTo(0x06u));
            Assert.That(AccountClassInfo.PrivilegedMask, Is.EqualTo(0x3Eu));
            Assert.That(AccountClassInfo.PremiumMask, Is.EqualTo(0x3003Eu));
            // Composites must be expressible from the known bits.
            Assert.That(composites & ~AccountClassInfo.KnownMask, Is.Zero);
        });
    }

    [Test]
    public void PlayerJsonAcceptsNamedAccountClassElements()
    {
        const string json = """
            {
              "accountClass": {
                "normal": true,
                "challenger": true,
                "jjang": true,
                "premium": false,
                "credit": false,
                "pcBang": true,
                "unknownBits": 2147483648
              }
            }
            """;

        LocalPlayerProfile profile = LocalPlayerProfileFile.Deserialize(json);

        Assert.That(
            profile.AccountClass,
            Is.EqualTo(0x80080061u));
    }

    [Test]
    public void PlayerJsonRoundTripPreservesLegacyAndUnknownBits()
    {
        LocalPlayerProfile profile = LocalPlayerProfileFile.Deserialize(
            """{ "accountClass": 2309737967 }""");

        string json = LocalPlayerProfileFile.Serialize(profile);
        LocalPlayerProfile roundTrip = LocalPlayerProfileFile.Deserialize(json);

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip.AccountClass, Is.EqualTo(0x89ABCDEFu));
            Assert.That(json, Does.Contain("\"jjang\": true"));
            Assert.That(json, Does.Contain("\"pcBang\": true"));
            Assert.That(json, Does.Contain("\"unknownBits\": 2309016960"));
        });
    }
}
