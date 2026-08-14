using System.Text;
using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

public class ChatPacketTest
{
    [TestCase(ChatMessageType.Notice)]
    [TestCase(ChatMessageType.Alert)]
    [TestCase(ChatMessageType.Styled8)]
    public void NativeChatDisplayModeRoundTrips(ChatMessageType type)
    {
        byte[] text = Encoding.ASCII.GetBytes("native chat effect");
        ChatMessage message = new(type, text, IsNullTerminated: false);

        ChatMessage parsed = OnChatInfPacket.Parse(OnChatInfPacket.Build(message));

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Type, Is.EqualTo(type));
            Assert.That(parsed.EncodedText, Is.EqualTo(text));
            Assert.That(parsed.IsNullTerminated, Is.False);
        });
    }

    [Test]
    public void AdminPlayerChatUsesGmDisplayNameWithoutReplacingIdentity()
    {
        const string nickname = "Blade";
        ChatMessage message = ChatMessage.FromPlayer(
            nickname,
            (uint)(AccountClassFlags.Normal | AccountClassFlags.Admin),
            Encoding.ASCII.GetBytes("hello"),
            ChatMessageType.Lobby);

        Assert.Multiple(() =>
        {
            // The tag PREFIXES the name - replacing it produced "<GM> > hello", a line
            // with no speaker at all.
            Assert.That(message.AsciiText, Is.EqualTo("<GM> Blade > hello"));
            Assert.That(nickname, Is.EqualTo("Blade"));
            Assert.That(message.Type, Is.EqualTo(ChatMessageType.Lobby));
            Assert.That(message.IsNullTerminated, Is.False);
        });
    }

    [Test]
    public void AdminTagIsNotDoubledWhenTheNicknameAlreadyCarriesIt()
    {
        ChatMessage message = ChatMessage.FromPlayer(
            "<GM> Blade",
            (uint)(AccountClassFlags.Normal | AccountClassFlags.Admin),
            Encoding.ASCII.GetBytes("hi"),
            ChatMessageType.Lobby);

        Assert.That(message.AsciiText, Is.EqualTo("<GM> Blade > hi"));
    }

    [TestCase((uint)AccountClassFlags.Normal)]
    [TestCase((uint)AccountClassFlags.GameMaster)]
    [TestCase((uint)AccountClassFlags.Observer)]
    public void PlayerChatWithoutAdminBitUsesRealNickname(uint accountClass)
    {
        ChatMessage message = ChatMessage.FromPlayer(
            "Blade",
            accountClass,
            Encoding.ASCII.GetBytes("hello"),
            ChatMessageType.Room);

        Assert.That(message.AsciiText, Is.EqualTo("Blade > hello"));
    }

    [Test]
    public void WhisperRequestRoundTripsRetailLayout()
    {
        byte[] text = Encoding.ASCII.GetBytes("secret message");
        WhisperRequest request = new("PLAYER", text);
        PacketFactory factory = new();
        factory.FillReadBuffer(factory.Write(WChatReqPacket.Build(request)));

        Packet framed = factory.ReadPacket()!;
        WhisperRequest parsed = WChatReqPacket.Parse(framed);

        Assert.Multiple(() =>
        {
            Assert.That(framed.Id, Is.EqualTo(PacketId.WChatReq));
            Assert.That(parsed.TargetNickname, Is.EqualTo("PLAYER"));
            Assert.That(parsed.EncodedText, Is.EqualTo(text));
        });
    }

    [Test]
    public void BigNewsUsesRetailOffsetsAndFixedSize()
    {
        Packet packet = OnBigNewsInfPacket.Build(
            new BigNewsMessage("SERVER", "Maintenance soon"));
        byte[] wire = new PacketFactory().Write(packet);
        BigNewsMessage parsed = OnBigNewsInfPacket.Parse(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(0x165));
            Assert.That(
                Encoding.ASCII.GetString(wire, 19, "SERVER".Length),
                Is.EqualTo("SERVER"));
            Assert.That(
                Encoding.ASCII.GetString(wire, 100, "Maintenance soon".Length),
                Is.EqualTo("Maintenance soon"));
            Assert.That(parsed, Is.EqualTo(
                new BigNewsMessage("SERVER", "Maintenance soon")));
        });
    }

    [TestCase("normal", 0x01u)]
    [TestCase("admin", 0x02u)]
    [TestCase("gm", 0x04u)]
    [TestCase("observer+mc", 0x18u)]
    [TestCase("all", 0x3Eu)]
    [TestCase("0x24", 0x24u)]
    public void AccountClassNamesUseRetailRoleBits(string text, uint expected)
    {
        bool parsed = AccountClassInfo.TryParse(text, out uint value);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(expected));
        });
    }

    [TestCase((uint)AccountClassFlags.Admin, true)]
    [TestCase((uint)(AccountClassFlags.Normal | AccountClassFlags.Admin), true)]
    [TestCase((uint)AccountClassFlags.Normal, false)]
    [TestCase((uint)AccountClassFlags.GameMaster, false)]
    [TestCase((uint)AccountClassFlags.Observer, false)]
    [TestCase((uint)(AccountClassFlags.Premium | AccountClassFlags.PcBang), false)]
    public void PacketTestAdministrationRequiresTheAdminBit(
        uint accountClass,
        bool expected)
    {
        Assert.That(AccountClassInfo.IsAdmin(accountClass), Is.EqualTo(expected));
    }

}
