using Arrowgene.DJMaxOnline.Server.China260;
using Arrowgene.DJMaxOnline.Server.China260.Packets;

namespace Arrowgene.DJMaxOnline;

/// <summary>Interactive adapter for <see cref="ServerAdministrationService"/>.</summary>
internal sealed class LiveAdministrationConsole
{
    private readonly ServerAdministrationService _administration;
    private readonly Func<string, string> _readConfirmedPassword;
    private readonly Func<string, GracefulShutdownResult> _shutdown;

    public LiveAdministrationConsole(
        ServerAdministrationService administration,
        Func<string, string> readConfirmedPassword,
        Func<string, GracefulShutdownResult> shutdown)
    {
        _administration = administration ??
            throw new ArgumentNullException(nameof(administration));
        _readConfirmedPassword = readConfirmedPassword ??
            throw new ArgumentNullException(nameof(readConfirmedPassword));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
    }

    public bool Run()
    {
        Console.WriteLine("Live administration ready. Type /help; use /stop to shut down.");
        bool running = true;
        while (running)
        {
            string? line = InteractiveConsole.ReadCommand("djmax> ");
            if (line == null)
            {
                Console.WriteLine("Console input closed; stopping the server.");
                break;
            }

            try
            {
                running = Execute(line);
            }
            catch (OperationCanceledException exception)
            {
                Console.WriteLine(exception.Message);
            }
            catch (Exception exception)
            {
                // A bad command must never terminate the live game server.
                Console.WriteLine($"ERROR: {exception.Message}");
            }
        }

        return ProcessTerminationRequested;
    }

    internal bool ProcessTerminationRequested { get; private set; }

    internal bool Execute(string line)
    {
        string commandLine = (line ?? string.Empty).Trim();
        if (commandLine.StartsWith('/'))
        {
            commandLine = commandLine[1..].TrimStart();
        }
        if (commandLine.Length == 0)
        {
            return true;
        }

        (string command, string arguments) = SplitHead(commandLine);
        switch (command.ToLowerInvariant())
        {
            case "help":
            case "?":
                PrintHelp();
                return true;
            case "accounts":
            case "users":
                PrintAccounts();
                return true;
            case "online":
                PrintOnlinePlayers();
                return true;
            case "account":
                ExecuteAccount(arguments);
                return true;
            case "ban":
                ExecuteLock(arguments, AccountLockState.Locked);
                return true;
            case "suspend":
                ExecuteLock(arguments, AccountLockState.UnderReview);
                return true;
            case "unban":
            case "unsuspend":
                ExecuteLock(arguments, AccountLockState.None);
                return true;
            case "announce":
            case "notice":
                PrintBroadcast(_administration.BroadcastChat(
                    RequireText(arguments, "/announce <message>"),
                    ChatMessageType.Notice));
                return true;
            case "alert":
                PrintBroadcast(_administration.BroadcastChat(
                    RequireText(arguments, "/alert <message>"),
                    ChatMessageType.Alert));
                return true;
            case "say":
                PrintBroadcast(_administration.BroadcastChat(
                    RequireText(arguments, "/say <message>"),
                    ChatMessageType.System));
                return true;
            case "bignews":
                ExecuteBigNews(arguments);
                return true;
            case "stop":
            case "exit":
            case "quit":
            {
                GracefulShutdownResult result = _shutdown(arguments);
                Console.WriteLine(
                    $"Shutdown drain complete ({result.Reason}); " +
                    $"{result.DisconnectedDuringDrain} connection(s) already closed; " +
                    $"terminating {result.PendingProcessTermination} remaining connection(s) " +
                    "with the server process.");
                ProcessTerminationRequested = true;
                return false;
            }
            default:
                Console.WriteLine($"Unknown command '{command}'. Type /help.");
                return true;
        }
    }

    /// <summary>
    /// /ban, /suspend and /unban. The client shows its own dialog for each state, so
    /// there is nothing to word here - DISCONNECTMSG5 for a ban, DISCONNECTMSG6 for a
    /// suspension.
    /// </summary>
    private void ExecuteLock(string arguments, AccountLockState state)
    {
        (string selector, string reason) = SplitHead(arguments.Trim());
        if (selector.Length == 0)
        {
            throw new ArgumentException(
                "Usage: /ban | /suspend <account-id|nickname|user-id> <reason>, " +
                "/unban <account-id|nickname|user-id>");
        }
        if (state != AccountLockState.None && reason.Trim().Length == 0)
        {
            // A ban with no reason is unmanageable a month later.
            throw new ArgumentException("Give a reason: <selector> <reason>");
        }

        LocalPlayerProfile profile =
            _administration.SetAccountLock(selector, state, reason);
        Console.WriteLine(
            $"{profile.AccountId} / {profile.Nickname} (user {profile.UserId}) is now " +
            $"{AccountLockReasons.Describe(state)}" +
            (profile.LockReason.Length == 0 ? "." : $" ({profile.LockReason})."));
    }

