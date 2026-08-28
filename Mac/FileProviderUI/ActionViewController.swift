import AppKit
import FileProvider
import FileProviderUI

/// What Finder's own Sign In button opens.
///
/// The provider answers requests with `.notAuthenticated` when the credential is gone, and the
/// system puts a Sign In button next to the volume — but the click goes nowhere unless the app
/// bundles a FileProviderUI extension to receive it. This is that extension, and it is deliberately
/// thin: the app already runs the sign-in properly (system browser sheet, password autofill, the
/// SMS code), and a second sign-in surface inside a Finder dialog would be a worse copy of it. So
/// this dialog says what has happened and hands the click to the app, which opens the sheet at once
/// (`AppModel.signInFromSystem`).
final class ActionViewController: FPUIActionExtensionViewController {

    override func loadView() {
        let message = NSTextField(wrappingLabelWithString:
            "Helmsley Drive is signed out of the portal. Signing in happens in the Helmsley Drive app — your password and the code texted to you, exactly as on the portal."
        )
        message.preferredMaxLayoutWidth = 380

        let open = NSButton(title: "Open Helmsley Drive", target: self, action: #selector(openApp))
        open.keyEquivalent = "\r"
        let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancelSignIn))
        cancel.keyEquivalent = "\u{1b}"

        let buttons = NSStackView(views: [cancel, open])
        buttons.orientation = .horizontal
        buttons.alignment = .centerY

        let stack = NSStackView(views: [message, buttons])
        stack.orientation = .vertical
        stack.alignment = .trailing
        stack.spacing = 16
        stack.edgeInsets = NSEdgeInsets(top: 20, left: 20, bottom: 20, right: 20)
        stack.translatesAutoresizingMaskIntoConstraints = false

        let container = NSView()
        container.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: container.topAnchor),
            stack.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            stack.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            container.widthAnchor.constraint(equalToConstant: 420),
        ])
        view = container
        preferredContentSize = container.fittingSize
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

    @objc private func openApp() {
        // The URL rather than the app bundle, because the URL carries the intent: the app answers
        // `helmsley-drive://signin` by opening the sign-in sheet itself, so the Finder click is the
        // whole gesture rather than the first half of one.
        NSWorkspace.shared.open(URL(string: "helmsley-drive://signin")!)
        extensionContext.completeRequest()
    }

    @objc private func cancelSignIn() {
        extensionContext.cancelRequest(withError: NSError(
            domain: FPUIErrorDomain,
            code: Int(FPUIExtensionErrorCode.userCancelled.rawValue)
        ))
    }
}
