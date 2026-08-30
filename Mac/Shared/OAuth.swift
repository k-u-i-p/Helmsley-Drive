import CryptoKit
import Foundation

/// Talks to the portal's OAuth endpoints (`backend/routes/oauth/`), mounted at the site root, so
/// `/authorize` and `/token` are absolute paths rather than anything under `/api`.
enum OAuth {

    struct TokenResponse: Decodable {
        let access_token: String
        let refresh_token: String?
        let expires_in: Double?
    }

    /// A code verifier and its S256 challenge. The whole of a public client's security: the app
    /// ships to laptops with no secret it could keep, so what proves the code is being redeemed by
    /// the process that requested it is knowledge of a value only that process generated.
    struct PKCE {
        let verifier: String
        var challenge: String {
            let digest = SHA256.hash(data: Data(verifier.utf8))
            return Data(digest).base64URLEncodedString()
        }

        init() {
            var bytes = [UInt8](repeating: 0, count: 32)
            _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
            verifier = Data(bytes).base64URLEncodedString()
        }
    }

    static func authorizeURL(pkce: PKCE, state: String) -> URL {
        var components = URLComponents(
            url: Configuration.baseURL.appendingPathComponent("authorize"),
            resolvingAgainstBaseURL: false
        )!
        components.queryItems = [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: Configuration.oauthClientID),
            URLQueryItem(name: "redirect_uri", value: Configuration.oauthRedirectURI),
            URLQueryItem(name: "code_challenge", value: pkce.challenge),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "state", value: state),
            // What this app is for: 'drive' opens the file-sync API and nothing else. The portal grants
            // by our registration either way, but asking for what we mean keeps the consent honest.
            URLQueryItem(name: "scope", value: "drive"),
        ]
        return components.url!
    }

    static func exchange(code: String, verifier: String) async throws -> TokenSet {
        try await token(form: [
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": Configuration.oauthRedirectURI,
            "client_id": Configuration.oauthClientID,
            "code_verifier": verifier,
        ])
    }

    /// Whether a token-endpoint refusal says the grant itself is dead.
    ///
    /// `invalid_grant` is the only refusal that means the refresh token is spent and signing in
    /// again is the way back. Everything else a 400 can carry — `invalid_request`, a proxy's error
    /// page, a body that is not JSON at all — leaves a refresh token that is still good, so an
    /// unrecognised refusal reads as "not this", and the credential is kept.
    static func isInvalidGrant(_ body: String) -> Bool {
        struct Refusal: Decodable { let error: String? }
        return (try? JSONDecoder().decode(Refusal.self, from: Data(body.utf8)))?.error == "invalid_grant"
    }

    static func refresh(_ refreshToken: String) async throws -> TokenSet {
        try await token(form: [
            "grant_type": "refresh_token",
            "refresh_token": refreshToken,
            "client_id": Configuration.oauthClientID,
        ])
    }

    private static func token(form: [String: String]) async throws -> TokenSet {
        var request = URLRequest(url: Configuration.baseURL.appendingPathComponent("token"))
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data(form.map { "\($0.key)=\($0.value.formURLEncoded)" }.joined(separator: "&").utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            throw OAuthError.tokenEndpoint(status: status, body: String(decoding: data, as: UTF8.self))
        }

        let decoded = try JSONDecoder().decode(TokenResponse.self, from: data)
        // The refresh token rotates on every use server-side, so a response without one would leave
        // nothing to refresh with next time — better to fail here than to discover it in an hour.
        guard let refresh = decoded.refresh_token else { throw OAuthError.noRefreshToken }
        return TokenSet(
            accessToken: decoded.access_token,
            refreshToken: refresh,
            accessExpiresAt: Date().addingTimeInterval(decoded.expires_in ?? 3600)
        )
    }
}

enum OAuthError: LocalizedError {
    case notAuthenticated
    case tokenEndpoint(status: Int, body: String)
    case noRefreshToken
    case stateMismatch

    var errorDescription: String? {
        switch self {
        case .notAuthenticated:
            return "Not signed in to Helmsley. Open Helmsley Drive and sign in again."
        case .tokenEndpoint(let status, let body):
            return "The sign-in server refused the request (HTTP \(status)): \(body)"
        case .noRefreshToken:
            return "The sign-in server issued no refresh token."
        case .stateMismatch:
            return "The sign-in response did not match the request that started it."
        }
    }
}

