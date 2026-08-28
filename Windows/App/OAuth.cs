using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelmsleyDrive.App;

/// <summary>
/// Talks to the portal's OAuth endpoints. The authorization server is the one the MCP connector
/// already uses, mounted at the site root, so <c>/authorize</c> and <c>/token</c> are absolute
/// paths rather than anything under <c>/api</c>.
/// </summary>
public static class OAuth
{
    sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public double? ExpiresIn { get; set; }
    }

    /// <summary>
    /// A code verifier and its S256 challenge. The whole of a public client's security: the app
    /// ships with no secret it could keep, so what proves the code is being redeemed by the process
    /// that requested it is knowledge of a value only that process generated.
    /// </summary>
    public sealed class Pkce
    {
        public string Verifier { get; } = Base64Url(RandomNumberGenerator.GetBytes(32));
        public string Challenge => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
    }

    public static string AuthorizeUrl(Pkce pkce, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = Configuration.OAuthClientId,
            ["redirect_uri"] = Configuration.OAuthRedirectUri,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["scope"] = Configuration.OAuthScope,
        };
        var encoded = string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Uri(Configuration.BaseUri, "authorize").ToString() + "?" + encoded;
    }

    public static Task<TokenSet> Exchange(string code, string verifier) =>
        Token(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Configuration.OAuthRedirectUri,
            ["client_id"] = Configuration.OAuthClientId,
            ["code_verifier"] = verifier,
        });

    public static Task<TokenSet> Refresh(string refreshToken) =>
        Token(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = Configuration.OAuthClientId,
        });

    // The token endpoint neither takes a bearer token nor answers with a redirect, so it wants
    // none of the API client's plumbing.
    static readonly HttpClient Http = new();

    static async Task<TokenSet> Token(Dictionary<string, string> form)
    {
        using var response = await Http.PostAsync(
            new Uri(Configuration.BaseUri, "token"),
            new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new OAuthException((int)response.StatusCode, body);

        var decoded = JsonSerializer.Deserialize<TokenResponse>(body);
        if (decoded?.AccessToken is not { } access)
            throw new OAuthException((int)response.StatusCode, "The sign-in server sent a response this app could not read.");
        // The refresh token rotates on every use server-side, so a response without one would leave
        // nothing to refresh with next time — better to fail here than to discover it in an hour.
        if (decoded.RefreshToken is not { } refresh)
            throw new OAuthException((int)response.StatusCode, "The sign-in server issued no refresh token.");
        return new TokenSet(access, refresh, DateTimeOffset.UtcNow.AddSeconds(decoded.ExpiresIn ?? 3600));
    }

    /// <summary>RFC 7636 wants base64url with no padding, which Convert's base64 is not.</summary>
    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>The sign-in server refused the request.</summary>
public sealed class OAuthException(int status, string body)
    : Exception($"The sign-in server refused the request (HTTP {status}): {body}")
{
    public int Status { get; } = status;
}

public sealed class NotAuthenticatedException()
    : Exception("Not signed in to Helmsley. Run Helmsley Drive and sign in again.");

/// <summary>
/// Hands out an access token that is valid right now, refreshing when it is not.
///
/// Serialised, because hydrations arrive concurrently and a refresh must happen once: the server
/// rotates refresh tokens, so two simultaneous refreshes would see the second revoke what the
/// first had just been issued. The Mac side also re-reads the store mid-refresh in case the other
/// process rotated the pair; there is no other process here, so that recovery has no counterpart.
/// </summary>
public sealed class TokenProvider
{
    public static readonly TokenProvider Shared = new();

    readonly SemaphoreSlim _gate = new(1, 1);
    TokenSet? _cached;

    public bool IsSignedIn => (_cached ?? TokenStore.Load()) is not null;

    public void Store(TokenSet tokens)
    {
        TokenStore.Save(tokens);
        _cached = tokens;
    }

    public void SignOut()
    {
        TokenStore.Clear();
        _cached = null;
    }

    public async Task<string> AccessToken()
    {
        await _gate.WaitAsync();
        try
        {
            if (_cached is { IsAccessTokenFresh: true } cached) return cached.AccessToken;

            var stored = TokenStore.Load() ?? throw new NotAuthenticatedException();
            if (stored.IsAccessTokenFresh)
            {
                _cached = stored;
                return stored.AccessToken;
            }

            try
            {
                Console.WriteLine("refreshing access token");
                var refreshed = await OAuth.Refresh(stored.RefreshToken);
                TokenStore.Save(refreshed);
                _cached = refreshed;
                return refreshed.AccessToken;
            }
            catch (OAuthException e) when (e.Status is 400 or 401)
            {
                // invalid_grant: the grant is dead and there is nothing left but to sign in again.
                SignOut();
                throw new NotAuthenticatedException();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
