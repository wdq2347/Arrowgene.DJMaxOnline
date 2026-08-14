namespace Arrowgene.DJMaxOnline.Server;

/// <summary>
/// Whether an account may log in, and which dialog the client shows when it may not.
///
/// Stored as this enum rather than the wire number so the database stays readable and
/// the protocol value lives in one place (<see cref="AccountLockReasons"/>).
/// </summary>
public enum AccountLockState
{
    /// <summary>Normal. The account logs in.</summary>
    None = 0,

    /// <summary>
    /// Locked outright - a ban. DISCONNECTMSG5: "Your account is currently locked and
    /// cannot be used. Please contact the DJMAX operations team."
    /// </summary>
    Locked = 1,

    /// <summary>
    /// Suspended pending review. DISCONNECTMSG6: "The operations team is currently
    /// managing your account. It will be usable after review."
    /// </summary>
    UnderReview = 2
}

/// <summary>
/// The disconnect reasons the client maps to an account-state dialog.
///
/// Read out of sub_44D012, which switches on the reason the OnDisconnectPeerInf handler
/// (sub_4318E0) stores at net+895300:
///     5   DISCONNECTMSG3   client version mismatch
///     22  DISCONNECTMSG2   same account connected elsewhere
///     25  LOGINMSG8        password mismatch
///     26  LOGINMSG11       input timeout
///     27  DISCONNECTMSG5   account locked
///     28  DISCONNECTMSG6   account under review
///     29  LOGINMSG10       server refused authentication
///
/// NEVER send 161 or 162. sub_4318E0 routes those into sub_436DF0, the retail
/// anti-cheat, which snapshots the running process list and contacts a hardcoded
/// address. They are not ours to use.
/// </summary>
public static class AccountLockReasons
{
    public const short Locked = 27;
    public const short UnderReview = 28;

    /// <summary>The reason byte for a state, or null when the account may log in.</summary>
    public static short? ReasonFor(AccountLockState state) => state switch
    {
        AccountLockState.Locked => Locked,
        AccountLockState.UnderReview => UnderReview,
        _ => null
    };

    public static string Describe(AccountLockState state) => state switch
    {
        AccountLockState.Locked => "locked",
        AccountLockState.UnderReview => "under review",
        _ => "active"
    };
}
