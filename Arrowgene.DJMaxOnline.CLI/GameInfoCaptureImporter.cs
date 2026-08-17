using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline;

public sealed record GameInfoImportResult(
    int CaptureCount,
    int ObservedPayloads,
    int ImportedDiscs,
    IReadOnlyList<uint> DiscIds);

/// <summary>Extracts validated OnGameInfoInf chart payloads from packet captures.</summary>
public static class GameInfoCaptureImporter
{
    public static GameInfoImportResult ImportDirectory(
        string captureDirectory,
        string outputDirectory)
    {
        captureDirectory = Path.GetFullPath(captureDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);
        string[] files = Directory.GetFiles(captureDirectory, "*.yaml")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"No YAML captures found in {captureDirectory}.");
        }

        Directory.CreateDirectory(outputDirectory);
        Dictionary<uint, byte[]> payloads = [];
        int observed = 0;
        foreach (string file in files)
        {
            ImportFile(file, payloads, ref observed);
        }

        foreach ((uint discId, byte[] bytes) in payloads)
        {
            File.WriteAllBytes(Path.Combine(outputDirectory, $"{discId}.bin"), bytes);
        }

        return new GameInfoImportResult(
            files.Length,
            observed,
            payloads.Count,
            payloads.Keys.Order().ToArray());
    }

    private static void ImportFile(
        string capturePath,
        IDictionary<uint, byte[]> payloads,
        ref int observed)
    {
        PacketReader reader = new();
        List<PacketReader.PcapPacket> chunks = reader.ReadYamlPcap(
            File.ReadAllText(capturePath));
        PacketFactory server = new();
        PacketFactory client = new();
        foreach (PacketReader.PcapPacket chunk in chunks)
        {
            PacketFactory factory = chunk.Source == PacketSource.Server ? server : client;
            if (chunk.Source == PacketSource.Server && chunk.Data.Length >= 2 &&
                chunk.Data[0] == 0x0A && chunk.Data[1] == 0x00)
            {
                chunk.Data[0] = 0x09;
            }

            factory.FillReadBuffer(chunk.Data);
            while (factory.ReadPacket() is { } packet)
            {
                if (packet.Id == PacketId.OnConnectAck)
                {
                    server.InitCrypto(DjMaxCrypto.FromOnConnectAckPacket(packet));
                    client.InitCrypto(DjMaxCrypto.FromOnConnectAckPacket(packet));
                }

                if (packet.Id != PacketId.OnGameInfoInf)
                {
                    continue;
                }

                GameInfoPayload payload = OnGameInfoInfPacket.Parse(packet);
                observed++;
                if (!payloads.ContainsKey(payload.DiscId))
                {
                    payloads.Add(payload.DiscId, payload.Bytes);
                }
            }
        }
    }
}
