using System.Text;
using Arrowgene.DJMaxOnline.Server.Japan400;
using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline;

public static class ProtocolReportWriter
{
    private static readonly IReadOnlyDictionary<PacketId, string> HandlerNotes =
        new Dictionary<PacketId, string>
        {
            [PacketId.OnConnectAck] =
                "raw+3 result:u16; +5 assignedUserId:i16; +7 cipherSeed[32], first two dwords complemented",
            [PacketId.OnAuthenticateInAck] =
                "raw+3 userId:u32; +7 class:u32; +11 level:u32; +15 result:u16; strings at +21/+46/+67",
            [PacketId.OnChannelInfoInf] =
                "declared wire size at raw+3; 90-byte channel entries start at raw+7",
            [PacketId.OnLogInAck] =
                "status:u16 at raw+7; 32-byte seed at raw+9; signed values at +41/+43/+45",
            [PacketId.OnUserInfoInf] = "copies 0x87 bytes from raw+3",
            [PacketId.OnInventoryInfoInf] = "copies 0x2EC bytes from raw+3",
            [PacketId.OnMessengerInfoInf] = "copies 0x3B6 bytes from raw+3",
            [PacketId.OnUserIdInfoInf] =
                "declared wire size at raw+3; 67-byte entries start at raw+7",
            [PacketId.OnChatInf] =
                "declared wire size at raw+3; type:u8 at raw+7; text begins raw+8",
            [PacketId.OnGameInfoInf] =
                "declared wire size at raw+3; variable payload (observed up to 22,955 bytes)",
            [PacketId.OnAlertCreditInf] =
                "fixed 7 bytes; raw+3 is credit/status data, not a length",
            [PacketId.OnGameStartInf] =
                "raw+3/+4 bytes; five u32 values at +5/+9/+13/+17/+21; 8 reserved bytes",
            [PacketId.OnDisconnectPeerInf] = "reads signed user id at raw+3",
            [PacketId.OnAuthenticateInSndeKeyReq] = "copies 8 bytes from raw+3",
            [PacketId.OnUpdateUserIconInf] = "reads u32 at +3, u16 at +7, u32 at +9",
            [PacketId.OnUpdateUserPropertyLevelInf] = "reads u32 at +3/+9/+13",
            [PacketId.OnUpdateUserPropertyMoneyInf] = "reads u32 at +3/+9",
            [PacketId.OnUpdateUserPropertyRecordInf] = "reads u32 at +3/+9/+13/+17",
            [PacketId.OnUpdateUserAccountClassInf] =
                "userId:u32 at raw+3; complete accountClass mask:u32 at raw+7",
            [PacketId.OnReserved34Inf] =
                "fixed 4-byte reserved ack; scene callbacks ignore it",
            [PacketId.OnWChatInf] = "dynamic server whisper result/display",
            [PacketId.OnSlotControlAck] = "slot index:u8 at raw+3; enabled:u8 at raw+4",
            [PacketId.OnSlotControlInf] = "handler identified; semantics unresolved",
            [PacketId.OnEventInfoInf] = "reads u16 at +5/+7/+9 and u32 at +11/+15",
            [PacketId.OnCourseListInf] = "declared wire size at raw+3; u16 entries from raw+7",
            [PacketId.OnBigNewsInf] =
                "fixed 357 bytes; title at raw+19 (81-byte field), body at raw+100 (257-byte field)",
            [PacketId.OnChatControlInf] = "chat flood control state",
            [PacketId.OnQuickInviteAck] = "reads u32 at raw+3 and raw+7",
            [PacketId.OnUseItemAck] = "passes structure beginning raw+3 to item handler",
            [PacketId.OnBillingAuthInf] = "reads raw+3, signed raw+9, and byte raw+11",
            [PacketId.OnUserAlertInf] = "reads six u32 values at raw+3 through raw+23",
            [PacketId.OnMsgChatInf] = "messenger conversation line",
            [PacketId.OnUbsAccountAuthenResAck] =
                "registered as fixed 8 but absent from JPmax sub_430250; received and discarded"
        };

