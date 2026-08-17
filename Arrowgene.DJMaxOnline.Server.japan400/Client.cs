using Arrowgene.Logging;
using System.Linq;
using Arrowgene.Networking.Tcp;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

public class Client
{
    private static readonly ServerLogger Logger = LogProvider.Logger<ServerLogger>(typeof(PacketFactory));

    public Client(ITcpSocket socket, PacketFactory packetFactory)
    {
        _socket = socket;
        _packetFactory = packetFactory;
        Identity = socket.Identity;
        UpdateIdentity();
    }

    private readonly ITcpSocket _socket;
    private readonly PacketFactory _packetFactory;

    /// <summary>
    /// Serializes encrypt-then-transmit. The send cipher is a STREAM cipher: every
    /// PacketFactory.Write advances DjMaxCrypto's _enc state, so the bytes on the wire
    /// only decrypt if they arrive in the exact order they were encrypted. Without this
    /// lock two threads can encrypt as A,B and reach the socket as B,A - after which every
    /// following byte the client decrypts is garbage and the whole UI renders blank.
    /// DjMaxCryptoState also reuses instance scratch buffers, so concurrent Write calls
    /// corrupt each other's output outright rather than merely reordering it.
    /// </summary>
    private readonly object _sendLock = new();


    public string Identity { get; protected set; }

    public DateTime PingTime { get; set; }

    /// <summary>
    /// When this connection last sent anything. A client that crashes or is killed often
    /// leaves its TCP socket open, so the disconnect callback never fires and the player
    /// stays in the lobby/room forever; the keepalive sweep uses this to evict it.
    /// </summary>
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The seed negotiated by ConnectReq/OnConnectAck for this socket.</summary>
    public byte[]? CipherSeed { get; set; }

    /// <summary>
    /// The full 32-byte seed made when the socket opened, waiting to go out in the
    /// OnConnectAck that answers the client's connect request. Cleared once sent.
    /// </summary>
    public byte[]? PendingConnectSeed { get; set; }

    public uint? UserId { get; set; }

    /// <summary>
    /// SQLite-backed account authenticated for this socket. This is deliberately
    /// connection-scoped: selecting one account when the server starts must not make
    /// every connected client operate on that same player's inventory and scores.
    /// </summary>

    public LocalPlayerStore? PlayerStore { get; set; }

    /// <summary>
    /// Opaque lease for the launcher session attached to this socket. Disconnect
    /// cleanup releases it and opens the short channel-transition grace period.
    /// </summary>
    public LoginSessionLease? LoginSessionLease { get; set; }

    /// <summary>
    /// This socket's authenticated player. The fallback exists only for the legacy
    /// single-player mode that has no account database; with a database there is no
    /// fallback, and Consumer drops every player packet until a login attaches a store.
    /// </summary>
    public LocalPlayerStore PlayerStoreOr(LocalPlayerStore? fallback) =>
        PlayerStore ?? fallback ?? throw new InvalidOperationException(
            "This connection has no authenticated player.");

    /// <summary>
    /// Set when this player started the current stage in the client's video mode, which
    /// auto-plays the chart. An auto-played run must not be scored or paid, so this is
    /// checked when the stage result arrives and cleared at the next start.
    /// </summary>
    public bool VideoMode { get; set; }

    /// <summary>
    /// Chat rate limiting for this connection. Per-client because the mute is personal
    /// and has to survive between messages.
    /// </summary>
    public ChatFloodControl ChatFlood { get; } = new();

    /// <summary>
    /// Set once this connection has been told to go and must hear nothing more.
    ///
    /// The client stores a disconnect reason in ONE field that every packet handler zeroes
    /// on entry, so a single reply - even an automatic keepalive echo - erases the reason
    /// before the client can draw the dialog explaining it. Consumer drops everything from a
    /// kicked client so no handler can answer.
    /// </summary>
    public bool Kicked { get; private set; }

