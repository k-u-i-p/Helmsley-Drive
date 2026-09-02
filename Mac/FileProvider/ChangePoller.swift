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

/// What the working set's enumerator reports on the watched folders' behalf, and what to remember
/// once it has.
///
/// The snapshots ride along unrecorded. They are written by `ChangePoller.delivered(_:)`, after the
/// system has taken the changes — a folder's changes have no sync anchor of their own to fall back
/// on, so recording them first meant that an extension killed between the write and the hand-over
/// (iOS does that, under memory pressure) lost them for good: the next round found the folder in
/// step and said nothing. Recorded afterwards, the worst case is a change reported twice, which the
/// system takes in its stride.
struct PendingChanges {
    var updated: [NSFileProviderItem] = []
    var deleted: [NSFileProviderItemIdentifier] = []
    fileprivate var snapshots: [(container: NSFileProviderItemIdentifier, snapshot: SnapshotStore.Snapshot)] = []
}

/// A container the poller can ask about without knowing what kind of container it is.
protocol PolledContainer: AnyObject {

    var polledIdentifier: NSFileProviderItemIdentifier { get }

    /// The container as the server has it now: the items it holds, and the version map a diff and a
    /// sync anchor are both built from. One request, so the two always describe the same moment.
    func currentListing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot)

    /// Where the container's listing comes from, so the poller can ask about several containers in
    /// one request (`HelmsleyAPI.listings`) — or nil for one listed some other way, which is asked
    /// about on its own through `currentListing()`.
    var polledDestination: Destination? { get }

    /// The container as a listing the server has just answered describes it: what `currentListing()`
    /// would have made of the same answer. Only asked of a container with a `polledDestination`.
    func contents(of listing: Listing) -> ([NSFileProviderItem], SnapshotStore.Snapshot)
}

extension PolledContainer {
    var polledDestination: Destination? { nil }
    func contents(of listing: Listing) -> ([NSFileProviderItem], SnapshotStore.Snapshot) { ([], [:]) }
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
/// Detection costs one request for every folder watched, and the reporting enumeration a second.
/// That is the price of a portal that cannot say what changed, and it is paid only when something
/// has — and it is one request, not one per folder: the portal answers a batch of folders in a
/// single round trip, and both steps ask for the watched folders that way.
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
    private let api = HelmsleyAPI.shared
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
    /// Each folder's snapshot is recorded once the system has taken its changes — `delivered(_:)`,
    /// which the caller owes this — so the round that follows finds it in step and says nothing.
    /// The trash is left out: the working set's own listing is the bin, so its diff is already
    /// covered and reporting it twice would report every binned item as changed whenever the trash
    /// happened to be open.
    func pendingChanges() async -> PendingChanges {
        var pending = PendingChanges()

        for (container, current) in await currentListings(excludingTrash: true) {
            let identifier = container.polledIdentifier
            let previous = await SnapshotStore.shared.current(for: identifier)
            guard previous != current.1 else { continue }

            let changes = await Departures.changes(from: previous, to: current, in: identifier.rawValue)
            pending.updated += changes.updated
            pending.deleted += changes.deleted
            pending.snapshots.append((identifier, changes.snapshot))
        }
        return pending
    }

    /// Every watched container as the server has it now: one request for all the folders that can
    /// be asked about together, and one each for the rest. A container whose listing fails is left
    /// out — it keeps what it had, and the next round asks again. Debug rather than error, because
    /// a closed laptop would otherwise write a line every time the system asked.
    private func currentListings(excludingTrash: Bool) async -> [(container: any PolledContainer, listing: ([NSFileProviderItem], SnapshotStore.Snapshot))] {
        var together: [(container: any PolledContainer, destination: Destination)] = []
        var alone: [any PolledContainer] = []
        // Over a copy of the keys, and each one read again as it comes up: the awaits below let a
        // registration or an invalidation land mid-round, and what is polled should be what is
        // watched now rather than what was watched when the round began.
        for key in Array(watched.keys) {
            guard let container = watched[key]?.container else {
                watched[key] = nil
                continue
            }
            if excludingTrash, container.polledIdentifier == .trashContainer { continue }
            if let destination = container.polledDestination {
                together.append((container, destination))
            } else {
                alone.append(container)
            }
        }
        // One folder is asked for on its own, as it always was; the batch is for two or more.
        if together.count == 1 {
            alone.append(together[0].container)
            together.removeAll()
        }

        var listings: [(container: any PolledContainer, listing: ([NSFileProviderItem], SnapshotStore.Snapshot))] = []
        for container in alone {
            do {
                listings.append((container, try await container.currentListing()))
            } catch {
                Log.enumeration.debug("listing \(container.polledIdentifier.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        guard !together.isEmpty else { return listings }
        do {
            let answers = try await api.listings(together.map(\.destination))
            for (entry, answer) in zip(together, answers) {
                switch answer {
                case .success(let listing):
                    listings.append((entry.container, entry.container.contents(of: listing)))
                case .failure(let error):
                    Log.enumeration.debug("listing \(entry.container.polledIdentifier.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                }
            }
        } catch {
            Log.enumeration.debug("listing \(together.count) folders together failed: \(error.localizedDescription, privacy: .public)")
        }
        return listings
    }

    /// The system has taken `pending`, so what it described is what each folder is now known to
    /// hold. See `PendingChanges` for why this is a second step rather than part of the first.
    func delivered(_ pending: PendingChanges) async {
        for (container, snapshot) in pending.snapshots {
            await SnapshotStore.shared.record(snapshot, for: container)
        }
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

    /// Every watched container, asked once — in one request, for the folders that can be.
    private func ask() async {
        for (container, current) in await currentListings(excludingTrash: false) {
            await consider(container, current.1)
        }
    }

    /// One container, asked on its own.
    private func ask(_ key: String) async {
        guard let container = watched[key]?.container else {
            watched[key] = nil
            return
        }
        do {
            await consider(container, try await container.currentListing().1)
        } catch {
            // A poll that fails changes nothing: the folder keeps what it had and the next round
            // asks again. Debug rather than error, because a closed laptop or a dropped link would
            // otherwise write a line every interval about a state nobody can act on.
            Log.enumeration.debug("polling \(container.polledIdentifier.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// One container's version map against what it is known to hold: has it moved on the server,
    /// and say so if it has.
    private func consider(_ container: any PolledContainer, _ current: SnapshotStore.Snapshot) async {
        let identifier = container.polledIdentifier
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
    }
}
