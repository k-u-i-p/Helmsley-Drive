import FileProvider
import Foundation

/// Tells the system a container's contents moved under it, so Finder reflects the change at once
/// instead of at the next time something happens to ask.
///
/// The bin is signalled twice, because it is held twice: the working set lists exactly what the
/// trash lists, and the system asks the two for changes separately, from anchors of their own.
/// Signalling only the container that changed would leave the other saying what the bin held
/// before — and the working set is the copy Finder reasons about when the trash is not open.
struct ChangeSignal {

    let domain: NSFileProviderDomain

    func fire(at container: NSFileProviderItemIdentifier) async {
        try? await NSFileProviderManager(for: domain)?.signalEnumerator(for: container)
        if container == .trashContainer {
            try? await NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet)
        }
    }
}

/// A container the poller can ask about without knowing what kind of container it is.
protocol PolledContainer: AnyObject {

    var polledIdentifier: NSFileProviderItemIdentifier { get }

    /// The container's signature as the server has it now — the same fingerprint an enumeration
    /// would finish with, worked out without reporting anything to anybody.
    func currentSignature() async throws -> Data
}

/// Watches the folders someone is currently looking at, and signals the ones that have moved on the
/// server.
///
/// This is a replicated extension, so the system owns the copy: once a folder has been enumerated it
/// is not enumerated again on a refresh or on navigating back into it — the system asks for changes,
/// and it asks only when this extension says there are some. Every write made through Finder already
/// says so. A document filed in the dashboard says nothing at all, and nothing in the framework goes
/// looking, so the folder holding it stays as it was until the domain is removed and re-added. That
/// is the whole of the "I had to sign out and back in" complaint.
///
/// The portal says so with a push now, and this is what turns one into the containers it means. A
/// file provider push signals the domain's working set and carries nothing else — the payload has
/// room for one container identifier and the system ignores any but the working set for a replicated
/// extension — so what arrives is "look again" and no more. `BinEnumerator` calls `checkNow()` when
/// the signalled working set reaches it, and this asks the folders someone actually has open.
///
/// The timer stays, at a fraction of the rate, for everything push cannot promise: a portal with no
/// APNs key, a device whose registration failed, a push dropped while the machine was asleep. What is
/// asked about either way is bounded to containers with a live enumerator — the system makes one when
/// it starts observing a folder and invalidates it when it stops, which is as close to "the user is
/// looking at this" as anything here gets. A folder nobody has open costs nothing; the tree at large
/// is never walked.
///
/// One exception: the working set is left out. Its enumerator is made once and kept for as long as
/// the extension lives, so polling it would mean a request every interval forever, awake or idle,
/// whether or not anyone is looking at anything. The trash is polled while it is open, and the
/// working set follows it there because `ChangeSignal` signals both.
///
/// Detection costs one listing; the enumeration the signal provokes costs a second. That is the
/// price of a portal that cannot say what changed, and it is paid only when something has.
actor ChangePoller {

    /// Long enough that a folder left open is not a standing load on the portal, short enough that
    /// filing something in the dashboard and turning to Finder shows it there. What the timer was
    /// when it was the only way of finding anything out, and what it goes back to whenever push is
    /// not established.
    private static let asking: Duration = .seconds(30)

    /// And what it becomes once the portal says it will push. Not switched off: a push is a datagram
    /// with no delivery anybody can check, so this is the interval at which a folder left open
    /// notices that one never arrived. Long enough to cost nothing, short enough that "nothing has
    /// updated" is never a state somebody sits in.
    private static let waiting: Duration = .seconds(900)

    private let signal: ChangeSignal
    private var watched: [String: Watch] = [:]
    private var loop: Task<Void, Never>?

    /// Whether the portal is in a position to push this device. Told by `PushRegistrar` from what the
    /// registration answered, and only ever an optimism about the timer: every round asks the same
    /// question of the same folders whichever this is.
    private var pushIsLive = false

    private var interval: Duration { pushIsLive ? Self.waiting : Self.asking }

    /// A round in progress, and whether another was asked for while it ran. A push landing during a
    /// round should not start a second one over the same folders, and should not be dropped either —
    /// it may be about a change the round in flight had already looked past.
    private var sweeping = false
    private var sweepAgain = false

    /// Which loop is the live one. A round that wakes up belonging to a retired generation stops
    /// there — without it, a task cancelled while sleeping goes on to run one more round, and that
    /// round can retire the handle of the loop that replaced it and leave two of them polling.
    private var generation = 0

    /// Weakly, because an enumerator's life is the system's to end: `invalidate()` deregisters, but
    /// a missed one must not keep the enumerator — or the folder's place in the poll — alive.
    private struct Watch {
        weak var container: (any PolledContainer)?
    }

    init(signal: ChangeSignal) {
        self.signal = signal
    }

    func watch(_ container: some PolledContainer) {
        let identifier = container.polledIdentifier
        guard identifier != .workingSet else { return }

        watched[identifier.rawValue] = Watch(container: container)

        // A folder somebody has just opened is the likeliest one in the volume to be out of date: a
        // push that landed while it was closed had no enumerator to ask about, and the system does
        // not re-list a folder on being navigated back into. So it is asked about once, shortly, and
        // then left to the push.
        //
        // Shortly rather than now, because the first enumeration of a folder is usually a moment
        // behind this call and it is the one that records what the folder holds. Asking before it
        // lands compares a listing against nothing, which reads as a change and costs a signal and a
        // re-listing — harmless, and worth three seconds to avoid on every folder anybody opens.
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(3))
            await self?.settle(identifier.rawValue)
        }

        guard loop == nil else { return }

        generation += 1
        let mine = generation
        loop = Task { [weak self] in
            while !Task.isCancelled {
                // Read each round rather than captured, so a registration that establishes push part
                // way through stretches the very next wait. Weakly, and outside the sleep: a poller
                // nobody holds should not be kept alive by a fifteen-minute nap.
                guard let interval = await self?.interval else { break }
                try? await Task.sleep(for: interval)
                guard let self, await self.tick(mine) else { break }
            }
        }
    }

    /// Something changed on the server — asked for now rather than at the next round.
    ///
    /// This is the whole of what a push does here. It says nothing about *what* changed, because the
    /// payload cannot: the containers to look at are the ones with a live enumerator, which is
    /// knowledge that exists in this process and nowhere else.
    func checkNow() async {
        guard !watched.isEmpty else { return }
        await sweep()
    }

    /// What `PushRegistrar` learned from registering: whether the portal will push this device at
    /// all. Only the fallback interval turns on it.
    func pushRegistered(_ live: Bool) {
        guard pushIsLive != live else { return }
        pushIsLive = live
        Log.enumeration.info("push is \(live ? "established — polling every 15 minutes as a fallback" : "unavailable — polling every 30 seconds", privacy: .public)")
    }

    /// Takes the enumerator rather than its identifier, and drops it only if it is still the one
    /// registered: the system invalidates the enumerator for a folder and makes a new one for the
    /// same folder as readily as not, and the two arrive here in whichever order they arrive. Going
    /// by identifier alone would let a departing enumerator deregister its replacement, and the
    /// folder would sit there unpolled.
    func stopWatching(_ container: some PolledContainer) {
        let key = container.polledIdentifier.rawValue
        guard watched[key]?.container === container else { return }

        watched[key] = nil
        guard watched.isEmpty else { return }
        loop?.cancel()
        loop = nil
        generation += 1
    }

    /// One round of asking, answering whether there is any reason to come back.
    private func tick(_ generation: Int) async -> Bool {
        guard generation == self.generation else { return false }
        await sweep()

        guard !watched.isEmpty else {
            loop = nil
            return false
        }
        return true
    }

    /// A round, and then another if one was asked for while it ran.
    ///
    /// Rounds do not overlap. Two at once would ask the server the same questions twice and could
    /// signal the same container twice from answers taken a moment apart; the second request simply
    /// waits for the first to finish and then runs, which is what makes a push landing mid-round
    /// neither doubled nor lost.
    private func sweep() async {
        guard !sweeping else {
            sweepAgain = true
            return
        }
        sweeping = true
        defer { sweeping = false }

        repeat {
            sweepAgain = false
            await ask()
        } while sweepAgain
    }

    /// One folder, just opened. Nothing if the system has since stopped observing it.
    private func settle(_ key: String) async {
        guard watched[key] != nil else { return }
        await ask(key)
    }

    /// Every watched container, asked once.
    private func ask() async {
        // Over a copy of the keys, and each one read again as it comes up: the awaits below let a
        // registration or an invalidation land mid-round, and what is polled should be what is
        // watched now rather than what was watched when the round began.
        for key in Array(watched.keys) {
            await ask(key)
        }
    }

    /// One container: has it moved on the server, and say so if it has.
    private func ask(_ key: String) async {
        guard let container = watched[key]?.container else {
            watched[key] = nil
            return
        }
        let identifier = container.polledIdentifier
        do {
            let current = try await container.currentSignature()
            let held = SnapshotStore.signature(of: await SnapshotStore.shared.current(for: identifier))
            guard current != held else { return }

            // Signalled every round the two disagree, rather than once: the store advances when the
            // enumeration actually happens, so this stops of its own accord the moment the system
            // has taken the change — and goes on asking if it has not.
            Log.enumeration.info("\(identifier.rawValue, privacy: .public) has moved on the server — signalling")
            await signal.fire(at: identifier)
        } catch {
            // A poll that fails changes nothing: the folder keeps what it had and the next round
            // asks again. Debug rather than error, because a closed laptop or a dropped link would
            // otherwise write a line every interval about a state nobody can act on.
            Log.enumeration.debug("polling \(identifier.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
        }
    }
}
