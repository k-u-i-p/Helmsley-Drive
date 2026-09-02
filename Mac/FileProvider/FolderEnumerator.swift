import FileProvider
import Foundation

/// Whether an item may be reported, saying so in the log where it may not.
///
/// Handing the framework an item with no name aborts the extension outright — see
/// `FileProviderItem.isNameable` — and doing it here would take down a whole enumeration and
/// everything else in flight with it. So a nameless row is left out of the listing rather than
/// reported, which costs the folder one entry and a line in the log instead of the volume.
private func nameable(_ item: FileProviderItem, in container: String) -> Bool {
    guard !item.isNameable else { return true }
    Log.enumeration.error("an item of \(container, privacy: .public) has no name — left out of the listing")
    return false
}

/// Items and the version map together, with anything nameless dropped from both.
///
/// One pass over the items that were actually built, so the snapshot cannot describe a row the
/// listing left out and cannot spell a version the item does not have.
private func snapshotting(
    _ items: [FileProviderItem],
    in container: String
) -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
    var reported: [NSFileProviderItem] = []
    var snapshot = SnapshotStore.Snapshot()
    for item in items where nameable(item, in: container) {
        reported.append(item)
        snapshot[item.itemIdentifier.rawValue] = item.snapshotSignature
    }
    return (reported, snapshot)
}

/// A listing's changes since the snapshot it was compared with, every departure settled.
struct Changes {
    var updated: [NSFileProviderItem]
    var deleted: [NSFileProviderItemIdentifier]
    /// The listing to record, with anything left unsettled carried over from the previous one.
    var snapshot: SnapshotStore.Snapshot
}

/// Where the items a listing no longer holds have gone.
///
/// A diff can say that an identifier has left this container and nothing more, and the two things
/// that can mean are reported in opposite ways. A row that was purged is a deletion. A row that was
/// moved to another folder, thrown into the bin or put back out of it is still there, and
/// `didDeleteItems` is not "left this folder" but "gone": it would drop the row from the folder
/// that now holds it — whose own snapshot already carries it, so it would never be reported there
/// again. Snapshots outlive launches, so a folder opened for the first time in weeks would have
/// reported every row that moved out of it since as gone, and taken each one out of wherever it
/// had gone to.
///
/// So each departure is looked up, and reported as what the server says it is now: gone, or an
/// update carrying its current parent, which is what a move and a trashing both amount to. A
/// declared folder that has since been materialised answers under the row that replaced it, and
/// the reference Finder held is gone — the listing vends the row. A lookup the portal could not
/// answer settles nothing, so the identifier is carried into the recorded snapshot under its old
/// signature and asked about again next time, rather than guessed at in either direction.
///
/// One request per departure, paid only when something has left — which is the same price the
/// rest of this file pays for a portal that cannot say what changed.
enum Departures {

    static func changes(
        from previous: SnapshotStore.Snapshot,
        to current: ([NSFileProviderItem], SnapshotStore.Snapshot),
        in container: String
    ) async -> Changes {
        let (updated, departed) = SnapshotStore.diff(previous, current.1, over: current.0)
        var changes = Changes(updated: updated, deleted: [], snapshot: current.1)
        var elsewhere = 0, unsettled = 0
        for identifier in departed {
            switch await whereabouts(of: identifier) {
            case .gone:
                changes.deleted.append(identifier)
            case .elsewhere(let item):
                changes.updated.append(item)
                elsewhere += 1
            case .unknown:
                changes.snapshot[identifier.rawValue] = previous[identifier.rawValue]
                unsettled += 1
            }
        }
        if !departed.isEmpty {
            Log.enumeration.info("\(departed.count, privacy: .public) left \(container, privacy: .public) — \(changes.deleted.count, privacy: .public) gone, \(elsewhere, privacy: .public) elsewhere, \(unsettled, privacy: .public) unsettled")
        }
        return changes
    }

