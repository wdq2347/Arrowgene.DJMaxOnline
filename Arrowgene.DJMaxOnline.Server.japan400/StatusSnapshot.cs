namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// What the local status API is allowed to publish.
///
/// These types are the privacy boundary: they deliberately carry no account id, no
/// secondary id, no password material, no session token and no network endpoint. A
/// player is identified only by the nickname they already show to everyone in the lobby.
/// Anything added here is visible to whatever reads the API, so add nothing that is not
/// already public inside the game.
/// </summary>
public sealed record RoomStatus(
    int Index,
    string Title,
    int Occupants,
    int Capacity,
    int OpenSlots,
    byte KeyMode,
    byte MatchMode,
    byte GameType,
    byte LevelRestriction,
    bool Locked,
    bool Playing,
    uint? SongId);

/// <summary>One connected player. Nickname only - never the account they log in with.</summary>
public sealed record PlayerStatus(
    string Nickname,
    int Level,
    int? RoomIndex,
    bool Playing,
    uint? SongId,
    byte KeyMode);

public sealed record ChannelStatus(
    string Name,
    byte KeyMode,
    int Players,
    int Playing,
    int Rooms,
    IReadOnlyList<RoomStatus> RoomList,
    IReadOnlyList<PlayerStatus> PlayerList);

public sealed record ServerStatus(
    bool Online,
    string StartedUtc,
    int Players,
    int Playing,
    int Rooms,
    IReadOnlyList<ChannelStatus> Channels);

/// <summary>
/// One finished run, as published by the status API.
///
/// Same privacy rule as the rest of this file: the player is identified by NICKNAME and
/// nothing else. There is no account id, no session token and no client flags here, and
/// the API cannot serialise anything that is not on this record.
/// </summary>
/// <summary>
/// One player's line in a finished match. Nickname only, same rule as the rest of the file.
/// </summary>
/// <param name="Placement">
/// Zero-based finishing position as the server assigned it: 0 is first. In a TEAM battle
/// every member of the winning side shares 0 and everyone else shares 1, so this is a side
/// verdict there rather than a ladder - read <see cref="MatchStatus.TeamBattle"/> before
/// rendering it as "2nd place". <see cref="Unranked"/> when the room had no placement at
/// all (a plain freestyle room, where nobody is competing).
/// </param>
/// <param name="Winner">Placement 0 in a room that actually ranked its players.</param>
public sealed record MatchPlayerStatus(
    string Nickname,
    byte Slot,
    byte Team,
    int Placement,
    bool Winner,
    bool Failed,
    uint Score,
    double Accuracy,
    uint MaxCombo,
    uint Breaks,
    bool FullCombo,
    bool IsBot)
{
    /// <summary>No placement was assigned - the room was not a competitive one.</summary>
    public const int Unranked = -1;
}

/// <summary>
/// One finished multiplayer match: who played, and who won.
///
/// This is the verdict <see cref="LocalLobby.FinishPlay"/> reached, captured as it was
/// reached - see <see cref="MatchHistory"/> for why it is not read back out of the score
/// table. Song and course are published as CATALOG ids (DiscStock and CourseSection both
/// number from 1) to match the score endpoints; the server's own state holds the 0-based
/// indexes.
/// </summary>
public sealed record MatchStatus(
    long MatchId,
    string FinishedUtc,
    string Channel,
    int RoomIndex,
    string RoomTitle,
    byte KeyMode,
    byte MatchMode,
    byte GameType,
    bool TeamBattle,
    bool Ranked,
    uint? SongId,
    ushort? CourseId,
    IReadOnlyList<MatchPlayerStatus> Players);

public sealed record ScoreStatus(
    long ScoreId,
    string Nickname,
    string PlayedUtc,
    uint? SongId,
    ushort? CourseId,
    int? CourseStage,
    /// <summary>
    /// The room the run was played in. A ranked match is three songs written as three
    /// rows, and this is what groups them back into one result. Not account data: it is
    /// the same room index already published in the room list.
    /// </summary>
    ushort? RoomId,
    byte KeyMode,
    byte Difficulty,
    byte MatchMode,
    ushort TotalNotes,
    uint MaxCombo,
    uint NotesHit,
    uint Breaks,
    uint Score,
    double Accuracy,
    byte Rank,
    bool Failed,
    bool FullCombo,
    uint MoneyAwarded,
    uint ExperienceAwarded);