    /// <summary>
    /// Stops the server answering this connection, without sending anything yet.
    ///
    /// Needed when a kick is a SEQUENCE of packets rather than one: the reason the client
    /// finally displays lives in a single field that every one of its handlers zeroes on
    /// entry, so nothing of ours - not even a keepalive echo - may go out in between.
    /// </summary>
    public void MarkKicked() => Kicked = true;

    /// <summary>
    /// Transient socket identity assigned by OnConnectAck. Room-member packets use
    /// this value to let the client recognize its own joiner record.
    /// </summary>
    public ushort AssignedUserId { get; set; }

    /// <summary>
    /// Course the client last selected with ChangeCourseReq (0x85). The course-mode
    /// requests that follow carry no course id of their own, so the selection has to be
    /// remembered here to attribute a ranking query or a reward claim to a course.
    /// </summary>
    public ushort? SelectedCourseId { get; set; }

    /// <summary>
    /// Zero-based stage within <see cref="SelectedCourseId"/>. A course start carries no
    /// song of its own, so the server tracks how far through the course the client is in
    /// order to serve the right chart for each stage.
    /// </summary>
    public int CourseStage { get; set; }

    /// <summary>
    /// Score accumulated across the stages played so far, posted to the course ranking
    /// board once the final stage is cleared.
    /// </summary>
    public uint CourseScore { get; set; }

    /// <summary>
    /// Running totals across the course's stages. The client keeps only a three-slot
    /// rolling buffer of stage records and its total-result panel reads one of them, so
    /// these have to be accumulated here and sent as the final stage's figures.
    /// </summary>
    public uint CourseNotesHit { get; set; }
    public uint CourseBreaks { get; set; }
    public uint CourseMaxCombo { get; set; }
    public uint CourseBonusScore { get; set; }
    public double CourseAccuracySum { get; set; }
    public int CourseStagesPlayed { get; set; }

    /// <summary>Restarts course progress, e.g. when a different course is selected.</summary>
    public void ResetCourseProgress()
    {
        CourseStage = 0;
        CourseScore = 0;
        CourseNotesHit = 0;
        CourseBreaks = 0;
        CourseMaxCombo = 0;
        CourseBonusScore = 0;
        CourseAccuracySum = 0;
        CourseStagesPlayed = 0;
    }

    public void InitCrypto(DjMaxCrypto crypto)
    {
        _packetFactory.InitCrypto(crypto);
    }

    public void UpdateIdentity()
    {
        // call when user info set/changed
        //string newIdentity = $"[GameClient@{Socket.Identity}]";
        //if (Account != null)
        //{
        //    newIdentity += $"[Acc:({Account.Id}){Account.NormalName}]";
        //}

        //if (Character != null)
        //{
        //    newIdentity += $"[Cha:({Character.CharacterId}){Character.FirstName} {Character.LastName}]";
        //}

        //Identity = newIdentity;
    }

    public void Close()
    {
        _socket.Close();
    }

    /// <summary>
    /// Sends a final packet and then closes, giving the write time to actually leave.
    ///
    /// <see cref="Send"/> queues into the socket's async send path; closing in the next
    /// statement discards anything still queued, so a "you have been disconnected"
    /// notice sent that way never arrives and the player just sees the connection drop.
    /// The delay is what makes the difference between the client showing its dialog and
    /// showing nothing.
    ///
    /// The close still happens even if the client ignores the packet, so this cannot be
    /// used to stay connected.
    ///
    /// The grace is deliberately SHORT. The client stores a disconnect reason in a field
    /// that every subsequent packet handler zeroes, so the longer this window is, the
    /// more chance a keepalive or room broadcast arrives and wipes the reason before the
    /// client can draw its dialog. 5 bytes needs no time at all to leave.
    /// </summary>
    /// <summary>
    /// Ends the connection ABRUPTLY (RST), the way the process dying does.
    ///
    /// A graceful close (FIN) leaves this retail client sitting on a stale scene - the
    /// same reason GracefulShutdownCoordinator refuses to call Close and lets the process
    /// terminate instead. For a single banned player we cannot end the process, so we
    /// reproduce the same wire-level ending with a zero linger, which makes the close
    /// send RST and the client actually tear its session down and run its disconnect
    /// handler (sub_44D012), which is what draws the ban dialog.
    ///
    /// The socket is behind ITcpSocket and the property that exposes the real socket is
    /// not part of that interface, so it is found reflectively. Which path was taken is
    /// logged: a graceful fallback is the difference between the dialog appearing and
    /// the client hanging, so it must not fail silently.
    /// </summary>
    public void Kill()
    {
        // Nothing may be answered from here on; see Kicked.
        Kicked = true;
        try
        {
            System.Net.Sockets.Socket? socket = FindSocket(_socket);
            if (socket == null)
            {
                Logger.Error(
                    $"{Identity}: no underlying Socket found on " +
                    $"{_socket.GetType().Name}; closing gracefully, so a disconnect " +
                    "notice may not be shown.");
            }
            else
            {
                socket.LingerState = new System.Net.Sockets.LingerOption(true, 0);
                Logger.Info($"{Identity}: abortive close armed (RST).");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"{Identity}: could not arm an abortive close ({ex.Message}).");
        }
        Close();
    }

