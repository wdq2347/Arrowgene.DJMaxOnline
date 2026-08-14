using System.Net;
using System.Text.Json;
using Arrowgene.DJMaxOnline.Server;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The status API is the one place server state is published outside the game, so these
/// pin down both that it answers and - more importantly - that it cannot leak an account.
/// </summary>
[TestFixture]
public class LocalStatusApiTest
{
    private static int NextPort() => Random.Shared.Next(47000, 48500);

    private static ChannelStatus SampleChannel() => new(
        Name: "SEOUL",
        KeyMode: 5,
        Players: 2,
        Playing: 1,
        Rooms: 1,
        RoomList:
        [
            new RoomStatus(
                Index: 3,
                Title: "come play",
                Occupants: 2,
                Capacity: 6,
                OpenSlots: 4,
                KeyMode: 5,
                MatchMode: 2,
                GameType: 0,
                LevelRestriction: 0,
                Locked: false,
                Playing: true,
                SongId: 42)
        ],
        PlayerList:
        [
            new PlayerStatus("Blade", 16, 3, true, 42, 5),
            new PlayerStatus("Guest2", 1, null, false, null, 5)
        ]);

    private static Setting Configure(int port, string token = "") => new()
    {
        StatusApiEnabled = true,
        StatusApiListenIpAddress = IPAddress.Loopback,
        StatusApiPort = (ushort)port,
        StatusApiToken = token
    };

    private static async Task<(HttpStatusCode Code, string Body)> GetAsync(
        int port, string path, string? token = null)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        using HttpRequestMessage request = new(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        if (token != null)
        {
            request.Headers.Add("X-Status-Token", token);
        }
        using HttpResponseMessage response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task StatusReportsChannelsRoomsAndPlayers()
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port), () => [SampleChannel()]);
        api.Start();

        (HttpStatusCode code, string body) = await GetAsync(port, "/status");

        Assert.That(code, Is.EqualTo(HttpStatusCode.OK));
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.That(root.GetProperty("online").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("players").GetInt32(), Is.EqualTo(2));
        Assert.That(root.GetProperty("playing").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("rooms").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("channels")[0].GetProperty("name").GetString(),
            Is.EqualTo("SEOUL"));
    }

    [Test]
    public async Task RoomsEndpointFlattensAcrossChannels()
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port), () => [SampleChannel()]);
        api.Start();

        (_, string body) = await GetAsync(port, "/rooms");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement rooms = document.RootElement.GetProperty("rooms");
        Assert.That(rooms.GetArrayLength(), Is.EqualTo(1));
        Assert.That(rooms[0].GetProperty("channel").GetString(), Is.EqualTo("SEOUL"));
        Assert.That(rooms[0].GetProperty("room").GetProperty("title").GetString(),
            Is.EqualTo("come play"));
    }

    /// <summary>
    /// The whole point of the DTO layer: an account id must be unreachable through the
    /// API even by accident. This checks the serialised bytes, not the type.
    /// </summary>
    [Test]
    public async Task NoEndpointLeaksAccountOrCredentialFields()
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port), () => [SampleChannel()]);
        api.Start();

        string[] forbidden =
        [
            "accountid", "account_id", "secondaryid", "password", "hash", "salt",
            "token", "ticket", "sessiontoken", "endpoint", "ipaddress", "userid"
        ];

        foreach (string path in new[] { "/status", "/rooms", "/players", "/health" })
        {
            (_, string body) = await GetAsync(port, path);
            string lowered = body.ToLowerInvariant();
            foreach (string needle in forbidden)
            {
                Assert.That(lowered, Does.Not.Contain(needle),
                    $"{path} exposed '{needle}'");
            }
        }
    }

    [Test]
    public async Task PlayersEndpointPublishesNicknamesOnly()
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port), () => [SampleChannel()]);
        api.Start();

        (_, string body) = await GetAsync(port, "/players");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement first = document.RootElement.GetProperty("players")[0]
            .GetProperty("player");
        Assert.That(first.GetProperty("nickname").GetString(), Is.EqualTo("Blade"));
        Assert.That(first.TryGetProperty("accountId", out _), Is.False);
    }

    [Test]
    public async Task TokenIsRequiredWhenConfigured()
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port, "s3cret"), () => [SampleChannel()]);
        api.Start();

        Assert.That((await GetAsync(port, "/status")).Code,
            Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await GetAsync(port, "/status", "wrong")).Code,
            Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await GetAsync(port, "/status", "s3cret")).Code,
            Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>
    /// The score endpoints exist for the Discord feed. They must answer even with no
    /// database attached, and must never grow an account field.
    /// </summary>
    [TestCase("/scores/recent")]
    [TestCase("/scores/player?nickname=Blade")]
    [TestCase("/scores/song?song=1")]
    public async Task ScoreEndpointsAnswerWithoutADatabase(string path)
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port), () => [SampleChannel()], scores: null);
        api.Start();

        (HttpStatusCode code, string body) = await GetAsync(port, path);

        Assert.That(code, Is.EqualTo(HttpStatusCode.OK));
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.That(document.RootElement.GetProperty("scores").GetArrayLength(), Is.Zero);
        string lowered = body.ToLowerInvariant();
        foreach (string needle in new[] { "accountid", "password", "hash", "secondaryid" })
        {
            Assert.That(lowered, Does.Not.Contain(needle));
        }
    }

    [Test]
    public async Task ThereIsNoAccountEndpoint()
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port), () => [SampleChannel()]);
        api.Start();

        // Password and account work must never be reachable over the wire.
        foreach (string path in new[]
                 { "/accounts", "/signup", "/login", "/password", "/credentials" })
        {
            Assert.That((await GetAsync(port, path)).Code,
                Is.EqualTo(HttpStatusCode.NotFound), $"{path} must not exist");
        }
    }

    [Test]
    public async Task UnknownEndpointIsNotFound()
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port), () => [SampleChannel()]);
        api.Start();

        Assert.That((await GetAsync(port, "/secrets")).Code,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public void DisabledApiOpensNoPort()
    {
        int port = NextPort();
        Setting setting = Configure(port);
        setting.StatusApiEnabled = false;

        using LocalStatusApi api = new(setting, () => [SampleChannel()]);
        api.Start();

        Assert.That(
            async () => await GetAsync(port, "/status"),
            Throws.InstanceOf<HttpRequestException>());
    }

    [Test]
    public async Task WriteMethodsAreRejected()
    {
        int port = NextPort();
        using LocalStatusApi api = new(Configure(port), () => [SampleChannel()]);
        api.Start();

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Delete })
        {
            using HttpRequestMessage request = new(
                method, $"http://127.0.0.1:{port}/status");
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        }
    }
}
