import Foundation
import Security

/// Identifiers and defaults shared by the container app and the file provider extension.
///
/// The two are separate processes with separate containers, so everything either of them needs to
/// agree about — where the server is, which keychain item holds the credential, what the mounted
/// domain is called — is named here once rather than settled twice.
enum Configuration {

    // MARK: - Server

    /// Where the portal lives. Overridable so the extension can be pointed at a local backend
    /// without a rebuild; the app's Settings pane writes it, and both processes read it out of the
    /// shared defaults below.
    static let defaultBaseURL = URL(string: "https://helmsley-clients.co.uk")!

    /// The OAuth client this app is registered as, matching `mcp.clients[].clientId` in the
    /// portal's config.json. A public client: it ships to laptops, so it holds no secret, and PKCE
    /// is what binds an authorization code to the process that asked for it.
    static let oauthClientID = "helmsley-drive"

    /// Registered against that client id server-side. A custom scheme rather than a loopback
    /// listener because `ASWebAuthenticationSession` claims it for the duration of the sign-in,
    /// which means no port to pick and nothing left listening afterwards.
    static let oauthRedirectURI = "helmsley-drive://oauth/callback"

    // MARK: - Sharing between the app and the extension

    /// Non-secret settings live here; the group container is the only writable place both
    /// processes can see, since each is sandboxed into a container of its own otherwise.
    static let appGroupIdentifier = "group.uk.co.helmsley.HelmsleyDrive"

    /// The credential itself lives in the keychain instead, in this access group.
    ///
    /// Read back out of the running binary's own entitlements rather than written down. The
    /// entitlement files declare it as `$(AppIdentifierPrefix)uk.co.helmsley.HelmsleyDrive`, and
    /// only the build system expands that prefix — so any constant here is a second copy of a value
    /// the signing process owns, and the two disagree the moment the development team changes.
    ///
    /// That disagreement is worth this much trouble to avoid: it is not a build error but a silent
    /// `errSecMissingEntitlement` at the first keychain call, which presents as being unable to sign
    /// in, with nothing on screen pointing at the team id. Asking the binary what it was actually
    /// signed with cannot be wrong.
    static let keychainAccessGroup: String = {
        #if os(macOS)
        // SecTask reads the running binary's entitlements straight back. iOS has no such API.
        if let task = SecTaskCreateFromSelf(nil),
           let groups = SecTaskCopyValueForEntitlement(task, "keychain-access-groups" as CFString, nil) as? [String],
           // The entitlement may list several; ours is the one ending in the group we declared.
           let group = groups.first(where: { $0.hasSuffix(".\(declaredKeychainGroup)") }) ?? groups.first {
            return group
        }
        #else
        if let group = probedKeychainAccessGroup() { return group }
        #endif
        // Unsigned, or signed without the entitlement — the keychain will refuse either way, so
        // this only decides which access group appears in the error.
        return declaredKeychainGroup
    }()

    private static let declaredKeychainGroup = "uk.co.helmsley.HelmsleyDrive"

    #if !os(macOS)
    /// The prefix, asked of the keychain itself.
    ///
    /// An item added without naming an access group lands in the *first* group the entitlement
    /// lists, and reports back which one that was. Ours lists exactly one, so a throwaway item is a
    /// reliable way to learn the team prefix on a platform that will not simply say.
    private static func probedKeychainAccessGroup() -> String? {
        let identity: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: "access-group-probe",
        ]
        // Any leftover from a previous launch would make the add fail as a duplicate.
        SecItemDelete(identity as CFDictionary)
        defer { SecItemDelete(identity as CFDictionary) }

        var created: CFTypeRef?
        var insert = identity
        insert[kSecValueData as String] = Data()
        insert[kSecReturnAttributes as String] = true
        guard SecItemAdd(insert as CFDictionary, &created) == errSecSuccess,
              let attributes = created as? [String: Any] else { return nil }
        return attributes[kSecAttrAccessGroup as String] as? String
    }
    #endif

    /// Service name of the keychain item holding the token set.
    static let keychainService = "uk.co.helmsley.HelmsleyDrive.oauth"

    // MARK: - The mounted volume

    /// Identifies the file provider domain. Stable for the life of the install: changing it
    /// orphans whatever the system has already synced under the old one.
    static let domainIdentifier = "helmsley-documents"

    /// What Finder shows in the sidebar.
    static let domainDisplayName = "Helmsley Documents"

    // MARK: - Shared defaults

    static var sharedDefaults: UserDefaults {
        UserDefaults(suiteName: appGroupIdentifier) ?? .standard
    }

    private static let baseURLKey = "baseURL"

    /// The portal origin both processes talk to. Falls back to the production one, so a fresh
    /// install works before anything has been configured.
    static var baseURL: URL {
        get {
            guard let stored = sharedDefaults.string(forKey: baseURLKey),
                  let url = URL(string: stored) else { return defaultBaseURL }
            return url
        }
        set { sharedDefaults.set(newValue.absoluteString, forKey: baseURLKey) }
    }
}