    public static void Write(string outputPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        int captured = Count(ReceiveFramingEvidence.CaptureVerified);
        int declared = Count(ReceiveFramingEvidence.DeclaredButUncaptured);
        int handlerOnly = Count(ReceiveFramingEvidence.HandlerOnly);

        StringBuilder report = new();
        report.AppendLine("# DJMax Online receive protocol map");
        report.AppendLine();
        report.AppendLine(
            $"`sub_430250` dispatches {ReceivePacketCatalog.All.Count} unique receive IDs; " +
            $"the JPmax registration table registers {ReceivePacketCatalog.Registrations.Count}. " +
            $"The supplied captures prove framing for {captured}; {declared} more have declared " +
            $"metadata but do not occur in this corpus; {handlerOnly} are handler-only and must " +
            "not be assigned guessed sizes.");
        report.AppendLine();
        report.AppendLine("## Confirmed corrections");
        report.AppendLine();
        report.AppendLine("- Cipher `sumSeed` is a signed little-endian 16-bit value at seed offset 28, sign-extended to `uint`.");
        report.AppendLine("- Three-byte packets do not advance the cipher stream because they have no encrypted body.");
        report.AppendLine("- Dynamic packets use the little-endian total wire size stored at raw offset 3.");
        report.AppendLine("- `OnGameInfoInf` is dynamic; the previous fixed size of 9,841 bytes was incorrect.");
        report.AppendLine("- `OnAlertCreditInf` is fixed at 7 bytes; its dword at raw offset 3 is data, not framing.");
        report.AppendLine();
        report.AppendLine("## Dispatcher cases");
        report.AppendLine();
        report.AppendLine("| ID | Name | Client handler | Framing | Evidence | IDA observation |");
        report.AppendLine("|---:|---|---:|---|---|---|");

        foreach (ReceivePacketDefinition definition in ReceivePacketCatalog.All
                     .OrderBy(value => (ushort)value.Id))
        {
            ReceiveFramingEvidence evidence =
                ReceivePacketCatalog.GetFramingEvidence(definition.Id);
            string framing = ReceivePacketCatalog.TryGetRegistration(
                    definition.Id, out ReceivePacketRegistration registration)
                ? registration.IsDynamic ? "dynamic" : $"{registration.WireSize} bytes"
                : definition.Id == PacketId.OnPlaySkipInf
                    ? "3-byte unregistered fallback"
                    : "unregistered";
            string note = HandlerNotes.TryGetValue(definition.Id, out string? value)
                ? value
                : string.Empty;
            report.AppendLine(
                $"| `0x{(ushort)definition.Id:X}` | `{definition.Id}` | " +
                $"`0x{definition.HandlerAddress:X}` | {framing} | {EvidenceLabel(evidence)} | {note} |");
        }

        report.AppendLine();
        report.AppendLine("## Implementation boundary");
        report.AppendLine();
        report.AppendLine(
            "A dispatcher case proves the packet ID and client handler, but often not the total " +
            "wire length: handlers may ignore reserved tails or consume only a pointer passed to " +
            "another function. Handler-only rows therefore need either an observed packet or the " +
            "server-side packet construction site before byte-exact framing can be implemented.");

        File.WriteAllText(outputPath, report.ToString());
    }

    private static int Count(ReceiveFramingEvidence evidence) =>
        ReceivePacketCatalog.All.Count(definition =>
            ReceivePacketCatalog.GetFramingEvidence(definition.Id) == evidence);

    private static string EvidenceLabel(ReceiveFramingEvidence evidence) => evidence switch
    {
        ReceiveFramingEvidence.CaptureVerified => "capture-verified",
        ReceiveFramingEvidence.DeclaredButUncaptured => "declared, uncaptured",
        ReceiveFramingEvidence.HandlerOnly => "handler-only",
        _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null)
    };
}