    private void ExecuteAccount(string arguments)
    {
        (string operation, string values) = SplitHead(arguments);
        switch (operation.ToLowerInvariant())
        {
            case "list":
                PrintAccounts();
                return;
            case "create":
            {
                string[] parts = values.Split(
                    ' ', StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries);
                if (parts.Length is < 2 or > 3)
                {
                    throw new ArgumentException(
                        "Usage: /account create <account-id> <nickname> [male|female]");
                }
                byte gender = parts.Length == 3 ? ParseGender(parts[2]) : (byte)1;
                string password = _readConfirmedPassword(parts[0]);
                LocalPlayerProfile created = _administration.CreateAccount(
                    parts[0], parts[1], password, gender);
                Console.WriteLine(
                    $"Created account {created.AccountId} / {created.Nickname} " +
                    $"(user {created.UserId}, {(created.Gender == 0 ? "female" : "male")}).");
                return;
            }
            case "password":
            {
                string selector = values.Trim();
                if (selector.Length == 0 || selector.Contains(' '))
                {
                    throw new ArgumentException(
                        "Usage: /account password <account-id|nickname|user-id>");
                }
                string password = _readConfirmedPassword(selector);
                LocalPlayerProfile updated = _administration.SetPassword(selector, password);
                Console.WriteLine(
                    $"Password updated for {updated.AccountId} / {updated.Nickname} " +
                    $"(user {updated.UserId}).");
                return;
            }
            default:
                throw new ArgumentException(
                    "Usage: /account list | /account create <account-id> <nickname> " +
                    "[male|female] | /account password <selector>");
        }
    }

    private void ExecuteBigNews(string arguments)
    {
        string value = RequireText(
            arguments, "/bignews <message> or /bignews <title>|<message>");
        int separator = value.IndexOf('|');
        string title = separator < 0 ? string.Empty : value[..separator].Trim();
        string body = separator < 0 ? value : value[(separator + 1)..].Trim();
        PrintBroadcast(_administration.BroadcastBigNews(title, body));
    }

    private void PrintAccounts()
    {
        IReadOnlyList<PlayerAccountSummary> accounts = _administration.ListAccounts();
        Console.WriteLine($"{accounts.Count} account(s):");
        foreach (PlayerAccountSummary account in accounts)
        {
            Console.WriteLine(
                $"  {account.UserId}: {account.AccountId} / {account.Nickname} " +
                $"(level {account.Level})");
        }
    }

    private void PrintOnlinePlayers()
    {
        IReadOnlyList<OnlinePlayerSummary> players =
            _administration.ListOnlinePlayers();
        Console.WriteLine($"{players.Count} authenticated player(s) online:");
        foreach (OnlinePlayerSummary player in players)
        {
            Console.WriteLine(
                $"  {player.UserId}: {player.AccountId} / {player.Nickname} " +
                $"(session {player.WireUserId})");
        }
    }

    private static void PrintBroadcast(AdministrationBroadcastResult result) =>
        Console.WriteLine(
            $"Announcement delivered to {result.Delivered} player(s)" +
            (result.Failed == 0 ? "." : $"; {result.Failed} send(s) failed."));

    private static void PrintHelp()
    {
        Console.WriteLine("/accounts - list persisted accounts");
        Console.WriteLine("/online - list authenticated players currently connected");
        Console.WriteLine(
            "/account create <account-id> <nickname> [male|female] - create a login");
        Console.WriteLine(
            "/account password <account-id|nickname|user-id> - securely reset a password");
        Console.WriteLine(
            "/ban <account-id|nickname|user-id> <reason> - lock an account, kick them, "
            + "and alert the server (the reason stays private)");
        Console.WriteLine(
            "/suspend <account-id|nickname|user-id> <reason> - lock it as 'under review'");
        Console.WriteLine("/unban <account-id|nickname|user-id> - let them back in");
        Console.WriteLine("/announce <message> - cyan 30-second announcement");
        Console.WriteLine("/alert <message> - yellow 30-second announcement");
        Console.WriteLine("/say <message> - system chat line");
        Console.WriteLine("/bignews <message> or <title>|<message> - native news panel");
        Console.WriteLine(
            "/stop [reason] - drain active games, count down, and shut down cleanly");
        Console.WriteLine("Passwords are prompted invisibly and are never command arguments.");
    }

    private static byte ParseGender(string value) => value.ToLowerInvariant() switch
    {
        "female" or "f" or "0" => 0,
        "male" or "m" or "1" => 1,
        _ => throw new ArgumentException("Gender must be male/female or 1/0.")
    };

    private static string RequireText(string value, string usage)
    {
        string text = value.Trim();
        return text.Length != 0
            ? text
            : throw new ArgumentException($"Usage: {usage}");
    }

    private static (string Head, string Tail) SplitHead(string value)
    {
        string trimmed = value.Trim();
        int separator = trimmed.IndexOf(' ');
        return separator < 0
            ? (trimmed, string.Empty)
            : (trimmed[..separator], trimmed[(separator + 1)..].TrimStart());
    }
}
