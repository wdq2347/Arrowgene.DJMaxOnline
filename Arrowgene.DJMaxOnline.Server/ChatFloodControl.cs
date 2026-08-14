using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Server;

/// <summary>
/// Per-player chat rate limiting, driving the client's own flood-control messages.
///
/// The client already has the whole vocabulary and renders it from a packet: sub_4327E0
/// receives OnChatControlInf and the lobby scene sub_442787 shows
///   CHATMSG1 "Please don't spam!"                       (Warn)
///   CHATMSG2 "You cannot use the chat window for a while" (Disable)
///   CHATMSG3 "You can use the chat window again now"      (Enable)
/// Nothing was ever sending those states, so flooding had no consequence at all.
///
/// A sliding window rather than a fixed one: a fixed window lets a player send a full
/// burst at the end of one window and another at the start of the next, which is exactly
/// the pattern flood control is meant to stop.
/// </summary>
public sealed class ChatFloodControl
{
    /// <summary>Messages allowed in <see cref="Window"/> before the first warning.</summary>
    public const int WarnThreshold = 5;

    /// <summary>Messages in <see cref="Window"/> that mute the player.</summary>
    public const int MuteThreshold = 8;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>How long a mute lasts. CHATMSG2 does not name a duration.</summary>
    public static readonly TimeSpan MuteDuration = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private DateTimeOffset? _mutedUntil;
    private bool _warned;

    /// <summary>
    /// Lifts a mute that has run its course, returning Enable exactly once.
    ///
    /// This exists because <see cref="Observe"/> only runs when the player sends a message,
    /// so on its own the mute appears to last until they next try to talk - and the whole
    /// point of the mute is that they have stopped trying. A caller schedules this for
    /// <see cref="MuteDuration"/> after muting so the chat box reopens on time.
    ///
    /// Returns null if they are not muted, or the mute has not expired, or it was already
    /// lifted - so racing this against Observe cannot produce two Enables.
    /// </summary>
    public ChatControlState? Expire(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_mutedUntil is not { } until || now < until)
            {
                return null;
            }
            Reset();
            return ChatControlState.Enable;
        }
    }

    /// <summary>What to tell the client, or null when nothing needs saying.</summary>
    public ChatControlState? Observe(DateTimeOffset now, out bool allowed)
    {
        lock (_lock)
        {
            return ObserveLocked(now, out allowed);
        }
    }

    private ChatControlState? ObserveLocked(DateTimeOffset now, out bool allowed)
    {
        if (_mutedUntil is { } until)
        {
            if (now < until)
            {
                allowed = false;
                return null; // Already told them; do not repeat it every message.
            }

            // The mute expired. Enable is the ONLY thing that re-opens the chat box -
            // the client never re-enables itself - so this must be sent or the player
            // stays muted for the rest of the session. Normally the scheduled Expire has
            // already done this; this covers the case where it has not run yet.
            Reset();
            allowed = true;
            return ChatControlState.Enable;
        }

        while (_recent.Count > 0 && now - _recent.Peek() > Window)
        {
            _recent.Dequeue();
        }
        _recent.Enqueue(now);

        if (_recent.Count >= MuteThreshold)
        {
            _mutedUntil = now + MuteDuration;
            allowed = false;
            return ChatControlState.Disable;
        }

        allowed = true;
        if (_recent.Count >= WarnThreshold && !_warned)
        {
            _warned = true;
            return ChatControlState.Warn;
        }
        return null;
    }

    /// <summary>Lifts a mute early, for a GM clearing it by hand.</summary>
    public ChatControlState Clear()
    {
        lock (_lock)
        {
            Reset();
            return ChatControlState.Enable;
        }
    }

    private void Reset()
    {
        _mutedUntil = null;
        _warned = false;
        _recent.Clear();
    }
}
