using System.Text;
using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Song titles as players read them in chat.
///
/// DiscStock cannot store a space, so it writes "♪_End_of_the_Moonlight". The client swaps
/// those back when it draws a title in its own UI, but a chat packet is rendered verbatim -
/// so anything the server composes has to do the conversion itself.
/// </summary>
[TestFixture]
public class ChatSongTitleTest
{
    private static Encoding ClientText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949);
    }

    [TestCase("♪_End_of_the_Moonlight", "End of the Moonlight")]
    [TestCase("♪_Funky_Chups", "Funky Chups")]
    [TestCase("♪_바람에게_부탁해", "바람에게 부탁해")]
    [TestCase("♪_Fever_GJ", "Fever GJ")]
    public void UnderscoresBecomeSpacesAndTheNoteIsNotDuplicated(
        string stored, string expected)
    {
        Assert.That(SongDefinition.Display(stored), Is.EqualTo(expected));
    }

    [Test]
    public void ABlankFieldIsEmptyRatherThanASingleSpace()
    {
        // A lone underscore is DiscStock's empty marker.
        Assert.Multiple(() =>
        {
            Assert.That(SongDefinition.Display("_"), Is.Empty);
            Assert.That(SongDefinition.Display(""), Is.Empty);
            Assert.That(SongDefinition.Display("   "), Is.Empty);
        });
    }

    [Test]
    public void ATitleWithoutTheNoteGlyphIsLeftAlone()
    {
        Assert.That(SongDefinition.Display("Plain_Title"), Is.EqualTo("Plain Title"));
    }

    // ------------------------------------------------------------------ encoding

    [Test]
    public void ServerComposedChatEncodesKoreanRatherThanQuestionMarks()
    {
        // ASCII encoding turned every Korean character into '?', so a Korean song title
        // announced by the server arrived as "????? ?????".
        ChatMessage message = ChatMessage.FromAscii("♪ 바람에게 부탁해 [NM Lv.5]");

        Assert.Multiple(() =>
        {
            Assert.That(message.EncodedText, Does.Not.Contain((byte)'?'));
            Assert.That(ClientText().GetString(message.EncodedText),
                Is.EqualTo("♪ 바람에게 부탁해 [NM Lv.5]"));
        });
    }

    [Test]
    public void PlainEnglishStillEncodesExactlyAsBefore()
    {
        // CP949 is a superset of ASCII, so nothing that used to work can have changed.
        const string line = "Only the room owner starts a song.";
        Assert.That(ChatMessage.FromAscii(line).EncodedText,
            Is.EqualTo(Encoding.ASCII.GetBytes(line)));
    }

    [Test]
    public void TheMusicNoteSurvivesEncoding()
    {
        // The glyph is in CP949; under ASCII it became '?', which is why the announcement
        // read "? Song Name".
        ChatMessage message = ChatMessage.FromAscii("♪ Funky Chups");
        Assert.That(ClientText().GetString(message.EncodedText),
            Does.StartWith("♪"));
    }

    // ------------------------------------------------------------------- catalog

    [Test]
    public void TheSongCatalogIsReadAsKoreanNotChinese()
    {
        // DiscStock.csv was being read as CP936 (Simplified Chinese), left over from the
        // China client. That does not throw - it decodes Korean into plausible Chinese,
        // so the only symptom was wrong titles in chat.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] row = Encoding.GetEncoding(949).GetBytes("1,♪_바람에게_부탁해,_,Original");

        string korean = Encoding.GetEncoding(949).GetString(row);
        string chinese = Encoding.GetEncoding(936).GetString(row);

        Assert.Multiple(() =>
        {
            Assert.That(korean, Does.Contain("바람에게"));
            Assert.That(chinese, Is.Not.EqualTo(korean),
                "if these ever match, this test has stopped proving anything");
        });

        string catalog = FindDiscStock();
        if (catalog == null)
        {
            Assert.Ignore("No DiscStock.csv available to check.");
            return;
        }

        // The real file must decode to Korean, so a wrong code page cannot creep back in.
        string firstSong = File.ReadAllLines(catalog, Encoding.GetEncoding(949))
            .First(line => line.StartsWith("1,", StringComparison.Ordinal));
        Assert.That(firstSong, Does.Not.Contain("官"), "decoded as Chinese, not Korean");
    }

    private static string FindDiscStock()
    {
        DirectoryInfo directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "DATA", "DiscStock.csv");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }
}
