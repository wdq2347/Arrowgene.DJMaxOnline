using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Japan400.Packets;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// Loads captured, byte-exact OnGameInfoInf payloads by disc id. Keeping the
/// variable chart data outside source code avoids guessed or embedded packet bodies.
/// </summary>
public sealed class GameInfoCatalog : IGameInfoProvider
{
    private readonly string _directory;
    private readonly uint _fallbackDiscId;

    public GameInfoCatalog(string directory, uint fallbackDiscId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (fallbackDiscId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fallbackDiscId), "Fallback disc id must be non-zero.");
        }
        _directory = Path.GetFullPath(directory);
        _fallbackDiscId = fallbackDiscId;
    }

    public string DirectoryPath => _directory;

    public bool TryLoad(
        uint discId,
        byte difficulty,
        short roomDescriptor,
        out GameInfoPayload? payload,
        out bool usedFallback,
        out string error,
        JudgmentWindows? windows = null)
    {
        // Captured .bin payloads carry their own baked-in scramble key and chosen
        // difficulty, so the room-descriptor int16 and requested difficulty do not
        // apply to them. Nor can the judgment windows: re-encoding the config block
        // needs the key inputs, and a captured blob does not carry them.
        _ = roomDescriptor;
        _ = difficulty;
        _ = windows;
        string path = Path.Combine(_directory, $"{discId}.bin");
        if (!File.Exists(path))
        {
            path = Path.Combine(_directory, $"{_fallbackDiscId}.bin");
            usedFallback = true;
        }
        else
        {
            usedFallback = false;
        }

        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Fallback game-info payload for disc {_fallbackDiscId} was not found.",
                    path);
            }

            byte[] bytes = File.ReadAllBytes(path);
            GameInfoPayload source = GameInfoPayload.Parse(bytes);
            if (!usedFallback && source.DiscId != discId)
            {
                throw new InvalidDataException(
                    $"Payload contains disc {source.DiscId}, not requested disc {discId}.");
            }
            if (usedFallback)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(GameplayProtocol.GameInfoDiscIdOffset), discId);
            }

            payload = GameInfoPayload.Parse(bytes);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or ArgumentException)
        {
            payload = null;
            usedFallback = false;
            error = $"Invalid game-info payload {path}: {exception.Message}";
            return false;
        }
    }
}
