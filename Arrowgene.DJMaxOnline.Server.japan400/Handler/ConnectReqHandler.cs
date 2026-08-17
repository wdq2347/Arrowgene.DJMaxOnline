using Arrowgene.DJMaxOnline.Server.Japan400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Handler;

public class ConnectReqHandler : IPacketHandler
{
    private readonly Func<IReadOnlyList<ChannelInfo>> _channelSnapshot;
    private readonly LocalAccountResolver _accounts;

    public ConnectReqHandler(
        Func<IReadOnlyList<ChannelInfo>> channelSnapshot,
        LocalAccountResolver accounts)
    {
        _channelSnapshot = channelSnapshot ??
            throw new ArgumentNullException(nameof(channelSnapshot));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }



    private const ushort FirstAssignedUserId = 0x014D;
    private static readonly object AssignedUserIdLock = new();
    private static ushort _nextAssignedUserId = FirstAssignedUserId;

    private static readonly ServerLogger Logger = LogProvider.Logger<ServerLogger>(typeof(ConnectReqHandler));

    /// <summary>
    /// How long to wait before answering the connect request.
    ///
    /// The client sends id 0x0A from its NETWORK layer the instant the socket opens, but
    /// its front-end scene registers the packet sink that reacts to the reply a few seconds
    /// later. Answer immediately and every packet lands before anything is listening: the
    /// handshake completes, keepalives work, and the screen stays black. Answering late
    /// showed the client's first dialog, which is how this delay was found.
    /// </summary>
    private const int ConnectResponseDelayMilliseconds = 250;

    public void Handle(Client client, Packet packet)
    {
        // The whole reply is delayed, not just part of it, so the seed still goes out
        // before the cipher starts. Delaying only OnConnectAck once put the seed on the
        // wire after the cipher was already running, which the client cannot read.
        _ = Task.Run(async () =>
        {
            await Task.Delay(ConnectResponseDelayMilliseconds);
            Respond(client);
        });
    }

