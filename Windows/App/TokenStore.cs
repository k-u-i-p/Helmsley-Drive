using System.Security.Cryptography;
using System.Text.Json;

namespace HelmsleyDrive.App;

/// <summary>The OAuth token set, as it sits on disk.</summary>
public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt)
{
    /// <summary>
    /// Absolute, not the <c>expires_in</c> the server sends: the relative figure is only meaningful
    /// at the instant of the response, and this outlives the process that received it. Treated as
    /// expired a minute early, so a token that would have died mid-flight is refreshed before the
    /// request rather than retried after it.
    /// </summary>
    public bool IsAccessTokenFresh => AccessExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1);
}

/// <summary>
/// Reads and writes the token set under <see cref="Configuration.DataDirectory"/>, sealed with
/// DPAPI to the current user. The Mac counterpart (Mac/Shared/TokenStore.swift) is bigger because
/// two processes share the credential through a keychain access group; here one process is the
/// only reader, and a user-scoped blob in the profile is the whole of it.
/// </summary>
public static class TokenStore
{
    static string TokenPath => Path.Combine(Configuration.DataDirectory, "oauth-tokens.dat");

    /// <summary>
    /// Entropy mixed into the seal. DPAPI at CurrentUser scope is unsealed by anything running as
    /// the user, which is the whole of what it promises; this raises the price of that from
    /// "read the file and call CryptUnprotectData" to "read the binary first". It is not a secret
    /// and cannot be — a shipped app holds none — and changing it costs one sign-in, because an
    /// older blob then reads as unreadable, which is already handled.
    /// </summary>
    static readonly byte[] Entropy = "HelmsleyDrive/oauth-tokens/v1"u8.ToArray();

    public static TokenSet? Load()
    {
        byte[] protectedBytes;
        try { protectedBytes = File.ReadAllBytes(TokenPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Missing is the ordinary case. Held open by a scanner, or unreadable, is not — but
            // neither is worth taking the app down for, and this is read on every redraw.
            if (e is not (FileNotFoundException or DirectoryNotFoundException))
                Console.Error.WriteLine($"token store could not be read: {e.Message}");
            return null;
        }

        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TokenSet>(plain);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Another user's blob, or one sealed by a build that mixed different entropy.
            // Unreadable either way: the state is "nobody has signed in", and the file only says
            // why the next sign-in was needed.
            Console.Error.WriteLine($"token store unreadable: {e.Message}");
            return null;
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void Save(TokenSet tokens)
    {
        Directory.CreateDirectory(Configuration.DataDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            // Staged and swapped, as the snapshots are. The refresh token rotates server-side
            // before this is written, so the copy on its way to disk is the only one left: a
            // truncated file here is not a slow start but a sign-in the user has to do again.
            var staging = TokenPath + ".new";
            File.WriteAllBytes(staging, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            File.Move(staging, TokenPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void Clear()
    {
        // File.Delete forgives a missing file but not a missing directory, and before the first
        // sign-in there is neither.
        try { File.Delete(TokenPath); File.Delete(TokenPath + ".new"); }
        catch (DirectoryNotFoundException) { }
    }
}