    private enum Whereabouts {
        case gone
        case elsewhere(FileProviderItem)
        case unknown
    }

    private static func whereabouts(of identifier: NSFileProviderItemIdentifier) async -> Whereabouts {
        // Anything this volume no longer mints — the two-tree era's prefixes — is to be dropped,
        // and the mount point never departs from anywhere.
        guard case .node(let id)? = ItemIdentity(identifier) else { return .gone }
        do {
            let remote = try await HelmsleyAPI.shared.item(id: id)
            // Answered with another row: the reference was a declared folder's, since materialised.
            guard remote.id == id else { return .gone }
            let item = FileProviderItem.item(remote)
            // A nameless row cannot be handed over (`FileProviderItem.isNameable`), and the listing
            // that would have held it has already left it out.
            return item.isNameable ? .elsewhere(item) : .gone
        } catch let error as APIError where error.isNotFound {
            return .gone
        } catch {
            Log.enumeration.debug("could not settle where \(identifier.rawValue, privacy: .public) went: \(error.localizedDescription, privacy: .public)")
            return .unknown
        }
    }
}

/// Lists one folder of the portal's tree, and answers what has changed in it since a given sync
/// anchor.
///
/// The portal has no change feed — a row records when it was written and nothing else — so
/// "changed" is computed here, by listing the folder and diffing against what it held last time
/// (`SnapshotStore`). That is enough to be correct: every item carries a version derived from its
/// content hash, so an edit, an addition and a removal are all visible in the diff.
///
/// The system asks for changes only when it is told there are some, and nothing about a file filed
/// in the dashboard reaches this process on its own — so a live enumerator registers with
/// `ChangePoller`, which is woken by the portal's push and falls back to asking on a slow timer. That
/// is what keeps an open folder current; a write made through Finder signals directly and waits for
/// neither.
final class FolderEnumerator: NSObject, NSFileProviderEnumerator, PolledContainer {

    private let identity: ItemIdentity
    private let api = HelmsleyAPI.shared
    private let poller: ChangePoller

    var polledIdentifier: NSFileProviderItemIdentifier { identity.identifier }

    init(identity: ItemIdentity, watchedBy poller: ChangePoller) {
        self.identity = identity
        self.poller = poller
        super.init()
        Task { await poller.watch(self) }
    }

    /// The system is done observing this folder, so it stops being polled. Nothing else is torn
    /// down: the snapshot belongs to the container, not to this object, and the next enumerator for
    /// the same folder picks up where this one left off.
    func invalidate() {
        Task { [poller, self] in await poller.stopWatching(self) }
    }

    // MARK: - Full enumeration

