import FileProvider
import Foundation
import PushKit

/// Tells the portal where to push this device, so a change made anywhere else reaches the volume at
/// once instead of at the next time something asks.
///
/// A file provider push is not a notification anybody sees and not one this code ever handles: the
/// system takes delivery of it, signals the domain's working set itself, and never calls
/// `pushRegistry(_:didReceiveIncomingPushWith:for:)` for the `.fileProvider` type at all. So the
/// whole of this class is the registration half — obtain a token, hand it to the portal, withdraw it
/// when the system says it has gone stale. What happens when a push lands is `BinEnumerator`'s, which
/// is where the signalled working set arrives.
///
/// Registered here rather than in the container app, though both are allowed. The extension is loaded
/// whenever anything touches the volume; the app might not be opened again for months, and a token
/// that changed in between would leave the volume unreachable until somebody thought to open it.
final class PushRegistrar: NSObject, PKPushRegistryDelegate {

    private let registry: PKPushRegistry
    private let domain: NSFileProviderDomain
    private let poller: ChangePoller
    private let api = HelmsleyAPI.shared

    /// A failed registration is retried on this ladder and then left alone. The extension re-registers
    /// on every launch — PushKit re-delivers the token each time the push types are set — so the case
    /// this covers is narrow: an extension that stays loaded across a spell with no network. Giving up
    /// after a few minutes is safe because giving up means polling on the short interval, which is
    /// what the app did before any of this existed.
    private static let retries: [Duration] = [.seconds(5), .seconds(30), .seconds(120)]

    /// The registration in flight. PushKit delivers twice in one launch — the cached token the moment
    /// the types are set, APNs's word again shortly after — and the retry ladder can stretch the
    /// first delivery's task minutes past the second's. Cancelled before each new one starts, so the
    /// token the portal ends up holding is always the last one delivered. Touched only on the
    /// registry's queue, which is the main queue, so it needs no ordering of its own.
    private var registration: Task<Void, Never>?

    init(domain: NSFileProviderDomain, poller: ChangePoller) {
        self.domain = domain
        self.poller = poller
        // The main queue, because there is nothing here worth a queue of its own: the delegate does
        // no work beyond starting a task.
        self.registry = PKPushRegistry(queue: .main)
        super.init()
        registry.delegate = self
        // Setting this is what asks for a token; the delegate hears back with one shortly, and hears
        // back again on every later launch.
        registry.desiredPushTypes = [.fileProvider]
    }

    // MARK: - PKPushRegistryDelegate

    func pushRegistry(_ registry: PKPushRegistry, didUpdate credentials: PKPushCredentials, for type: PKPushType) {
        guard type == .fileProvider else { return }
        let token = credentials.token.map { String(format: "%02x", $0) }.joined()

        registration?.cancel()
        registration = Task { [api, domain, poller] in
            for attempt in 0...PushRegistrar.retries.count {
                do {
                    let live = try await api.registerPushToken(token, domain: domain.identifier.rawValue)
                    guard !Task.isCancelled else { return }
                    PushTokenStore.token = token
                    await poller.pushRegistered(live)
                    Log.provider.info("push token registered — the portal \(live ? "will push" : "has no push configured", privacy: .public)")
                    return
                } catch OAuthError.notAuthenticated {
                    // Nobody is signed in yet, or the credential has lapsed. Retrying would fail the
                    // same way every time; signing in loads the extension afresh, which registers.
                    Log.provider.info("push token not registered — not signed in")
                    return
                } catch {
                    // Cancelled means a later delivery has taken over, and the failure is not one
                    // to report: the ladder, the fallback and the log line all belong to it now.
                    guard !Task.isCancelled else { return }
                    guard attempt < PushRegistrar.retries.count else {
                        Log.provider.error("registering the push token failed: \(error.localizedDescription, privacy: .public) — falling back to polling")
                        return await poller.pushRegistered(false)
                    }
                    try? await Task.sleep(for: PushRegistrar.retries[attempt])
                    guard !Task.isCancelled else { return }
                }
            }
        }
    }

    /// The system has withdrawn the token. Whatever the portal holds is now a way to reach nothing,
    /// so it is told — and the poller goes back to its short interval, since nothing is going to
    /// arrive until a new token does.
    func pushRegistry(_ registry: PKPushRegistry, didInvalidatePushTokenFor type: PKPushType) {
        guard type == .fileProvider else { return }
        // A registration still climbing the ladder is registering the very token being withdrawn.
        registration?.cancel()
        registration = nil
        let held = PushTokenStore.token
        PushTokenStore.token = nil

        Task { [api, poller] in
            await poller.pushRegistered(false)
            guard let held else { return }
            do {
                try await api.forgetPushToken(held)
                Log.provider.info("push token invalidated by the system and withdrawn from the portal")
            } catch {
                // Not worth retrying: the portal drops a token APNs reports as dead, and a token
                // nobody can be reached at is one APNs reports as dead the first time it is used.
                Log.provider.error("withdrawing the invalidated push token failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }
}