    private void Respond(Client client)
    {
        // ANSWER THE CONNECT REQUEST, IN THIS ORDER.
        //
        // 1. OnConnectAck goes out IN CLEAR - it carries the cipher seed, so the client
        //    cannot possibly read it enciphered.
        // 2. Only then is the cipher switched on, keyed on that same seed, so everything
        //    after it is enciphered and both sides stay in step.
        //
        // Getting this backwards is silent: the handshake still "completes" because the
        // client answers from its network layer, but every later packet is unreadable.
        if (client.PendingConnectSeed is { Length: OnConnectAckPacket.SeedSize } seed32)
        {
            client.Send(OnConnectAckPacket.Build(seed32, client.AssignedUserId));
            client.PendingConnectSeed = null;
            client.CipherSeed = seed32[..DjMaxCrypto.JapaneseSeedSize];

            // ON, and it must be on from the very next packet.
            //
            // The client's OnConnectAck handler (sub_4312C0) ends by keying two XTEA
            // contexts off this same seed and setting net+793748 = 1:
            //     sub_44E404(v3, HIDWORD(v3), this + 793712, 8);   // 8 WORDS = 32 bytes
            //     sub_44E404(v4, HIDWORD(v4), this + 793712, 8);
            //     *(_DWORD *)(this + 793748) = 1;
            // and net+793748 is 0xC1C94 - the flag the receive path gates on:
            //     4302b0  cmp dword ptr [edx+0C1C94h], 0
            //     4302b7  jz  short loc_4302CE      ; clear -> no decrypt
            //     4302c9  call sub_4394B0           ; set   -> decrypt from +7
            // sub_4394B0 has no other condition: every packet over 7 bytes is deciphered.
            //
            // The seed is keyed in its stored form. We invert the first two dwords on the
            // wire and the handler inverts them straight back, so the client keys on the
            // ORIGINAL bytes - seed32 as it is here, not as it went out.
            client.InitCrypto(DjMaxCrypto.InitJapanese(seed32));
            Logger.Info(client,
                $"Sent OnConnectAck (result 4, assigned id {client.AssignedUserId}); " +
                "cipher on from here.");
        }

        // The client's own debug trace, read correctly, is what proves the above.
        //
        // Patching its nullsub_5 stub to the built-in CSV logger produced:
        //
        //   DJMaxNet::OnConnectAck          <- parsed (we send that one in clear)
        //   DJMaxNet::OnAuthenticateInAck   <- dispatched, but its body read as GARBAGE
        //   DJMaxNet::OnGameStartInf
        //   [RECV] ... [V] pairs            <- channel list and peer counts arrive
        //   DJMaxApp::RunDisconnection      <- exactly 30s later: the LOGINMSG2 timeout
        //
        // Garbage bodies with correct dispatch is the signature of sending plaintext to a
        // client that is deciphering: packet IDS sit outside the enciphered region, so
        // every packet still routed to the right handler while every BODY was noise.
        //
        // That is why sub_4317F0 never saw result == 20 at +15 and never set net+793804.
        // The front-end state machine sub_44A674 case 4 needs BOTH that flag and
        // net+895176, and OnGameStartInf sets the latter unconditionally - so exactly one
        // of the two came up, and the 30-second timer ran out every time.
        //
        // ANSWER IT. This packet is the client's DJMaxNet::AuthenticateInReq (sub_431460
        // sends it the moment OnConnectAck lands), and it waits for OnAuthenticateInAck
        // before doing anything else - which is why the connection went quiet here.
        //
        // The request carries no account, so the identity has to come from the launcher.
        //
        // A placeholder here does NOT work, even though it gets the channel list drawn.
        // Whatever user id goes out in this ack is the one the client quotes back in
        // LogInReq on the channel socket, and that id is looked up against the launcher
        // session. A made-up id can never match: "no active launcher session exists for
        // userId 333" (0x14D being FirstAssignedUserId, not an account).
        //
        // The ticket normally arrives in packet 0x0E, but "ConnectFromNM" = 0 means the
        // client never sends it, so claim the one pending launcher login directly. The
        // selected channel may open a second socket after that ticket has been consumed,
        // in which case it joins the sole live launcher session instead. Both paths answer
        // with the real account, exactly as JpConnectConfirmReqHandler does on NetMarble.
        if (client.PlayerStore == null &&
            _accounts.TryResolveSoleLauncherSession(
                out LocalPlayerStore? store, out LoginSessionLease? lease) &&
            store != null && lease != null)
        {
            client.PlayerStore = store;
            client.LoginSessionLease = lease;
            client.UserId = store.Profile.UserId;
        }

        if (client.PlayerStore is { } bound)
        {
            LocalPlayerProfile profile = bound.Profile;
            client.Send(OnAuthenticateInAckPacket.Build(new AuthenticationIdentity(
                UserId: profile.UserId,
                AccountClass: profile.AccountClass,
                Level: profile.Progress.Level,
                AccountId: profile.AccountId,
                SecondaryId: profile.SecondaryId,
                Nickname: profile.Nickname)));
            Logger.Info(client,
                $"Sent OnAuthenticateInAck (result 20) for launcher account " +
                $"{profile.Nickname} (userId {profile.UserId}).");
        }
        else
        {
            // No launcher login is pending. The channel list still draws, but the LogInReq
            // that follows WILL be rejected - so say why here rather than leaving it to
            // look like a packet fault later.
            Logger.Error(client,
                "No unambiguous launcher ticket or session to bind this connection to; sending a " +
                "placeholder identity. The channel list will draw but LogInReq will be " +
                "rejected. Log in through the launcher first.");
            client.Send(OnAuthenticateInAckPacket.Build(new AuthenticationIdentity(
                UserId: client.AssignedUserId,
                AccountClass: 0,
                Level: 1,
                AccountId: "player",
                SecondaryId: string.Empty,
                Nickname: "player")));
        }

        // The ack alone leaves the client on a black screen: it acknowledges keepalives but
        // draws nothing, because the channel list is what it is waiting to render. These
        // two follow the ack in every other authenticated path here (see
        // AuthenticateInSndAccReqHandler and JpConnectConfirmReqHandler), and the client's
        // OnChannelInfoInf handler sub_4321A0 takes its channel count from the packet size,
        // so an empty list is a blank screen rather than an error.
        client.Send(OnGameStartInfPacket.Build(LocalLobbyBootstrap.GameStartParameters));
        IReadOnlyList<ChannelInfo> channels = _channelSnapshot();
        client.Send(OnChannelInfoInfPacket.Build(channels));
        // Per-channel counts. The record maps onto the same stored entry the channel list
        // fills (sub_433F50 writes v10[39]/v10[40], which are its UserCount/Capacity), and
        // the key is the channel id split the way the client reads it: word 1 then word 2.
        //
        // State MUST be non-zero. It lands in the very field sub_433DF0 gates on, so a zero
        // here does not mean "no players" - it DELETES the channel that was just added.
        client.Send(OnPeerCountInfPacket.Build([.. channels.Select(channel =>
            new PeerCountEntry(
                KeyHigh: (ushort)(channel.ChannelId & 0xFFFF),
                KeyLow: (ushort)(channel.ChannelId >> 16),
                Value: channel.UserCount,
                State: channel.Capacity,
                Flags: channel.EntryVersion))]));
        Logger.Info(
            $"Sent OnGameStartInf, OnChannelInfoInf and OnPeerCountInf " +
            $"({channels.Count} channel(s)).");

        // NO RE-SENDS. The channel list goes out exactly once, here.
        //
        // There used to be a retry burst at [1500, 2000, 3000] ms, added when the screen
        // was black and the first list could land before the front-end had built any UI to
        // draw it into. That was the CIPHER bug (see the InitCrypto call above); the
        // front-end now leaves its auth state normally and the single send lands fine.
        //
        // Re-sending crashed the client twice. sub_44AC0D builds a widget per entry
        // directly into the server-select UI, so a list that arrives after the player has
        // moved on writes into a screen that no longer exists.
        //
        // Gating on "this connection has logged in" is NOT sufficient, which is how the
        // second crash happened: the client opens a SECOND socket for the chosen channel
        // and sends LogInReq on that one, while the server-select socket stays open with
        // its own retry task. The flag was set on 56793 while the retries belonged to
        // 56790 and kept firing into the same process. Any re-send needs a per-CLIENT
        // signal, not a per-connection one - and there is no reason to need one at all.
    }

    public PacketId Id => PacketId.ConnectReq;

    public static ushort AllocateAssignedUserId()
    {
        lock (AssignedUserIdLock)
        {
            ushort assignedUserId = _nextAssignedUserId;
            _nextAssignedUserId = assignedUserId == ushort.MaxValue
                ? FirstAssignedUserId
                : checked((ushort)(assignedUserId + 1));
            return assignedUserId;
        }
    }
}
