import Foundation
import Security

/// The OAuth token set, as it sits in the keychain.
struct TokenSet: Codable, Sendable {
    var accessToken: String
    var refreshToken: String
    /// Absolute, not the `expires_in` the server sends: the relative figure is only meaningful at
    /// the instant of the response, and this outlives the process that received it.
    var accessExpiresAt: Date

    /// Treated as expired a minute early, so a token that would have died mid-flight is refreshed
    /// before the request rather than retried after it.
    var isAccessTokenFresh: Bool { accessExpiresAt.timeIntervalSinceNow > 60 }
}

/// Reads and writes the token set in the shared keychain access group.
///
/// Shared because the extension does the work and the app does the signing in: the tokens are
/// written by one process and spent by another, and the keychain is the only store both can reach
/// that the rest of the user's software cannot.
enum TokenStore {

    private static let account = "default"

    private static var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Configuration.keychainService,
            kSecAttrAccount as String: account,
            kSecAttrAccessGroup as String: Configuration.keychainAccessGroup,
            // The modern keychain, which is the only one that honours an access group on macOS
            // without a keychain-sharing prompt on every read from the second process.
            kSecUseDataProtectionKeychain as String: true,
        ]
    }

    static func load() -> TokenSet? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else {
            // errSecItemNotFound is ordinary (nobody has signed in). Anything else — a missing
            // entitlement above all — is a misconfiguration that would otherwise be invisible.
            if status != errSecItemNotFound {
                Log.auth.error("keychain read failed: \(KeychainError(status: status).localizedDescription, privacy: .public)")
            }
            return nil
        }
        return try? JSONDecoder().decode(TokenSet.self, from: data)
    }

    static func save(_ tokens: TokenSet) throws {
        let data = try JSONEncoder().encode(tokens)

        // Update first, add on miss: SecItemAdd on an existing item is a duplicate error, and the
        // two calls take different dictionaries, so there is no single "upsert" to make of them.
        let updateStatus = SecItemUpdate(
            baseQuery as CFDictionary,
            [kSecValueData as String: data] as CFDictionary
        )
        if updateStatus == errSecSuccess { return }
        guard updateStatus == errSecItemNotFound else { throw KeychainError(status: updateStatus) }

        var insert = baseQuery
        insert[kSecValueData as String] = data
        // The extension runs without a user session behind it, so anything requiring an unlocked
        // login keychain at first-unlock granularity would fail there and only there.
        insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock

        let addStatus = SecItemAdd(insert as CFDictionary, nil)
        guard addStatus == errSecSuccess else { throw KeychainError(status: addStatus) }
    }

    static func clear() {
        SecItemDelete(baseQuery as CFDictionary)
    }
}

struct KeychainError: LocalizedError {
    let status: OSStatus

    var errorDescription: String? {
        let detail = SecCopyErrorMessageString(status, nil) as String? ?? "status \(status)"
        return "Keychain access failed: \(detail)"
    }
}
