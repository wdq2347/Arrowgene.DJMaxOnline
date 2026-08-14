using System.Security.Cryptography;

namespace Arrowgene.DJMaxOnline.Server;

/// <summary>PBKDF2-SHA256 password hashing and constant-time verification.</summary>
public static class PasswordSecurity
{
    /// <summary>
    /// Identifies the stored hash format. NOT configurable: it is written into every
    /// credential and <see cref="IsSupported"/> refuses anything else, so changing it
    /// would orphan existing accounts rather than re-hash them.
    /// </summary>
    public const string Algorithm = "PBKDF2-SHA256";

    public const int HashSize = 32;

    /// <summary>
    /// Cost and length limits, from <c>Setting.PasswordPolicy</c>. Applied once at
    /// startup by <see cref="Configure"/>; the defaults here are what an unconfigured
    /// server uses. Iterations and salt size affect only NEW credentials - each stored
    /// credential carries the iteration count it was hashed with.
    /// </summary>
    public static int Iterations { get; private set; } = 600_000;
    public static int SaltSize { get; private set; } = 16;
    public static int MinimumLength { get; private set; } = 10;
    public static int MaximumLength { get; private set; } = 128;

    public static void Configure(PasswordPolicySetting? policy)
    {
        PasswordPolicySetting p = (policy ?? new PasswordPolicySetting()).Validated();
        Iterations = p.Iterations;
        SaltSize = p.SaltSize;
        MinimumLength = p.MinimumLength;
        MaximumLength = p.MaximumLength;
    }

    private static readonly byte[] DummySalt =
        Convert.FromHexString("BEBF943B695AA24C50EA327D9079F8E2");
    private static readonly byte[] DummyHash = new byte[HashSize];

    public static PlayerPasswordCredential Create(uint userId, string password)
    {
        ValidateNewPassword(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Derive(password, salt, Iterations, HashSize);
        return new PlayerPasswordCredential(userId, Algorithm, Iterations, salt, hash);
    }

    public static bool Verify(string password, PlayerPasswordCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!IsSupported(credential) || password == null || password.Length > MaximumLength)
        {
            PerformDummyVerification(password ?? string.Empty);
            return false;
        }

        byte[] candidate = Derive(
            password, credential.Salt, credential.Iterations, credential.Hash.Length);
        try
        {
            return CryptographicOperations.FixedTimeEquals(candidate, credential.Hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public static void PerformDummyVerification(string password)
    {
        string bounded = password.Length > MaximumLength
            ? password[..MaximumLength]
            : password;
        byte[] candidate = Derive(bounded, DummySalt, Iterations, HashSize);
        CryptographicOperations.FixedTimeEquals(candidate, DummyHash);
        CryptographicOperations.ZeroMemory(candidate);
    }

    public static void ValidateNewPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinimumLength || password.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"Password must contain {MinimumLength}-{MaximumLength} characters.",
                nameof(password));
        }
    }

    private static bool IsSupported(PlayerPasswordCredential credential) =>
        credential.Algorithm == Algorithm &&
        credential.Iterations >= 100_000 &&
        credential.Iterations <= 2_000_000 &&
        credential.Salt.Length >= SaltSize &&
        credential.Hash.Length >= HashSize;

    private static byte[] Derive(
        string password,
        byte[] salt,
        int iterations,
        int outputSize) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            outputSize);
}
