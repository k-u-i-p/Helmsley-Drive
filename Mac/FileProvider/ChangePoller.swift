import FileProvider
import Foundation

/// Tells the system something moved, so Finder reflects it at once instead of at the next time
/// something happens to ask.
///
/// The working set, always, whatever moved. That is not a simplification but the only thing the
/// framework will act on — `NSFileProviderManager.h`, on `signalEnumeratorForContainerItemIdentifier`:
///
/// > When using NSFileProviderReplicatedExtension, only call this method with
/// > NSFileProviderWorkingSetContainerItemIdentifier. Other container identifiers are ignored. The
/// > system will automatically propagate working set changes to the UI, without explicitly
/// > signaling the containers currently being viewed in the UI.
///
/// This used to name the container that had changed, which is what a non-replicated extension does
/// and what every article about them describes. It was accepted and discarded every time: the
/// enumerator for the folder was never asked for changes, and a document filed in the dashboard sat
/// there unseen however loudly this said otherwise. The signal is only half the mechanism — what the
/// system then asks for is the working set's changes, and `BinEnumerator` is where the folders
/// somebody is looking at are turned into items to report.
struct ChangeSignal {

    let domain: NSFileProviderDomain

    func fire() async {
        try? await NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet)
    }
}

/// A container the poller can ask about without knowing what kind of container it is.
protocol PolledContainer: AnyObject {

    var polledIdentifier: NSFileProviderItemIdentifier { get }

    /// The container as the server has it now: the items it holds, and the version map a diff and a
    /// sync anchor are both built from. One request, so the two always describe the same moment.
    func currentListing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot)
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
/// Two jobs, and they are two halves of one mechanism.
///
/// It is the register of what somebody is looking at: the system makes an enumerator when it starts
/// observing a folder and invalidates it when it stops, which is as close to "the user has this
/// open" as anything here gets. `pendingChanges()` is what the working set's enumerator asks for,
/// since the working set is the only container the system takes changes from. A folder nobody has
/// open costs nothing; the tree at large is never walked.
///
/// And it notices, on a timer, that a folder has moved — which is what turns into a signal. The
/// portal pushes now, so the timer runs at a fraction of its old rate and covers only what push
/// cannot promise: no APNs key, a failed registration, a push dropped while the machine slept.
///
/// The working set is left out of the watch: its enumerator lives as long as the extension, so
/// polling it would mean a request every interval forever, and it is what everything is reported *to*.
///
/// Detection costs one listing and the reporting enumeration costs a second. That is the price of a
/// portal that cannot say what changed, and it is paid only when something has.
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

    /// What has changed in the folders somebody is looking at, as items to report and identifiers to
    /// drop. Called by the working set's enumerator, which is the only place the system takes changes
    /// from — a folder's own enumerator is never asked, however loudly its container is signalled.
    ///
    /// This is where a push turns into something visible. The payload says only "look again" (it has
    /// room for one container identifier, and the system ignores any but the working set), so the
    /// folders to ask about are worked out here, where knowing which ones have an enumerator is
    /// possible at all.
    ///
    /// Each folder's snapshot is recorded as its changes are handed over, so the round that follows
    /// finds it in step and says nothing. The trash is left out: the working set's own listing is the
    /// bin, so its diff is already covered and reporting it twice would report every binned item as
    /// changed whenever the trash happened to be open.
    func pendingChanges() async -> (updated: [NSFileProviderItem], deleted: [NSFileProviderItemIdentifier]) {
        var updated: [NSFileProviderItem] = []
        var deleted: [NSFileProviderItemIdentifier] = []

        for key in Array(watched.keys) {
            guard let container = watched[key]?.container else {
                watched[key] = nil
                continue
            }
            let identifier = container.polledIdentifier
            guard identifier != .trashContainer else { continue }

            do {
                let (items, snapshot) = try await container.currentListing()
                let previous = await SnapshotStore.shared.current(for: identifier)
                guard previous != snapshot else { continue }

                let moved = SnapshotStore.diff(previous, snapshot, over: items)
                updated += moved.updated
                deleted += moved.deleted
                await SnapshotStore.shared.record(snapshot, for: identifier)
            } catch {
                // The folder keeps what it had, and the next round asks again. Debug rather than
                // error: a closed laptop would otherwise write a line every time the system asked.
                Log.enumeration.debug("collecting changes in \(identifier.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        return (updated, deleted)
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
    /// Rounds do not overlap. Two at once would ask the server the same questions twice and signal
    /// twice from answers taken a moment apart; a request arriving mid-round simply waits for the one
    /// in flight to finish and then runs, so it is neither doubled nor lost.
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
            let current = try await container.currentListing().1
            let held = await SnapshotStore.shared.current(for: identifier)
            guard current != held else { return }

            // The working set, not this folder — signalling the folder is discarded (see
            // `ChangeSignal`). What the system then asks the working set for is `pendingChanges()`,
            // which lists this folder again and reports what moved.
            //
            // Signalled every round the two disagree, rather than once: the store advances when the
            // change is actually handed over, so this stops of its own accord the moment the system
            // has taken it — and goes on asking if it has not.
            Log.enumeration.info("\(identifier.rawValue, privacy: .public) has moved on the server — signalling the working set")
            await signal.fire()
        } catch {
            // A poll that fails changes nothing: the folder keeps what it had and the next round
            // asks again. Debug rather than error, because a closed laptop or a dropped link would
            // otherwise write a line every interval about a state nobody can act on.
            Log.enumeration.debug("polling \(identifier.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
        }
    }
}
