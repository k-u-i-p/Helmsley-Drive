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

    public static TokenSet? Load()
    {
        byte[] sealed_;
        try { sealed_ = File.ReadAllBytes(TokenPath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        try
        {
            var plain = ProtectedData.Unprotect(sealed_, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TokenSet>(plain);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Another user's blob, or a half-written file. Unreadable either way: the state is
            // "nobody has signed in", and the file only says why the next sign-in was needed.
            Console.Error.WriteLine($"token store unreadable: {e.Message}");
            return null;
        }
    }

    public static void Save(TokenSet tokens)
    {
        Directory.CreateDirectory(Configuration.DataDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        File.WriteAllBytes(TokenPath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }

    public static void Clear() => File.Delete(TokenPath);
}
