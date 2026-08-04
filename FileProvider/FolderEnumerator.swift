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

/// Lists one folder of the portal's document tree, and answers what has changed in it since a
/// given sync anchor.
///
/// The portal has no change feed — `documents` records an upload date and nothing else — so
/// "changed" is computed here, by listing the folder and diffing against what it held last time
/// (`SnapshotStore`). That is enough to be correct: every item carries a version derived from its
/// content hash, so an edit, an addition and a removal are all visible in the diff.
///
/// The system asks for changes only when it is told there are some, and nothing about a document
/// filed in the dashboard reaches this process on its own — so a live enumerator registers with
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

    func currentListing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
        try await listing()
    }

    // MARK: - Full enumeration

    func enumerateItems(for observer: NSFileProviderEnumerationObserver, startingAt page: NSFileProviderPage) {
        Task {
            do {
                let (items, snapshot) = try await listing()
                await SnapshotStore.shared.record(snapshot, for: identity.identifier)
                Log.enumeration.info("listed \(self.identity.destination.logDescription, privacy: .public) — \(items.count, privacy: .public) items")
                observer.didEnumerate(items)
                // No paging: `/api/files/list` answers a whole folder in one query, ordered by the
                // same index the dashboard uses. A folder large enough to need pages would need the
                // server to offer it first.
                observer.finishEnumerating(upTo: nil)
            } catch {
                Log.enumeration.error("listing \(self.identity.destination.logDescription, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
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
                Log.enumeration.info("\(self.identity.destination.logDescription, privacy: .public) was asked for changes from an anchor it no longer holds — asking for a full listing")
                observer.finishEnumeratingWithError(NSFileProviderError(.syncAnchorExpired))
                return
            }

            do {
                let (items, snapshot) = try await listing()

                // Anything new, and anything whose version moved. An unchanged item is left out
                // entirely — reporting it would have the system re-download bytes it already holds.
                let changed = items.filter { previous[$0.itemIdentifier.rawValue] != snapshot[$0.itemIdentifier.rawValue] }
                let deleted = previous.keys
                    .filter { snapshot[$0] == nil }
                    .map(NSFileProviderItemIdentifier.init(_:))

                await SnapshotStore.shared.record(snapshot, for: identity.identifier)

                Log.enumeration.info("changes in \(self.identity.destination.logDescription, privacy: .public) — \(changed.count, privacy: .public) updated, \(deleted.count, privacy: .public) deleted")
                if !deleted.isEmpty { observer.didDeleteItems(withIdentifiers: deleted) }
                if !changed.isEmpty { observer.didUpdate(changed) }
                observer.finishEnumeratingChanges(upTo: NSFileProviderSyncAnchor(SnapshotStore.signature(of: snapshot)), moreComing: false)
            } catch {
                Log.enumeration.error("changes in \(self.identity.destination.logDescription, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
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
    private func listing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
        let listing = try await api.list(identity.destination)

        // Both built in one pass, and the snapshot keyed off the items that were just built rather
        // than off identities worked out a second time: what an identifier is depends on which tree
        // the folder is in, and deriving it twice is how the two readings come to disagree. It also
        // keeps the two in step where a row is left out, which nothing walking them in parallel
        // afterwards would.
        var items: [FileProviderItem] = []
        var snapshot = SnapshotStore.Snapshot()

        // Everything a folder in the bin lists is inside a thrown-away subtree, and what may be done
        // to it says so. Learned once from the folder that was asked for rather than per row: only
        // the top of what was thrown away carries the mark, so no row down here shows it.
        let standing: FileProviderItem.Standing = listing.isTrashed ? .covered : .live

        let container = identity.destination.logDescription
        for folder in listing.folders {
            let item = FileProviderItem.folder(in: identity, remote: folder, standing: standing)
            guard nameable(item, in: container) else { continue }
            items.append(item)
            // The folder's own version has nothing to do with what is inside it — that is the
            // child's anchor, not this one's — so a rename is the only thing that shows here.
            snapshot[item.itemIdentifier.rawValue] = "\(folder.name)|\(folder.writable)"
        }
        for file in listing.files {
            let item = FileProviderItem.file(in: identity, remote: file, standing: standing)
            guard nameable(item, in: container) else { continue }
            items.append(item)
            snapshot[item.itemIdentifier.rawValue] = "\(file.version)|\(file.filename)"
        }

        return (items, snapshot)
    }
}

/// What has been thrown away out of the admin's own folder, under either of the two containers that
/// hold it.
///
/// One bin for the whole volume, which is what the framework offers and what the portal has: a
/// trashed row keeps the folder it was in, so nothing needs a bin per directory to know where a
/// thing came from. The classified tree contributes nothing — a document has no path to be put back
/// along, and deleting one is final there as it is in the dashboard.
///
/// What is listed is the top of each thing thrown away. A directory takes its contents with it, so
/// the bin offers the directory; the files inside it are still in the table, still under it, and
/// come back with it. That is also what the working set asks for in so many words — trashed items
/// belong in it, and the children of trashed directories do not.
///
/// The working set is the same listing because here it holds nothing else. It is the set the system
/// keeps knowledge of outside any open folder, and filling it with the tree would mean enumerating
/// every document of every client of every syndicate on a schedule nobody asked for — the cost of
/// indexing the building rather than of opening one drawer. The bin is the exception that argument
/// never covered: it is small, it is bounded by what one admin threw away, and the framework wants
/// it there. What it buys is knowledge the system keeps when nothing is looking — the bin is in
/// Spotlight, and the system stops learning about it only when someone opens the trash.
///
/// The same listing-and-diffing as a folder, for the same reason: the portal has no change feed, so
/// what has left the bin — put back, or purged — is only visible by comparing against what it held
/// last time. Each container keeps its own snapshot, because the system asks the two for changes
/// independently and from anchors of their own.
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

    func currentListing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
        try await listing()
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
                let (items, snapshot) = try await listing()
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
                let (items, snapshot) = try await listing()

                var changed = items.filter { previous[$0.itemIdentifier.rawValue] != snapshot[$0.itemIdentifier.rawValue] }
                var deleted = previous.keys
                    .filter { snapshot[$0] == nil }
                    .map(NSFileProviderItemIdentifier.init(_:))

                await SnapshotStore.shared.record(snapshot, for: container)

                // The trash's own enumerator answers for the bin alone. Only the working set carries
                // the rest of the volume, because only the working set is asked on the system's own
                // schedule — the trash is asked while somebody has it open, and the folders these
                // items belong to are never asked at all.
                if container == .workingSet {
                    let watched = await poller.pendingChanges()
                    changed += watched.updated
                    deleted += watched.deleted
                }

                Log.enumeration.info("\(self.describing, privacy: .public) — \(changed.count, privacy: .public) updated, \(deleted.count, privacy: .public) gone")
                if !deleted.isEmpty { observer.didDeleteItems(withIdentifiers: deleted) }
                if !changed.isEmpty { observer.didUpdate(changed) }
                observer.finishEnumeratingChanges(upTo: NSFileProviderSyncAnchor(SnapshotStore.signature(of: snapshot)), moreComing: false)
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

    private func listing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
        var items: [FileProviderItem] = []
        var snapshot = SnapshotStore.Snapshot()

        for entry in try await api.trashed() {
            let item = FileProviderItem.fileRow(entry)
            guard nameable(item, in: describing) else { continue }
            items.append(item)
            snapshot[item.itemIdentifier.rawValue] = "\(entry.version)|\(entry.filename)"
        }
        return (items, snapshot)
    }
}
