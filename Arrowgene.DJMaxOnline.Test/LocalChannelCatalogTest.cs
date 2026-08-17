using System.Net;
using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Test;

[TestFixture]
public sealed class LocalChannelCatalogTest
{
    [Test]
    public void ConfiguredServerNameBuildsBothSessionAndFriendlyChannelNames()
    {
        Setting setting = new()
        {
            Name = "Portable Server",
            AdvertisedIpAddress = IPAddress.Parse("192.0.2.10")
        };

        IReadOnlyList<ChannelInfo> channels = LocalChannelCatalog.Create(setting);
        channels =
        [
            channels[0] with { UserCount = 12 },
            channels[1] with { UserCount = 34 }
        ];

        Assert.Multiple(() =>
        {
            Assert.That(channels[0].Name, Is.EqualTo("SEOUL"));
            Assert.That(channels[0].Description,
                Is.EqualTo(".[5KEY] Portable Server"));
            Assert.That(channels[0].FullName,
                Is.EqualTo("[5KEY] Portable Server"));
            Assert.That(channels[1].Name, Is.EqualTo("TOKYO"));
            Assert.That(channels[1].Description,
                Is.EqualTo(".[7KEY] Portable Server"));
            Assert.That(channels[1].FullName,
                Is.EqualTo("[7KEY] Portable Server"));
        });

        IReadOnlyList<ChannelInfo> wire = OnChannelInfoInfPacket.Parse(
            OnChannelInfoInfPacket.Build(channels));
        Assert.Multiple(() =>
        {
            Assert.That(wire[0].Description,
                Is.EqualTo(".[5KEY] Portable Server"));
            Assert.That(wire[1].Description,
                Is.EqualTo(".[7KEY] Portable Server"));
            Assert.That(wire[0].UserCount, Is.EqualTo(12));
            Assert.That(wire[1].UserCount, Is.EqualTo(34));
        });
    }

    [Test]
    public void NameThatCannotFitRetailChannelFieldIsRejectedClearly()
    {
        Setting setting = new() { Name = new string('X', 25) };

        Assert.That(
            () => LocalChannelCatalog.Create(setting),
            Throws.ArgumentException.With.Message.Contains("too long"));
    }
}
