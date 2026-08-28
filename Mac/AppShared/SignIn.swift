import AuthenticationServices
import Foundation

/// Runs the OAuth authorization-code flow in a system browser sheet.
///
/// `ASWebAuthenticationSession` rather than a `WKWebView`: the portal's admin sign-in is
/// password plus an SMS code, and putting that in a window this app controls would both train
/// staff to type credentials into whatever looks like Helmsley and cut the sheet off from the
/// browser's own password autofill. It also owns the custom URL scheme for the duration, so the
/// redirect comes back without this app having to register a handler that outlives the sign-in.
///
/// The window the sheet hangs off is the caller's to name, because the callers do not agree on how
/// to find one: the apps ask their application object, and the Files sign-in extension — where
/// `UIApplication` is barred outright — has only the window the system put its view in.
@MainActor
final class SignIn: NSObject {

    /// Held for the life of the sheet: releasing the session cancels it.
    private var session: ASWebAuthenticationSession?

    private let anchor: () -> ASPresentationAnchor

    init(anchor: @escaping () -> ASPresentationAnchor) {
        self.anchor = anchor
    }

    func run() async throws -> TokenSet {
        let pkce = OAuth.PKCE()
        // Guards against a callback that did not come from the request this process started.
        let state = UUID().uuidString

        let callback = try await authorize(url: OAuth.authorizeURL(pkce: pkce, state: state))

        let components = URLComponents(url: callback, resolvingAgainstBaseURL: false)
        let items = components?.queryItems ?? []
        let value = { (name: String) in items.first { $0.name == name }?.value }

        // The server redirects errors back here too, and its description is written for a person.
        if let error = value("error") {
            throw OAuthError.tokenEndpoint(status: 400, body: value("error_description") ?? error)
        }
        guard value("state") == state else { throw OAuthError.stateMismatch }
        guard let code = value("code") else { throw OAuthError.stateMismatch }

        return try await OAuth.exchange(code: code, verifier: pkce.verifier)
    }

    private func authorize(url: URL) async throws -> URL {
        let scheme = URL(string: Configuration.oauthRedirectURI)?.scheme

        return try await withCheckedThrowingContinuation { continuation in
            let session = ASWebAuthenticationSession(url: url, callbackURLScheme: scheme) { callback, error in
                if let callback {
                    continuation.resume(returning: callback)
                } else {
                    continuation.resume(throwing: error ?? OAuthError.notAuthenticated)
                }
            }
            session.presentationContextProvider = self
            // A shared session, so an admin already signed in to the portal in Safari is not asked
            // for a password and an SMS code all over again — the consent screen is all they see.
            session.prefersEphemeralWebBrowserSession = false
            self.session = session
            session.start()
        }
    }
}

extension SignIn: ASWebAuthenticationPresentationContextProviding {
    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        anchor()
    }
}