    func enumerateItems(for observer: NSFileProviderEnumerationObserver, startingAt page: NSFileProviderPage) {
        Task {
            do {
                let (items, snapshot) = try await currentListing()
                await SnapshotStore.shared.record(snapshot, for: identity.identifier)
                Log.enumeration.info("listed \(self.identity.logDescription, privacy: .public) — \(items.count, privacy: .public) items")
                observer.didEnumerate(items)
                // No paging: `/api/files/list` answers a whole folder in one query, ordered by the
                // same index the dashboard uses. A folder large enough to need pages would need the
                // server to offer it first.
                observer.finishEnumerating(upTo: nil)
            } catch {
                Log.enumeration.error("listing \(self.identity.logDescription, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                observer.finishEnumeratingWithError(FileProviderError.translate(error))
            }
        }
    }

    // MARK: - Change enumeration

    func enumerateChanges(for observer: NSFileProviderChangeObserver, from anchor: NSFileProviderSyncAnchor) {
        Task {
            // The listing the anchor was issued for, looked up before the request rather than after:
            // a diff against any other listing is not the diff that was asked for, and answering one
            // as though it were leaves the system recording itself as up to date having never been
            // told what it missed. `.syncAnchorExpired` is the framework's word for it — the system
            // drops what it holds for the folder and asks for a full listing, which costs one extra
            // request the once and is right from then on.
            guard let previous = await SnapshotStore.shared.snapshot(matching: anchor, for: identity.identifier) else {
                Log.enumeration.info("\(self.identity.logDescription, privacy: .public) was asked for changes from an anchor it no longer holds — asking for a full listing")
                observer.finishEnumeratingWithError(NSFileProviderError(.syncAnchorExpired))
                return
            }

            do {
                let current = try await currentListing()
                let changes = await Departures.changes(from: previous, to: current, in: identity.logDescription)
                await SnapshotStore.shared.record(changes.snapshot, for: identity.identifier)

                Log.enumeration.info("changes in \(self.identity.logDescription, privacy: .public) — \(changes.updated.count, privacy: .public) updated, \(changes.deleted.count, privacy: .public) deleted")
                if !changes.deleted.isEmpty { observer.didDeleteItems(withIdentifiers: changes.deleted) }
                if !changes.updated.isEmpty { observer.didUpdate(changes.updated) }
                observer.finishEnumeratingChanges(upTo: NSFileProviderSyncAnchor(SnapshotStore.signature(of: changes.snapshot)), moreComing: false)
            } catch {
                Log.enumeration.error("changes in \(self.identity.logDescription, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                observer.finishEnumeratingWithError(FileProviderError.translate(error))
            }
        }
    }

    func currentSyncAnchor(completionHandler: @escaping (NSFileProviderSyncAnchor?) -> Void) {
        Task {
            let snapshot = await SnapshotStore.shared.current(for: identity.identifier)
            completionHandler(NSFileProviderSyncAnchor(SnapshotStore.signature(of: snapshot)))
        }
    }

    // MARK: - Shared listing

    /// The folder as it stands, as items and as the version map the diff and the anchor are built
    /// from. Both come out of one request, so the anchor an enumeration finishes with always
    /// describes exactly the items it just reported.
    func currentListing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
        contents(of: try await api.list(identity.destination))
    }

    var polledDestination: Destination? { identity.destination }

    func contents(of listing: Listing) -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
        snapshotting(FileProviderItem.contents(of: listing, in: identity), in: identity.logDescription)
    }
}

/// What has been thrown away, under either of the two containers that hold it.
///
/// One bin for the whole volume, which is what the framework offers and what the portal has: a
/// trashed row keeps the folder it was in, so nothing needs a bin per directory to know where a
/// thing came from. What is listed is the top of each thing thrown away — a directory takes its
/// contents with it, and the files inside are still under it and come back with it. That is what
/// the working set asks for in so many words: trashed items belong in it, their children do not.
///
/// The working set is the same listing because here it holds nothing else. Filling it with the tree
/// would mean enumerating every document of every client of every syndicate on a schedule nobody
/// asked for — indexing the building rather than opening one drawer. The bin is the exception: it
/// is small, bounded by what one admin threw away, and the framework wants it there. What it buys
/// is Spotlight, and knowledge the system keeps when nobody has the trash open.
///
/// The same listing-and-diffing as a folder, for the same reason. Each container keeps its own
/// snapshot, because the system asks the two for changes independently and from anchors of their own.
final class BinEnumerator: NSObject, NSFileProviderEnumerator, PolledContainer {

    private let container: NSFileProviderItemIdentifier
    private let api = HelmsleyAPI.shared
    private let poller: ChangePoller

    var polledIdentifier: NSFileProviderItemIdentifier { container }

    init(container: NSFileProviderItemIdentifier, watchedBy poller: ChangePoller) {
        self.container = container
        self.poller = poller
        super.init()
        // Only the trash is taken up: the working set's enumerator is made once and kept for as
        // long as the extension lives, and `ChangePoller` declines it for that reason. It hears
        // about the bin anyway, since signalling the trash signals the working set with it.
        Task { await poller.watch(self) }
    }

    /// For the log, where "the trash" and "the working set" are worth telling apart.
    private var describing: String {
        container == .workingSet ? "working set" : "trash"
    }