    /// <summary>The real socket behind an ITcpSocket, whatever the property is called.</summary>
    private static System.Net.Sockets.Socket? FindSocket(object candidate)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance;
        for (int depth = 0; depth < 3 && candidate != null; depth++)
        {
            Type type = candidate.GetType();
            foreach (System.Reflection.PropertyInfo property in type.GetProperties(Flags))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }
                object? value = property.GetValue(candidate);
                if (value is System.Net.Sockets.Socket found)
                {
                    return found;
                }
            }
            foreach (System.Reflection.FieldInfo field in type.GetFields(Flags))
            {
                if (field.GetValue(candidate) is System.Net.Sockets.Socket found)
                {
                    return found;
                }
            }
            // Some wrappers hold another wrapper; go one level in.
            candidate = type.GetFields(Flags)
                .Select(field => field.GetValue(candidate))
                .FirstOrDefault(value => value != null && value.GetType().Name.Contains("Socket"))!;
        }
        return null;
    }

    public void SendThenClose(Packet packet, int graceMilliseconds = 150)
    {
        // Marked BEFORE the send, so a packet already in flight from the client cannot be
        // answered in the window between this going out and the socket closing.
        Kicked = true;
        Send(packet);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(graceMilliseconds);
            }
            finally
            {
                Kill();
            }
        });
    }

    public List<Packet> Receive(byte[] data)
    {
        // Any traffic at all proves the connection is alive, so stamp it here rather than
        // in one handler - the keepalive ack is only a fallback for an idle client.
        LastSeenUtc = DateTime.UtcNow;
        Logger.Info(
            $"RAW RECV {data.Length} bytes from {Identity}: {Convert.ToHexString(data)}");
        List<Packet> packets;
        try
        {
            _packetFactory.FillReadBuffer(data);
            packets = _packetFactory.ReadPackets();
        }
        catch (Exception ex)
        {
            Logger.Exception(this, ex);
            packets = new List<Packet>();
        }

        foreach (Packet packet in packets)
        {
            Logger.LogPacket(this, packet);
        }

        return packets;
    }

    public void Send(Packet packet)
    {
        lock (_sendLock)
        {
            byte[] data;
            try
            {
                data = _packetFactory.Write(packet);
            }
            catch (Exception ex)
            {
                // Write encrypts BEFORE it assembles, so a throw here may already have
                // advanced the send cipher for bytes that will never be transmitted. The
                // client's decrypt state is then permanently one packet ahead and every
                // later packet decodes to garbage, which looks like the UI losing all its
                // text rather than like a dropped packet. Say so plainly.
                Logger.Error(
                    $"{Identity}: {packet.Meta.Id} was not sent; if the send cipher " +
                    "already advanced, this connection's stream is now desynchronised.");
                Logger.Exception(this, ex);
                return;
            }

            SendRaw(data);
        }

        Logger.LogPacket(this, packet);
    }

    /// <summary>
    /// Sends raw bytes to the client, without any further processing
    /// </summary>
    public void SendRaw(byte[] data)
    {
        _socket.Send(data);
    }
}
