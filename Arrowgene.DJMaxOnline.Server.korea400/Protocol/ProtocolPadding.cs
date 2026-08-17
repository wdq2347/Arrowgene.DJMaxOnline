namespace Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

public static class ProtocolPadding
{
    /// <summary>
    /// The retail protocol fills deliberately unused bytes with 0xCC. Giving
    /// that sentinel a name keeps it out of packet construction code.
    /// </summary>
    public const byte Unused = 0xCC;

    /// <summary>The same unused-field sentinel represented as a 32-bit value.</summary>
    public const uint UnusedUInt32 = 0xCCCCCCCCu;
}
