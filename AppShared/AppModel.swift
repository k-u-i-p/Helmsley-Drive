import AuthenticationServices
import FileProvider
import Foundation
import SwiftUI

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
            // The domain first: removing it takes the volume out of Finder and discards whatever
            // the system had cached of it. Doing it after clearing the credential would leave a
            // mounted volume that could not answer a single request.
            try await Self.removeDomain()
            self.isMounted = false

            await TokenProvider.shared.signOut()
            self.admin = nil
        }
    }

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
