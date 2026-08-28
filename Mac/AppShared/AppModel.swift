import AuthenticationServices
import FileProvider
import Foundation
import SwiftUI
#if os(iOS)
import UIKit
#endif

/// What the window shows and what the buttons do.
///
/// The app itself does almost nothing: it signs in, and it tells the system that a domain exists.
/// Everything a person then does with the documents happens in Finder, serviced by the extension
/// in a different process — so the only state worth holding here is whether those two things are
/// currently true.
@MainActor
final class AppModel: ObservableObject {

    @Published private(set) var admin: Admin?
    @Published private(set) var isMounted = false
    @Published private(set) var isWorking = false
    @Published var errorMessage: String?

    private let signIn = SignIn()

    var isSignedIn: Bool { admin != nil }

    // MARK: - Lifecycle

    /// Called when the window appears: works out whether the stored credential is still good and
    /// whether the volume is still registered, since either can have changed while the app was shut.
    func refresh() async {
        isMounted = await Self.domainExists()

        guard await TokenProvider.shared.isSignedIn else {
            admin = nil
            return
        }
        // Asking the server who the token speaks for is also what proves it still works — an
        // expired refresh token fails here rather than the first time Finder opens a folder.
        do {
            admin = try await HelmsleyAPI.shared.whoami()
        } catch OAuthError.notAuthenticated {
            admin = nil
        } catch {
            // A server that cannot be reached is not a sign-out: the credential may be perfectly
            // good, so the identity is left unknown rather than being thrown away.
            errorMessage = error.localizedDescription
        }
    }

    // MARK: - Actions

    func connect() async {
        await perform {
            let tokens = try await self.signIn.run()
            try await TokenProvider.shared.store(tokens)

            self.admin = try await HelmsleyAPI.shared.whoami()
            try await Self.addDomain()
            self.isMounted = true
        }
    }

    /// Registers the domain against a credential that is already good.
    ///
    /// Separate from `connect()` because the two can come apart: a domain is removed when the app
    /// bundle backing it goes away — a rebuild to a new location will do it — and the credential is
    /// untouched by that. Without this the only route back is signing out and in, which means
    /// another password and another text message to undo something that was never about identity.
    func mount() async {
        await perform {
            try await Self.addDomain()
            self.isMounted = true
        }
    }

    func disconnect() async {
        await perform {
            // Before either, because it is the one step that still needs the credential: the portal
            // authenticates the withdrawal as it authenticated the registration. Best-effort, since
            // a token left behind costs a push nobody sees and is not worth failing a sign-out over.
            if let token = PushTokenStore.token {
                try? await HelmsleyAPI.shared.forgetPushToken(token)
                PushTokenStore.token = nil
            }

            // The domain first: removing it takes the volume out of Finder and discards whatever
            // the system had cached of it. Doing it after clearing the credential would leave a
            // mounted volume that could not answer a single request.
            try await Self.removeDomain()
            self.isMounted = false

            await TokenProvider.shared.signOut()
            self.admin = nil
        }
    }

    #if os(iOS)
    /// Opens the Files app at the mounted volume.
    ///
    /// Two steps, and the first is the one that has to be asked rather than assumed: the system
    /// decides where a domain's replica lives, and `getUserVisibleURL` is the only thing that knows.
    /// Writing the path down here would be a guess about a private layout that has already changed
    /// once between iOS releases.
    ///
    /// The second step is `shareddocuments:`, which is how anything gets Files to open at a folder.
    /// It is the file URL under another scheme, and it is not in any header — Apple documents no way
    /// to do this at all. That is a real caveat and the reason this fails quietly rather than
    /// loudly: if a future iOS stops answering it, the button says so and the volume is still there
    /// in Files under Locations, which is where the banner has always pointed. It is also why this
    /// app can afford the risk at all — it ships to a handful of staff over TestFlight, and never
    /// goes past a reviewer who could object to the scheme.
    func openInFiles() async {
        guard let manager = NSFileProviderManager(for: Self.domain) else {
            errorMessage = "The volume is not registered on this device."
            return
        }

        do {
            let visible = try await manager.getUserVisibleURL(for: .rootContainer)
            guard var components = URLComponents(url: visible, resolvingAgainstBaseURL: false) else {
                throw CocoaError(.fileNoSuchFile)
            }
            components.scheme = "shareddocuments"

            guard let target = components.url, await UIApplication.shared.open(target) else {
                errorMessage = "Files would not open. The documents are in the Files app, under Locations."
                return
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }
    #endif

    private func perform(_ work: @escaping () async throws -> Void) async {
        isWorking = true
        errorMessage = nil
        do {
            try await work()
        } catch let error as NSError where error.domain == ASWebAuthenticationSessionErrorDomain
            && error.code == ASWebAuthenticationSessionError.canceledLogin.rawValue {
            // Closing the sign-in sheet is a decision, not a failure, so it says nothing.
        } catch {
            errorMessage = error.localizedDescription
        }
        isWorking = false
    }

    // MARK: - The domain

    private static var domain: NSFileProviderDomain {
        NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(Configuration.domainIdentifier),
            displayName: Configuration.domainDisplayName
        )
    }

    private static func domainExists() async -> Bool {
        let domains = (try? await NSFileProviderManager.domains()) ?? []
        return domains.contains { $0.identifier.rawValue == Configuration.domainIdentifier }
    }

    private static func addDomain() async throws {
        guard await !domainExists() else { return }
        try await NSFileProviderManager.add(domain)
    }

    private static func removeDomain() async throws {
        guard await domainExists() else { return }
        try await NSFileProviderManager.remove(domain)
    }
}