/// Hands out an access token that is valid right now, refreshing when it is not.
///
/// An actor, but the actor is not what makes a refresh happen once: actors are reentrant, and every
/// `await` in here is a door another caller can walk through while the first is suspended on the
/// network. The extension produces exactly that traffic — a poll round and a handful of fetches all
/// asking for a token in the same breath — and the server rotates refresh tokens, so two calls
/// spending the same one would see the second revoke what the first had just been issued. What
/// serialises the rotation is `refreshing`: one task does the work, and everyone who arrives while
/// it runs awaits that task instead of starting another.
actor TokenProvider {

    static let shared = TokenProvider()

    private var cached: TokenSet?

    /// The refresh in flight, if any. See the note on the actor: this task, not actor isolation, is
    /// what guarantees the refresh token is spent once per rotation in this process.
    private var refreshing: Task<String, Error>?

    var isSignedIn: Bool { (cached ?? TokenStore.load()) != nil }

    func store(_ tokens: TokenSet) throws {
        try TokenStore.save(tokens)
        cached = tokens
    }

    func signOut() {
        TokenStore.clear()
        cached = nil
    }

    /// Drops the in-memory copy, so the next question is answered from the keychain.
    ///
    /// For the app's window when it comes back to the front: the keychain is the only store the
    /// extension writes, and a cached access token can go on answering for up to an hour after the
    /// extension has signed out — or rotated — behind this process's back. What the window shows
    /// should be the shared truth, not this process's memory of it.
    func resync() {
        cached = nil
    }

    func accessToken() async throws -> String {
        if let tokens = cached, tokens.isAccessTokenFresh { return tokens.accessToken }

        if let refreshing { return try await refreshing.value }
        let task = Task { try await refresh() }
        refreshing = task
        defer { refreshing = nil }
        return try await task.value
    }

    /// The slow path: nothing fresh in memory, so the keychain and then the server are asked. Runs
    /// as the one `refreshing` task; everything below stays on the actor.
    private func refresh() async throws -> String {
        // Re-read before refreshing: the other process may have rotated the pair since this one
        // last looked, and spending a refresh token it has already replaced would revoke the live
        // credential and sign the user out of both.
        guard let stored = TokenStore.load() else {
            // The extension reaching this is the difference between "the portal is empty" and "this
            // process cannot see the credential the app saved" — indistinguishable in Finder.
            Log.auth.error("no token in keychain (access group \(Configuration.keychainAccessGroup, privacy: .public))")
            throw OAuthError.notAuthenticated
        }
        if stored.isAccessTokenFresh {
            cached = stored
            return stored.accessToken
        }

        do {
            return try await rotate(from: stored)
        } catch OAuthError.tokenEndpoint(let status, let body)
            where (status == 400 || status == 401) && OAuth.isInvalidGrant(body) {
            // The server says the grant itself is dead — not that the request was malformed or that
            // a proxy hiccuped, which also arrive as 400s and must not cost the credential. Either
            // the other process won a simultaneous refresh — in which case the keychain now holds a
            // pair this one has never seen, and that pair is good — or there is nothing left but to
            // sign in again.
            if let latest = TokenStore.load(), latest.refreshToken != stored.refreshToken {
                if latest.isAccessTokenFresh {
                    cached = latest
                    return latest.accessToken
                }
                return try await rotate(from: latest)
            }
            signOut()
            throw OAuthError.notAuthenticated
        }
    }

    private func rotate(from tokens: TokenSet) async throws -> String {
        Log.auth.info("refreshing access token")
        let refreshed = try await OAuth.refresh(tokens.refreshToken)
        // In memory before the keychain: the server has already retired the pair this was minted
        // from, so if the save fails this copy is the only one anywhere — throwing it away over a
        // keychain hiccup would sign the user out while holding a perfectly good credential.
        cached = refreshed
        do {
            try TokenStore.save(refreshed)
        } catch {
            Log.auth.error("could not save the refreshed tokens — carrying on in memory: \(error.localizedDescription, privacy: .public)")
        }
        return refreshed.accessToken
    }
}

extension Data {
    /// RFC 7636 wants base64url with no padding, which `base64EncodedString()` is not.
    func base64URLEncodedString() -> String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

extension String {
    var formURLEncoded: String {
        // `.urlQueryAllowed` leaves `+`, `&` and `=` intact, which in a form body would be read as
        // structure rather than as content.
        let unreserved = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        return addingPercentEncoding(withAllowedCharacters: unreserved) ?? self
    }
}