    func invalidate() {
        Task { [poller, self] in await poller.stopWatching(self) }
    }

    func enumerateItems(for observer: NSFileProviderEnumerationObserver, startingAt page: NSFileProviderPage) {
        Task {
            do {
                let (items, snapshot) = try await currentListing()
                await SnapshotStore.shared.record(snapshot, for: container)
                Log.enumeration.info("\(self.describing, privacy: .public) — \(items.count, privacy: .public) items")
                observer.didEnumerate(items)
                observer.finishEnumerating(upTo: nil)
            } catch {
                Log.enumeration.error("listing the \(self.describing, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                observer.finishEnumeratingWithError(FileProviderError.translate(error))
            }
        }
    }

    /// This is where every change in the volume is reported, and the only place the system takes one.
    ///
    /// A replicated extension is asked for changes here and nowhere else: signalling a folder's own
    /// container is discarded (`ChangeSignal` quotes the header), and the folders somebody has open
    /// are never asked. So the working set answers for two things — the bin, which is what it holds,
    /// and whatever has moved in the folders the poller is watching, which is what a push or a poll
    /// round has just said to go and look for.
    ///
    /// The items carry their own parents, so the system files each one where it belongs; it applies
    /// them to its copy of those folders and Finder redraws. That is what "the system will
    /// automatically propagate working set changes to the UI" means, and it is the whole of how a
    /// document filed in the dashboard reaches a window somebody is looking at.
    func enumerateChanges(for observer: NSFileProviderChangeObserver, from anchor: NSFileProviderSyncAnchor) {
        Task {
            guard let previous = await SnapshotStore.shared.snapshot(matching: anchor, for: container) else {
                Log.enumeration.info("the \(self.describing, privacy: .public) was asked for changes from an anchor it no longer holds — asking for a full listing")
                observer.finishEnumeratingWithError(NSFileProviderError(.syncAnchorExpired))
                return
            }

            do {
                let current = try await currentListing()
                var changes = await Departures.changes(from: previous, to: current, in: describing)
                // Recorded before the hand-over, unlike the folders' below: the bin has an anchor,
                // so an enumeration that never completes is asked for again from the old one.
                await SnapshotStore.shared.record(changes.snapshot, for: container)

                // The trash's own enumerator answers for the bin alone. Only the working set carries
                // the rest of the volume, because only the working set is asked on the system's own
                // schedule — the trash is asked while somebody has it open, and the folders these
                // items belong to are never asked at all.
                var pending = PendingChanges()
                if container == .workingSet {
                    pending = await poller.pendingChanges()
                    changes.updated += pending.updated
                    changes.deleted += pending.deleted
                }

                Log.enumeration.info("\(self.describing, privacy: .public) — \(changes.updated.count, privacy: .public) updated, \(changes.deleted.count, privacy: .public) gone")
                if !changes.deleted.isEmpty { observer.didDeleteItems(withIdentifiers: changes.deleted) }
                if !changes.updated.isEmpty { observer.didUpdate(changes.updated) }
                observer.finishEnumeratingChanges(upTo: NSFileProviderSyncAnchor(SnapshotStore.signature(of: changes.snapshot)), moreComing: false)
                // Only once the system has them: see `PendingChanges`.
                await poller.delivered(pending)
            } catch {
                Log.enumeration.error("changes in the \(self.describing, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                observer.finishEnumeratingWithError(FileProviderError.translate(error))
            }
        }
    }

    func currentSyncAnchor(completionHandler: @escaping (NSFileProviderSyncAnchor?) -> Void) {
        Task {
            let snapshot = await SnapshotStore.shared.current(for: container)
            completionHandler(NSFileProviderSyncAnchor(SnapshotStore.signature(of: snapshot)))
        }
    }

    func currentListing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
        snapshotting(try await api.trashed().map { FileProviderItem.item($0) }, in: describing)
    }
}
