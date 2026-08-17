using System.Text;
using Arrowgene.DJMaxOnline.Server.Korea400;

namespace Arrowgene.DJMaxOnline.Test;

public class SongCatalogTest
{
    [Test]
    public void LoadsDuplicateChartHeadersAndBuildsExpectedFileNames()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"DiscStock-{Guid.NewGuid():N}.csv");
        try
        {
            string header =
                "ID_,Title,Subtitle,Version,Genre,Genre2,BPM,Composer,Writer," +
                "Arranger,Artist,Singer,PlayTime,Tag,VersionTag,OwnBG,MVFee," +
                "License,Status,Remark,DiscLevel_EZ_NM_HD_MX_SC," +
                "UserLevel_EZ_NM_HD_MX_SC,DiscLevel_EZ_NM_HD_MX_SC," +
                "UserLevel_EZ_NM_HD_MX_SC,PlayFeeSingle,PlayFeeMultiple," +
                "Premium,RefSpeed_5K,RefSpeed_7K";
            string row =
                "11,\"JBG, Remix\",_,Original,Hip_Hop,hr,102,Groove,JC," +
                "Groove,Eyehead,JC,129,JBG,ORG,own,0,Pentavision,onair,_," +
                "3_6_11_13_14,1_6_11_3_13,3_8_11_12_14,1_8_11_3_13," +
                "0_0_0_150_150,0_0_0_150_150,0," +
                "3.5_3.5_4_4.5_4.5,4.5_4.5_5_5_4";
            File.WriteAllText(
                path, $"{header}{Environment.NewLine}{row}{Environment.NewLine}",
                Encoding.GetEncoding(936));

            SongCatalog catalog = SongCatalog.Load(path);
            SongDefinition song = catalog.Get(11);

            Assert.Multiple(() =>
            {
                Assert.That(catalog.Count, Is.EqualTo(1));
                Assert.That(song.Title, Is.EqualTo("JBG, Remix"));
                Assert.That(song.Tag, Is.EqualTo("JBG"));
                Assert.That(song.Charts, Has.Count.EqualTo(10));
                Assert.That(
                    song.FindChart(SongKeyMode.FiveKey, SongDifficulty.Normal)?.DiscLevel,
                    Is.EqualTo(6));
                Assert.That(
                    song.FindChart(SongKeyMode.SevenKey, SongDifficulty.Maximum)?.ReferenceSpeed,
                    Is.EqualTo(5m));
                Assert.That(
                    song.ChartFileName(SongKeyMode.SevenKey, SongDifficulty.Maximum),
                    Is.EqualTo("jbg_7kMX.pt"));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void RandomPickUsesAPlayableChartAndAvoidsTheCurrentSong()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"DiscStock-random-{Guid.NewGuid():N}.csv");
        try
        {
            string header =
                "ID_,Title,Subtitle,Version,Genre,Genre2,BPM,Composer,Writer," +
                "Arranger,Artist,Singer,PlayTime,Tag,VersionTag,OwnBG,MVFee," +
                "License,Status,Remark,DiscLevel_EZ_NM_HD_MX_SC," +
                "UserLevel_EZ_NM_HD_MX_SC,DiscLevel_EZ_NM_HD_MX_SC," +
                "UserLevel_EZ_NM_HD_MX_SC,PlayFeeSingle,PlayFeeMultiple," +
                "Premium,RefSpeed_5K,RefSpeed_7K";
            static string Row(uint id, string tag, string fiveKeyLevels) =>
                $"{id},Song {id},_,Original,Hip_Hop,hr,102,Groove,JC," +
                $"Groove,Eyehead,JC,129,{tag},ORG,own,0,Pentavision,onair,_," +
                $"{fiveKeyLevels},1_1_1_1_1,99_99_99_99_99,1_1_1_1_1," +
                "0_0_0_150_150,0_0_0_150_150,0," +
                "3.5_3.5_4_4.5_4.5,4.5_4.5_5_5_4";
            string[] rows =
            [
                Row(1, "one", "3_6_11_13_14"),
                Row(2, "unavailable", "99_6_11_13_14"),
                Row(3, "three", "4_7_12_14_15")
            ];
            File.WriteAllText(
                path,
                string.Join(Environment.NewLine, new[] { header }.Concat(rows)) +
                Environment.NewLine,
                Encoding.GetEncoding(936));

            SongCatalog catalog = SongCatalog.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(catalog.RandomDiscIndex, Is.EqualTo(3));
                Assert.That(catalog.TryPickRandomPlayable(
                    SongKeyMode.FiveKey,
                    SongDifficulty.Easy,
                    excludedCatalogId: 1,
                    new Random(1234),
                    out SongDefinition selected), Is.True);
                Assert.That(selected.Id, Is.EqualTo(3),
                    "RANDOM must not repeat the current song when another playable one exists");
                Assert.That(catalog.TryPickRandomPlayable(
                    SongKeyMode.SevenKey,
                    SongDifficulty.Easy,
                    excludedCatalogId: null,
                    new Random(1234),
                    out _), Is.False,
                    "Unavailable charts must not be chosen");
            });
        }
        finally
        {
            File.Delete(path);
        }
    }
}
