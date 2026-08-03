import FileProvider
import Foundation

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
                Log.enumeration.info("listed \(self.identity.path.logPath, privacy: .public) — \(items.count, privacy: .public) items")
                observer.didEnumerate(items)
                // No paging: `/api/files/list` answers a whole folder in one query, ordered by the
                // same index the dashboard uses. A folder large enough to need pages would need the
                // server to offer it first.
                observer.finishEnumerating(upTo: nil)
            } catch {
                Log.enumeration.error("listing \(self.identity.path.logPath, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
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

                Log.enumeration.info("changes in \(self.identity.path.logPath, privacy: .public) — \(changed.count, privacy: .public) updated, \(deleted.count, privacy: .public) deleted")
                if !deleted.isEmpty { observer.didDeleteItems(withIdentifiers: deleted) }
                if !changed.isEmpty { observer.didUpdate(changed) }
                observer.finishEnumeratingChanges(upTo: NSFileProviderSyncAnchor(SnapshotStore.signature(of: snapshot)), moreComing: false)
            } catch {
                Log.enumeration.error("changes in \(self.identity.path.logPath, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
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
        let path = identity.path
        let listing = try await api.list(path: path)

        let folders = listing.folders.map { FileProviderItem.folder(path: path + [$0.segment], remote: $0) }
        let files = listing.files.map { FileProviderItem.file(path: path, remote: $0) }
        let items: [FileProviderItem] = folders + files

        var snapshot = SnapshotStore.Snapshot()
        for folder in listing.folders {
            let identifier = ItemIdentity.folder(path: path + [folder.segment]).identifier
            // The folder's own version has nothing to do with what is inside it — that is the
            // child's anchor, not this one's — so a rename is the only thing that shows here.
            snapshot[identifier.rawValue] = "\(folder.name)|\(folder.writable)"
        }
        for file in listing.files {
            snapshot[ItemIdentity.file(path: path, documentID: file.id).identifier.rawValue] = "\(file.version)|\(file.filename)"
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
