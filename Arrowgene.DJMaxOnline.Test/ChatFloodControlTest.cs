using System;
using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

public sealed class ChatFloodControlTest
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void NormalChatIsNeverInterrupted()
    {
        ChatFloodControl flood = new();
        for (int i = 0; i < 20; i++)
        {
            // One message every two seconds: brisk conversation, not flooding.
            ChatControlState? state = flood.Observe(T0.AddSeconds(i * 2), out bool allowed);
            Assert.That(allowed, Is.True);
            Assert.That(state, Is.Null, $"message {i} should not have been controlled");
        }
    }

    [Test]
    public void ABurstWarnsThenMutesThenReEnables()
    {
        ChatFloodControl flood = new();
        ChatControlState? warned = null;
        ChatControlState? muted = null;
        for (int i = 0; i < ChatFloodControl.MuteThreshold; i++)
        {
            ChatControlState? state = flood.Observe(T0.AddMilliseconds(i * 100), out _);
            if (state == ChatControlState.Warn) warned = state;
            if (state == ChatControlState.Disable) muted = state;
        }
        Assert.Multiple(() =>
        {
            Assert.That(warned, Is.EqualTo(ChatControlState.Warn), "no warning was sent");
            Assert.That(muted, Is.EqualTo(ChatControlState.Disable), "never muted");
        });

        // While muted the message is dropped and nothing is re-sent.
        Assert.That(flood.Observe(T0.AddSeconds(1), out bool allowed), Is.Null);
        Assert.That(allowed, Is.False);

        // Enable is the ONLY thing that reopens the client's chat box, so the mute
        // expiring must produce it or the player stays muted for the whole session.
        ChatControlState? reopened = flood.Observe(
            T0 + ChatFloodControl.MuteDuration + TimeSpan.FromSeconds(1), out bool after);
        Assert.That(reopened, Is.EqualTo(ChatControlState.Enable));
        Assert.That(after, Is.True);
    }

    /// <summary>
    /// The mute has to lift on its own. Observe only runs when the player sends a message,
    /// so without a scheduled expiry the chat box stays shut until they try to talk again -
    /// and a muted player has no reason to think that would help.
    /// </summary>
    [Test]
    public void AMuteExpiresWithoutThePlayerSendingAnything()
    {
        ChatFloodControl flood = Muted();

        Assert.Multiple(() =>
        {
            Assert.That(
                flood.Expire(T0 + ChatFloodControl.MuteDuration - TimeSpan.FromSeconds(1)),
                Is.Null,
                "the mute must not lift early");
            Assert.That(
                flood.Expire(T0 + ChatFloodControl.MuteDuration + TimeSpan.FromSeconds(1)),
                Is.EqualTo(ChatControlState.Enable));
        });
    }

    /// <summary>
    /// Enable must arrive exactly once. It is what reopens the chat box, and the scheduled
    /// expiry can race the player sending a message at the same moment.
    /// </summary>
    [Test]
    public void ExpiryAndAMessageCannotBothReopenTheChatBox()
    {
        ChatFloodControl flood = Muted();
        DateTimeOffset after = T0 + ChatFloodControl.MuteDuration + TimeSpan.FromSeconds(1);

        Assert.That(flood.Expire(after), Is.EqualTo(ChatControlState.Enable));

        Assert.Multiple(() =>
        {
            Assert.That(flood.Expire(after), Is.Null, "a second expiry must say nothing");
            // The player talks right after: allowed, but not told to enable a second time.
            Assert.That(flood.Observe(after, out bool allowed), Is.Null);
            Assert.That(allowed, Is.True);
        });
    }

    /// <summary>Expire says nothing about a player who was never muted.</summary>
    [Test]
    public void ExpireIsSilentWhenThereIsNoMute()
    {
        ChatFloodControl flood = new();
        flood.Observe(T0, out _);
        Assert.That(flood.Expire(T0 + TimeSpan.FromMinutes(1)), Is.Null);
    }

    private static ChatFloodControl Muted()
    {
        ChatFloodControl flood = new();
        ChatControlState? state = null;
        for (int i = 0; i < ChatFloodControl.MuteThreshold; i++)
        {
            state = flood.Observe(T0, out _) ?? state;
        }
        Assert.That(state, Is.EqualTo(ChatControlState.Disable), "setup: expected a mute");
        return flood;
    }

    [Test]
    public void TheWindowSlidesSoASpacedBurstIsNotPunished()
    {
        ChatFloodControl flood = new();
        for (int i = 0; i < 30; i++)
        {
            // Just outside the window each time: never accumulates.
            ChatControlState? state = flood.Observe(
                T0 + (ChatFloodControl.Window + TimeSpan.FromSeconds(1)) * i, out bool allowed);
            Assert.That(allowed, Is.True);
            Assert.That(state, Is.Null);
        }
    }
}
