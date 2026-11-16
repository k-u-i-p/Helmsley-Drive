import Foundation
import Security

/// Identifiers and defaults shared by the container app and the file provider extension.
///
/// The two are separate processes with separate containers, so everything either of them needs to
/// agree about — where the server is, which keychain item holds the credential, what the mounted
/// domain is called — is named here once rather than settled twice.
enum Configuration {

    // MARK: - Server

    /// Where the portal lives. Fixed at build time: a shipped app talks to the portal and nothing
    /// else, so there is no address for anyone to get wrong.
    static let baseURL = URL(string: "https://helmsley.co.uk")!

    /// The OAuth client this app is registered as, matching `oauth.clients[].clientId` in the
    /// portal's config.json. A public client: it ships to laptops, so it holds no secret, and PKCE
    /// is what binds an authorization code to the process that asked for it.
    static let oauthClientID = "helmsley-drive"

    /// Registered against that client id server-side. A custom scheme rather than a loopback
    /// listener because `ASWebAuthenticationSession` claims it for the duration of the sign-in,
    /// which means no port to pick and nothing left listening afterwards.
    static let oauthRedirectURI = "helmsley-drive://oauth/callback"

    // MARK: - Sharing between the app and the extension

    /// The enumeration snapshots live here; the group container is the only writable place both
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
    static var keychainAccessGroup: String {
        #if os(macOS)
        return entitledKeychainAccessGroup
        #else
        // Asked again on every call until it answers, and remembered only once it has: a probe
        // that fails — the device still locked when a push loads the extension, say — must not
        // pin this process to a group no entitlement grants for the rest of its life.
        if let known = probeLock.withLock({ probedGroup }) { return known }
        guard let group = probedKeychainAccessGroup() else { return declaredKeychainGroup }
        probeLock.withLock { probedGroup = group }
        return group
        #endif
    }

    private static let declaredKeychainGroup = "uk.co.helmsley.HelmsleyDrive"

    #if os(macOS)
    private static let entitledKeychainAccessGroup: String = {
        // SecTask reads the running binary's entitlements straight back. iOS has no such API.
        if let task = SecTaskCreateFromSelf(nil),
           let groups = SecTaskCopyValueForEntitlement(task, "keychain-access-groups" as CFString, nil) as? [String],
           // The entitlement may list several; ours is the one ending in the group we declared.
           let group = groups.first(where: { $0.hasSuffix(".\(declaredKeychainGroup)") }) ?? groups.first {
            return group
        }
        // Unsigned, or signed without the entitlement — the keychain will refuse either way, so
        // this only decides which access group appears in the error.
        return declaredKeychainGroup
    }()
    #else
    private static let probeLock = NSLock()
    private static var probedGroup: String?

    /// The prefix, asked of the keychain itself.
    ///
    /// An item added without naming an access group lands in the *first* group the entitlement
    /// lists, and reports back which one that was. Ours lists exactly one, so a throwaway item is a
    /// reliable way to learn the team prefix on a platform that will not simply say.
    private static func probedKeychainAccessGroup() -> String? {
        // Named for this process alone. The app, the provider and the sign-in extension all probe,
        // and can start together; a shared name would let one's add fail as the other's duplicate,
        // or one's tidy-up delete the other's item from under it.
        let identity: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: "access-group-probe." + UUID().uuidString,
        ]
        defer { SecItemDelete(identity as CFDictionary) }

        var created: CFTypeRef?
        var insert = identity
        insert[kSecValueData as String] = Data()
        insert[kSecReturnAttributes as String] = true
        // As reachable as the token itself: the provider is loaded by a push while the phone is
        // locked, and an item the keychain will not write until it is unlocked would fail exactly
        // then — and only then.
        insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        let status = SecItemAdd(insert as CFDictionary, &created)
        guard status == errSecSuccess, let attributes = created as? [String: Any],
              let group = attributes[kSecAttrAccessGroup as String] as? String else {
            Log.auth.error("keychain access-group probe failed: \(KeychainError(status: status).localizedDescription, privacy: .public)")
            return nil
        }
        return group
    }
    #endif

    /// Service name of the keychain item holding the token set.
    static let keychainService = "uk.co.helmsley.HelmsleyDrive.oauth"

    // MARK: - Push

    /// Which APNs environment this build's push tokens belong to.
    ///
    /// Reported when the token is registered, because nothing about a token says which host will
    /// accept it: a development-signed build's token is simply unknown to the production host and
    /// the other way round. The portal treats this as the first thing to try and corrects itself
    /// from what APNs answers, so being wrong costs one rejected push rather than the feature.
    ///
    /// Read from the running binary's own entitlement on macOS, where that can be asked, and taken
    /// from the build configuration on iOS, where it cannot: a debug build is what Xcode installs
    /// with a development profile, and a release build is what is archived for TestFlight.
    static let pushEnvironment: String = {
        #if os(macOS)
        if let task = SecTaskCreateFromSelf(nil),
           let value = SecTaskCopyValueForEntitlement(task, "com.apple.developer.aps-environment" as CFString, nil) as? String {
            return value
        }
        return "development"
        #elseif DEBUG
        return "development"
        #else
        return "production"
        #endif
    }()

    /// Which of the two platforms this is, for the portal's own record of a registered device.
    static let platformName: String = {
        #if os(macOS)
        return "macOS"
        #else
        return "iOS"
        #endif
    }()

    // MARK: - The mounted volume

    /// Identifies the file provider domain. Stable for the life of the install: changing it
    /// orphans whatever the system has already synced under the old one.
    static let domainIdentifier = "helmsley-documents"

    /// What Finder shows in the sidebar.
    static let domainDisplayName = "Helmsley Documents"
}
