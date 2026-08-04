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
/// content hash, so an edit, an addition and a removal are all visible in the diff. What it cannot
/// be is instantaneous — a change made in the dashboard shows up when the system next asks, which
/// it does on refresh, on navigation, and whenever this extension signals it after a write of its
/// own.
final class FolderEnumerator: NSObject, NSFileProviderEnumerator {

    private let identity: ItemIdentity
    private let api = HelmsleyAPI.shared

    init(identity: ItemIdentity) {
        self.identity = identity
        super.init()
    }

    func invalidate() {}

    // MARK: - Full enumeration

    func enumerateItems(for observer: NSFileProviderEnumerationObserver, startingAt page: NSFileProviderPage) {
        Task {
            do {
                let (items, snapshot) = try await listing()
                await SnapshotStore.shared.store(snapshot, for: identity.identifier)
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
            do {
                let (items, snapshot) = try await listing()
                let previous = await SnapshotStore.shared.snapshot(for: identity.identifier)

                // Anything new, and anything whose version moved. An unchanged item is left out
                // entirely — reporting it would have the system re-download bytes it already holds.
                let changed = items.filter { previous[$0.itemIdentifier.rawValue] != snapshot[$0.itemIdentifier.rawValue] }
                let deleted = previous.keys
                    .filter { snapshot[$0] == nil }
                    .map(NSFileProviderItemIdentifier.init(_:))

                await SnapshotStore.shared.store(snapshot, for: identity.identifier)

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
            let snapshot = await SnapshotStore.shared.snapshot(for: identity.identifier)
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

/// What has been thrown away out of the admin's own folder.
///
/// One container for the whole volume, which is what the framework offers and what the portal has:
/// a trashed row keeps the folder it was in, so nothing needs a bin per directory to know where a
/// thing came from. The classified tree contributes nothing — a document has no path to be put back
/// along, and deleting one is final there as it is in the dashboard.
///
/// What is listed is the top of each thing thrown away. A directory takes its contents with it, so
/// the bin offers the directory; the files inside it are still in the table, still under it, and
/// come back with it.
///
/// The same listing-and-diffing as a folder, for the same reason: the portal has no change feed, so
/// what has left the bin — put back, or purged — is only visible by comparing against what it held
/// last time.
final class TrashEnumerator: NSObject, NSFileProviderEnumerator {

    private let api = HelmsleyAPI.shared

    func invalidate() {}

    func enumerateItems(for observer: NSFileProviderEnumerationObserver, startingAt page: NSFileProviderPage) {
        Task {
            do {
                let (items, snapshot) = try await listing()
                await SnapshotStore.shared.store(snapshot, for: .trashContainer)
                Log.enumeration.info("trash — \(items.count, privacy: .public) items")
                observer.didEnumerate(items)
                observer.finishEnumerating(upTo: nil)
            } catch {
                Log.enumeration.error("listing the trash failed: \(error.localizedDescription, privacy: .public)")
                observer.finishEnumeratingWithError(FileProviderError.translate(error))
            }
        }
    }

    func enumerateChanges(for observer: NSFileProviderChangeObserver, from anchor: NSFileProviderSyncAnchor) {
        Task {
            do {
                let (items, snapshot) = try await listing()
                let previous = await SnapshotStore.shared.snapshot(for: .trashContainer)

                let changed = items.filter { previous[$0.itemIdentifier.rawValue] != snapshot[$0.itemIdentifier.rawValue] }
                let deleted = previous.keys
                    .filter { snapshot[$0] == nil }
                    .map(NSFileProviderItemIdentifier.init(_:))

                await SnapshotStore.shared.store(snapshot, for: .trashContainer)

                Log.enumeration.info("trash — \(changed.count, privacy: .public) updated, \(deleted.count, privacy: .public) gone")
                if !deleted.isEmpty { observer.didDeleteItems(withIdentifiers: deleted) }
                if !changed.isEmpty { observer.didUpdate(changed) }
                observer.finishEnumeratingChanges(upTo: NSFileProviderSyncAnchor(SnapshotStore.signature(of: snapshot)), moreComing: false)
            } catch {
                Log.enumeration.error("changes in the trash failed: \(error.localizedDescription, privacy: .public)")
                observer.finishEnumeratingWithError(FileProviderError.translate(error))
            }
        }
    }

    func currentSyncAnchor(completionHandler: @escaping (NSFileProviderSyncAnchor?) -> Void) {
        Task {
            let snapshot = await SnapshotStore.shared.snapshot(for: .trashContainer)
            completionHandler(NSFileProviderSyncAnchor(SnapshotStore.signature(of: snapshot)))
        }
    }

    private func listing() async throws -> ([NSFileProviderItem], SnapshotStore.Snapshot) {
        var items: [FileProviderItem] = []
        var snapshot = SnapshotStore.Snapshot()

        for entry in try await api.trashed() {
            let item = FileProviderItem.personal(entry)
            guard nameable(item, in: "the trash") else { continue }
            items.append(item)
            snapshot[item.itemIdentifier.rawValue] = "\(entry.version)|\(entry.filename)"
        }
        return (items, snapshot)
    }
}

/// The working set is the set of items the system keeps knowledge of outside any open folder — what
/// backs Spotlight, recents, and offline availability.
///
/// Deliberately empty. Filling it would mean enumerating the whole tree, which for this portal is
/// every document of every client of every syndicate, on a schedule the user never asked for. Folders
/// enumerate on demand instead, which is what a filing cabinet of this shape wants: the cost of
/// opening one drawer, not of indexing the building.
final class WorkingSetEnumerator: NSObject, NSFileProviderEnumerator {

    func invalidate() {}

    func enumerateItems(for observer: NSFileProviderEnumerationObserver, startingAt page: NSFileProviderPage) {
        observer.didEnumerate([])
        observer.finishEnumerating(upTo: nil)
    }

    func enumerateChanges(for observer: NSFileProviderChangeObserver, from anchor: NSFileProviderSyncAnchor) {
        observer.finishEnumeratingChanges(upTo: anchor, moreComing: false)
    }

    func currentSyncAnchor(completionHandler: @escaping (NSFileProviderSyncAnchor?) -> Void) {
        completionHandler(NSFileProviderSyncAnchor(Data("working-set".utf8)))
    }
}
