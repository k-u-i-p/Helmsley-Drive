import AuthenticationServices
import FileProvider
import FileProviderUI
import UIKit

/// What the Files app's own sign-in prompt opens.
///
/// The provider answers requests with `.notAuthenticated` when the credential is gone, and Files
/// offers to sign in — but the offer goes nowhere unless the app bundles a FileProviderUI extension
/// to receive it. Unlike its macOS twin, which relays the click to the app, this one runs the
/// sign-in itself: an iOS extension has no supported way to open its containing app, and the
/// sign-in does not need one — `SignIn` is the same system browser sheet the app uses, autofill and
/// all, anchored to the window the system put this view in rather than one asked of
/// `UIApplication`, which is barred in extensions.
final class ActionViewController: FPUIActionExtensionViewController {

    private lazy var signIn = SignIn(anchor: { [weak self] in
        self?.view.window ?? ASPresentationAnchor()
    })

    private let problem = UILabel()
    private var actions: [UIButton] = []

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .systemBackground

        let explanation = UILabel()
        explanation.text = "Helmsley Drive is signed out of the portal. Sign in with your password and the code texted to you, exactly as on the portal."
        explanation.font = .preferredFont(forTextStyle: .body)
        explanation.numberOfLines = 0

        problem.font = .preferredFont(forTextStyle: .footnote)
        problem.textColor = .systemOrange
        problem.numberOfLines = 0
        problem.isHidden = true

        var prominent = UIButton.Configuration.borderedProminent()
        prominent.title = "Sign In"
        let signInButton = UIButton(configuration: prominent, primaryAction: UIAction { [weak self] _ in
            self?.start()
        })

        var plain = UIButton.Configuration.plain()
        plain.title = "Cancel"
        let cancel = UIButton(configuration: plain, primaryAction: UIAction { [weak self] _ in
            self?.extensionContext.cancelRequest(withError: NSError(
                domain: FPUIErrorDomain,
                code: Int(FPUIExtensionErrorCode.userCancelled.rawValue)
            ))
        })
        actions = [signInButton, cancel]

        let stack = UIStackView(arrangedSubviews: [explanation, problem, signInButton, cancel])
        stack.axis = .vertical
        stack.spacing = 16
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 24),
            stack.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24),
        ])
    }

    override func prepare(forError error: any Error) {
        // Nothing to read off the error: the one thing the provider reports this way is
        // `.notAuthenticated`, and the view already says so.
    }

    override func prepare(forAction actionIdentifier: String, itemIdentifiers: [NSFileProviderItemIdentifier]) {
        // No custom actions are declared, so nothing should arrive here.
        extensionContext.cancelRequest(withError: NSError(
            domain: FPUIErrorDomain,
            code: Int(FPUIExtensionErrorCode.failed.rawValue)
        ))
    }

    private func start() {
        problem.isHidden = true
        actions.forEach { $0.isEnabled = false }

        Task {
            defer { actions.forEach { $0.isEnabled = true } }
            do {
                let tokens = try await signIn.run()
                try await TokenProvider.shared.store(tokens)

                // The system throttles a domain that reported .notAuthenticated and keeps offering
                // sign-in until told otherwise; this is the telling. Best-effort, because the
                // credential is saved either way and the next request proves it.
                let domain = NSFileProviderDomain(
                    identifier: NSFileProviderDomainIdentifier(Configuration.domainIdentifier),
                    displayName: Configuration.domainDisplayName
                )
                try? await NSFileProviderManager(for: domain)?
                    .signalErrorResolved(NSFileProviderError(.notAuthenticated))

                extensionContext.completeRequest()
            } catch let error as NSError where error.domain == ASWebAuthenticationSessionErrorDomain
                && error.code == ASWebAuthenticationSessionError.canceledLogin.rawValue {
                // Closing the sheet is a decision, not a failure: the dialog stays for another go.
            } catch {
                problem.text = error.localizedDescription
                problem.isHidden = false
            }
        }
    }
}
