using Arrowgene.Logging;
using Arrowgene.Networking.Tcp;
using Arrowgene.Networking.Tcp.Consumer.BlockingQueueConsumption;

namespace Arrowgene.DJMaxOnline.Server.China260;

public class Consumer : ThreadedBlockingQueueConsumer
{
    private static readonly ServerLogger Logger = LogProvider.Logger<ServerLogger>(typeof(Consumer));

    private readonly Dictionary<PacketId, IPacketHandler> _packetHandlerLookup;
    private readonly Dictionary<ITcpSocket, Client> _clients;
    private readonly object _lock;
    private readonly Setting _setting;
    private bool _acceptingConnections = true;
    public event Action<Client>? ClientDisconnected;
    public event Action<Client>? ClientConnected;

    public Consumer(Setting setting)
        : base(setting.AsyncEventSettings, setting.Name)
    {
        _setting = setting;
        _lock = new object();
        _clients = new Dictionary<ITcpSocket, Client>();
        _packetHandlerLookup = new Dictionary<PacketId, IPacketHandler>();
    }

    public void Clear()
    {
        _packetHandlerLookup.Clear();
    }

    public void AddHandler(IPacketHandler packetHandler)
    {
        if (!_packetHandlerLookup.TryAdd(packetHandler.Id, packetHandler))
        {
            Logger.Error($"PacketHandlerId: {packetHandler.Id} already exists");
        }
    }

    /// <summary>
    /// Closes every connection accepted after shutdown draining begins. The listening
    /// socket is stopped as well, but this guard closes the small accept/stop race.
    /// </summary>
    public void RefuseNewConnections()
    {
        lock (_lock)
        {
            _acceptingConnections = false;
        }
    }

    protected override void HandleReceived(ITcpSocket socket, byte[] data)
    {
        if (!socket.IsAlive)
        {
            return;
        }

        Client? client;
        lock (_lock)
        {
            if (!_clients.TryGetValue(socket, out client) || client == null)
            {
                Logger.Error(socket, "Client does not exist in lookup");
                return;
            }
        }

        List<Packet> packets = client.Receive(data);
        foreach (Packet packet in packets)
        {
            HandlePacket(client, packet);
        }
    }

    /// <summary>
    /// The only packets a socket may send before it has an authenticated player attached.
    /// Everything else operates on somebody's account, so it is dropped until the launcher
    /// ticket has been redeemed. This is what makes it safe for the server to hold no
    /// player at all until a real login happens.
    /// </summary>
    private static readonly HashSet<PacketId> PreLoginPackets =
    [
        PacketId.ConnectReq,
        PacketId.CnConnectConfirmReq,
        PacketId.AuthenticateInSndAccReq,
        PacketId.LogInReq,
        PacketId.KeepAuthenticateInReq,
        PacketId.PingTestInf,
        PacketId.OnPingTestInf
    ];

    private void HandlePacket(Client client, Packet packet)
    {
        if (!_packetHandlerLookup.TryGetValue(packet.Id, out var packetHandler))
        {
            Logger.LogUnhandledPacket(client, packet);
            return;
        }

        // A KICKED CLIENT GETS NO FURTHER REPLIES. Not one.
        //
        // The client keeps its disconnect reason in a single field (net+895300) that every
        // one of its packet handlers zeroes on entry. It also keeps pinging after being told
        // to go, so answering even a keepalive wipes the reason before the disconnect scene
        // can read it and draw the dialog. Observed exactly that: an OnPingTestInf and an
        // OnInviteRejectAck went out in the two seconds after a ban and blanked the message.
        //
        // Dropping their packets here means nothing can be sent in reply, whatever the
        // handler would have done.
        if (client.Kicked)
        {
            return;
        }

        if (client.PlayerStore == null && !PreLoginPackets.Contains(packet.Id))
        {
            Logger.Error(client,
                $"Dropped {packet.Id} from a socket with no authenticated player.");
            return;
        }

        try
        {
            packetHandler.Handle(client, packet);
        }
        catch (Exception ex)
        {
            Logger.Exception(client, ex);
            Logger.LogPacketError(client, packet);
        }
    }

    protected override void HandleDisconnected(ITcpSocket socket)
    {
        Client client;
        lock (_lock)
        {
            if (!_clients.ContainsKey(socket))
            {
                Logger.Error(socket, $"Disconnected client does not exist in lookup");
                return;
            }

            client = _clients[socket];
            _clients.Remove(socket);
        }

        Action<Client>? onClientDisconnected = ClientDisconnected;
        if (onClientDisconnected != null)
        {
            try
            {
                onClientDisconnected.Invoke(client);
            }
            catch (Exception ex)
            {
                Logger.Exception(client, ex);
            }
        }

        Logger.Info($"Disconnected: {client.Identity}");
    }

    protected override void HandleConnected(ITcpSocket socket)
    {
        Client client = new Client(socket, new PacketFactory());
        bool accepted;
        lock (_lock)
        {
            _clients.Add(socket, client);
            accepted = _acceptingConnections;
        }

        if (!accepted)
        {
            Logger.Info($"Refused connection during shutdown drain: {client.Identity}");
            client.Close();
            return;
        }

        Logger.Info($"Connected: {client.Identity}");

        Action<Client>? onClientConnected = ClientConnected;
        if (onClientConnected != null)
        {
            try
            {
                onClientConnected.Invoke(client);
            }
            catch (Exception ex)
            {
                Logger.Exception(client, ex);
            }
        }
    }
}
