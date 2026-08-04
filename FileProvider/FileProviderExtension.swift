import FileProvider
import Foundation
import UniformTypeIdentifiers

/// The extension the system loads to service the mounted volume.
///
/// A *replicated* extension: macOS keeps its own record of the items and asks this class for
/// metadata, contents and changes, rather than the extension owning a folder on disk. That is the
/// right shape for a document store that is not a folder anywhere — the portal's tree is a set of
/// database views, and nothing about it maps onto a directory a provider could hand over.
final class FileProviderExtension: NSObject, NSFileProviderReplicatedExtension {

    private let api = HelmsleyAPI.shared
    private let signal: ChangeSignal
    private let poller: ChangePoller

    /// Held for as long as the extension is loaded, because it is what owns the `PKPushRegistry`:
    /// nothing else refers to it, and a registry nobody holds stops being the delegate's.
    private let push: PushRegistrar

    required init(domain: NSFileProviderDomain) {
        self.signal = ChangeSignal(domain: domain)
        self.poller = ChangePoller(signal: signal)
        // Registering here means it happens whenever anything touches the volume, which is as often
        // as this class is made — and the token the portal holds is refreshed by the same act.
        self.push = PushRegistrar(domain: domain, poller: poller)
        super.init()
        Log.provider.info("extension loaded for domain \(domain.identifier.rawValue, privacy: .public), portal \(Configuration.baseURL.absoluteString, privacy: .public)")
    }

    func invalidate() {}

    // MARK: - Metadata

    func item(
        for identifier: NSFileProviderItemIdentifier,
        request: NSFileProviderRequest,
        completionHandler: @escaping (NSFileProviderItem?, Error?) -> Void
    ) -> Progress {
        let progress = Progress(totalUnitCount: 1)

        guard let identity = ItemIdentity(identifier) else {
            completionHandler(nil, NSFileProviderError(.noSuchItem))
            return progress
        }

        Task {
            do {
                completionHandler(try await item(for: identity), nil)
            } catch {
                Log.provider.error("item(for: \(identity.path.logPath, privacy: .public)) failed: \(error.localizedDescription, privacy: .public)")
                completionHandler(nil, FileProviderError.translate(error))
            }
            progress.completedUnitCount = 1
        }
        return progress
    }

    /// One item, by identity.
    ///
    /// Anything in a file tree answers directly, folder or file, in the bin or not: it is a row with
    /// an id, and `/items/:id` says what it is and where it sits — including which mount, when it
    /// sits at the top of one.
    ///
    /// A document answers directly too — a document id is a document id whichever folder is showing
    /// it. A folder of the classified tree cannot: its name and whether it takes uploads are
    /// properties of how its parent lists it, and the portal has no endpoint that describes a filter
    /// in isolation. So it is found by listing the folder above it, which is a request the system has
    /// almost always just made anyway.
    ///
    /// One thing is checked rather than passed on: an item with no name aborts the process the
    /// moment the framework sees it, so what the server said is refused here instead of crashing the
    /// extension in the caller's own completion handler. `.noSuchItem` because that is what it is —
    /// a filesystem has no way to show something it cannot name — and because it is the answer that
    /// makes the system drop one it is already holding rather than ask for it again forever.
    private func item(for identity: ItemIdentity) async throws -> NSFileProviderItem {
        let item: FileProviderItem

        switch identity {
        case .root:
            item = FileProviderItem.root

        case .fileRow(let id):
            item = FileProviderItem.fileRow(try await api.item(id: id))

        case .file(let path, let documentID):
            let remote = try await api.document(id: documentID)
            item = FileProviderItem.file(in: container(of: path), remote: remote)

        case .folder(let path):
            guard let segment = path.last else { return FileProviderItem.root }
            let above = container(of: path.dropLast())
            let parent = try await api.list(above.destination)
            guard let remote = parent.folders.first(where: { $0.segment == segment }) else {
                throw NSFileProviderError(.noSuchItem)
            }
            item = FileProviderItem.folder(in: above, remote: remote)
        }

        guard item.isNameable else {
            Log.provider.error("\(identity.destination.logDescription, privacy: .public) has no name — refused rather than handed over")
            throw NSFileProviderError(.noSuchItem)
        }
        return item
    }

    /// The folder a path names, as an identity. Only ever used for the classified tree, whose
    /// folders are still addressed this way; the empty path is the mount point itself.
    private func container(of path: some Collection<String>) -> ItemIdentity {
        path.isEmpty ? .root : .folder(path: Array(path))
    }

    // MARK: - Contents

    func fetchContents(
        for itemIdentifier: NSFileProviderItemIdentifier,
        version requestedVersion: NSFileProviderItemVersion?,
        request: NSFileProviderRequest,
        completionHandler: @escaping (URL?, NSFileProviderItem?, Error?) -> Void
    ) -> Progress {
        let progress = Progress()

        // Either tree, since both keep their bytes behind the same redirect — and a trashed personal
        // file included, because something in the bin is still there to be looked at.
        guard let identity = ItemIdentity(itemIdentifier), let contentID = identity.deletableID else {
            completionHandler(nil, nil, NSFileProviderError(.noSuchItem))
            return progress
        }

        let work = Task {
            do {
                // Metadata first: the item handed back must describe the bytes handed back, and a
                // document replaced between the system's last listing and this fetch would
                // otherwise be delivered under the old version and never re-fetched.
                let item = try await self.item(for: identity)
                // The most expensive thing this extension does, and the only one whose cost the
                // user feels — worth a line each way, so a slow mount can be told from a slow link.
                Log.provider.info("fetching \(item.filename, privacy: .public) (\(item.documentSize??.int64Value ?? -1, privacy: .public) bytes)")
                let url = try await api.downloadContents(id: contentID, reporting: progress)
                Log.provider.info("fetched \(item.filename, privacy: .public)")
                completionHandler(url, item, nil)
            } catch {
                Log.provider.error("fetch of \(contentID, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                completionHandler(nil, nil, FileProviderError.translate(error))
            }
        }
        // The system cancels a fetch that stalls or takes too long, as well as relaying the user's
        // own cancellation, and expects the completion handler promptly either way. Cancelling the
        // progress already cancels the transfer through its child; this stops the surrounding work
        // too, so the answer comes back rather than waiting on a request nobody wants.
        progress.cancellationHandler = { work.cancel() }
        return progress
    }

    // MARK: - Writes

    func createItem(
        basedOn itemTemplate: NSFileProviderItem,
        fields: NSFileProviderItemFields,
        contents url: URL?,
        options: NSFileProviderCreateItemOptions = [],
        request: NSFileProviderRequest,
        completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void
    ) -> Progress {
        let progress = Progress()

        guard let parent = ItemIdentity(itemTemplate.parentItemIdentifier) else {
            completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
            return progress
        }

        if itemTemplate.contentType == .folder {
            // A folder is a row only in the two file trees. In the rest of the tree it is a filter
            // over `documents` that the directory spec defines, so there is nothing there to
            // create. Refused rather than silently ignored, so a new folder never sits in Finder
            // looking as though it exists.
            guard parent.isFileTree else {
                completionHandler(nil, [], false, FileProviderError.unsupported("Folders can only be made in your own folder or in Shared — the rest of the Helmsley tree is the portal's, and fixed."))
                return progress
            }
            return makeFolder(in: parent, named: itemTemplate.filename, progress: progress, completionHandler: completionHandler)
        }

        guard let contents = url else {
            completionHandler(nil, [], false, FileProviderError.unsupported("A file must have contents to be filed."))
            return progress
        }

        let destination = parent.destination
        let filename = itemTemplate.filename
        // The template's type is what Finder settled from the extension it is being filed under;
        // the server signs it into the upload URL, so it has to be decided before a byte is sent.
        let mime = itemTemplate.contentType?.preferredMIMEType

        let work = Task {
            do {
                let remote = try await api.upload(to: destination, filename: filename, mime: mime, fileURL: contents, reporting: progress)
                await signal.fire()
                // No pending fields and nothing still uploading: the document is filed by the time
                // this returns, because the finalise step is what created it.
                completionHandler(FileProviderItem.file(in: parent, remote: remote), [], false, nil)
            } catch {
                Log.provider.error("upload to \(destination.logDescription, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                completionHandler(nil, [], false, FileProviderError.translate(error))
            }
        }
        progress.cancellationHandler = { work.cancel() }
        return progress
    }

    /// The folder half of `createItem`.
    ///
    /// The name that comes back is not always the name asked for — a collision is numbered rather
    /// than refused, which is what a filesystem does — so the item handed to the system is built
    /// from the server's answer. Reporting the requested name instead would leave Finder showing
    /// one folder under two names until something re-listed it.
    private func makeFolder(
        in parent: ItemIdentity,
        named name: String,
        progress: Progress,
        completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void
    ) -> Progress {
        let work = Task {
            do {
                let remote = try await api.createFolder(in: parent.destination, name: name)
                await signal.fire()
                completionHandler(FileProviderItem.folder(in: parent, remote: remote), [], false, nil)
            } catch {
                Log.provider.error("creating a folder in \(parent.destination.logDescription, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                completionHandler(nil, [], false, FileProviderError.translate(error))
            }
        }
        progress.cancellationHandler = { work.cancel() }
        return progress
    }

    func modifyItem(
        _ item: NSFileProviderItem,
        baseVersion version: NSFileProviderItemVersion,
        changedFields: NSFileProviderItemFields,
        contents newContents: URL?,
        options: NSFileProviderModifyItemOptions = [],
        request: NSFileProviderRequest,
        completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void
    ) -> Progress {
        let progress = Progress(totalUnitCount: 1)

        guard let identity = ItemIdentity(item.itemIdentifier) else {
            completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
            return progress
        }

        // Rewriting in place has no endpoint behind it anywhere in the tree, for the reason
        // `.allowsWriting` is never offered: a file's bytes are not something either table lets you
        // replace under the same row.
        guard !changedFields.contains(.contents) else {
            completionHandler(nil, [], false, FileProviderError.unsupported("Helmsley files cannot be edited in place. Save a copy and file that instead."))
            return progress
        }

        // Renaming and moving reach the server only inside the file trees. A document's
        // title, its type and its links are the filing an admin chose in the portal, which refuses
        // to refile some of them outright (a compliance document, for one) — and it has no path to
        // move one along in any case. Anything else Finder wants to record — a tag, a last-used
        // date — is local, so it is accepted unchanged.
        let relocation = changedFields.intersection([.filename, .parentItemIdentifier])
        guard let itemID = identity.fileRowID else {
            guard relocation.isEmpty else {
                completionHandler(nil, [], false, FileProviderError.unsupported(identity.isFileTree
                    ? "A folder the portal defines cannot be renamed or moved — your own is named after you, and Shared is named in the directory itself."
                    : "Helmsley documents cannot be renamed or moved. Change the filing in the portal instead."))
                return progress
            }
            return acknowledge(identity, progress: progress, completionHandler: completionHandler)
        }
        guard !relocation.isEmpty else {
            return acknowledge(identity, progress: progress, completionHandler: completionHandler)
        }

        // The identifier the answer comes back under. Everything the system has enumerated since
        // file rows became id-addressed already is one; anything still held by path becomes one
        // here, once, and the folder re-syncs around it.
        let settled = identity.asFileRow
        let target = item.parentItemIdentifier
        let work = Task {
            do {
                // Where the item is *now*, asked of the server rather than read off the item. The
                // one handed over describes what it should become — its parentItemIdentifier is the
                // destination — so the folder being left behind has to be looked up, and it is what
                // tells a restore apart from an ordinary move.
                let source = FileProviderItem.fileRow(try await api.item(id: itemID)).parentItemIdentifier

                if relocation.contains(.parentItemIdentifier) {
                    try await self.reparent(itemID, from: source, to: target)
                }
                // Moved first, then renamed — because the two settle a name clash differently. A
                // move numbers what it cannot keep, so doing it first parks the item in the target
                // under whatever name is free; the rename then asks for the name the user actually
                // typed, in the folder where it has to be free, and says so if it is taken. The
                // other order would refuse a rename over a clash in the folder being left behind.
                if relocation.contains(.filename) {
                    try await api.rename(id: itemID, to: item.filename)
                }

                let updated = try await self.item(for: settled)
                await signal.fire()
                completionHandler(updated, [], false, nil)
            } catch {
                Log.provider.error("modifying \(itemID, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                completionHandler(nil, [], false, FileProviderError.translate(error))
            }
            progress.completedUnitCount = 1
        }
        progress.cancellationHandler = { work.cancel() }
        return progress
    }

    /// Moving an item between containers, which is three different operations depending on which of
    /// them is the trash.
    ///
    /// The framework has no separate verb for throwing something away: it reparents the item into
    /// `.trashContainer` and expects the item to come back saying it is trashed. Taking it out again
    /// is the same move in reverse — an undo, or a drag out of the bin — and the move names where it
    /// is going, so nothing here has to remember where the item was. Finder's own Put Back never
    /// sends one and cannot be made to; the trash section of the README says why.
    private func reparent(
        _ itemID: String,
        from source: NSFileProviderItemIdentifier,
        to target: NSFileProviderItemIdentifier
    ) async throws {
        if target == .trashContainer {
            return try await api.trash(id: itemID)
        }

        // Either file tree is a destination, including the other one: dragging something into
        // Shared is how it gets there, and dragging it back out is the undo. The server moves the
        // whole subtree across in one go.
        guard let destination = ItemIdentity(target), destination.isFileTree else {
            throw FileProviderError.unsupported("An item can only be moved within your own folder or Shared.")
        }
        if source == .trashContainer {
            // Restored and relocated in one step, because that is what dragging something out of the
            // trash is. A restore to where it came from sends the same request naming that folder.
            _ = try await api.restore(id: itemID, to: destination.destination)
        } else {
            try await api.move(id: itemID, to: destination.destination)
        }
    }

    /// Answers a modification that needs nothing of the server with the item as it stands — the
    /// tags, the labels and the dates Finder keeps on its own side and only wants recorded.
    private func acknowledge(
        _ identity: ItemIdentity,
        progress: Progress,
        completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void
    ) -> Progress {
        Task {
            do {
                completionHandler(try await self.item(for: identity), [], false, nil)
            } catch {
                completionHandler(nil, [], false, FileProviderError.translate(error))
            }
            progress.completedUnitCount = 1
        }
        return progress
    }

    func deleteItem(
        identifier: NSFileProviderItemIdentifier,
        baseVersion version: NSFileProviderItemVersion,
        options: NSFileProviderDeleteItemOptions = [],
        request: NSFileProviderRequest,
        completionHandler: @escaping (Error?) -> Void
    ) -> Progress {
        let progress = Progress(totalUnitCount: 1)

        // A file, or a folder in the admin's own branch — the only folders that are rows rather than
        // filters. The classified tree's folders are the directory spec's, so there is nothing there
        // for a delete to remove.
        //
        // This is the permanent one. A Finder delete on something that can be trashed arrives as a
        // reparent into the bin instead (modifyItem), so what reaches here is Delete Immediately, an
        // emptied trash, or a document — for which there was never anything else.
        guard let identity = ItemIdentity(identifier), let itemID = identity.deletableID else {
            completionHandler(FileProviderError.unsupported("Only files and your own folders can be deleted — the rest of the tree is the portal's structure."))
            return progress
        }

        Task {
            do {
                // Nothing is looked up first any more. This used to ask the server which folder the
                // item sat in, before the delete made that unanswerable, so the signal could name it
                // — and naming it was worth nothing: a replicated extension's signals are only ever
                // about the working set, and what the folder lost is worked out when the system comes
                // asking for the working set's changes.
                //
                // Deletes the document, not this folder's view of it: a row listed in several
                // folders disappears from all of them, which is what deleting the file means. A
                // folder takes what is under it — including anything already in the bin from inside
                // it, which is gone either way once the folder holding it is.
                try await api.delete(id: itemID)
                await signal.fire()
                completionHandler(nil)
            } catch let error where (error as? APIError)?.isNotFound == true {
                // Already gone — which is the outcome asked for, so it is not a failure.
                completionHandler(nil)
            } catch {
                completionHandler(FileProviderError.translate(error))
            }
            progress.completedUnitCount = 1
        }
        return progress
    }

    // MARK: - Enumeration

    func enumerator(
        for containerItemIdentifier: NSFileProviderItemIdentifier,
        request: NSFileProviderRequest
    ) throws -> NSFileProviderEnumerator {
        Log.provider.info("enumerator requested for \(containerItemIdentifier.rawValue, privacy: .public)")
        // One bin for the volume, holding what has been thrown out of the admin's own folder. The
        // classified tree puts nothing in it — a document has no path to be put back along, and
        // deleting one there is final, as it is in the dashboard.
        //
        // The working set answers with the same listing, and with nothing else: the framework asks
        // for trashed items there by name, and holding the rest of this tree in it would mean
        // indexing every document of every client of every syndicate.
        if containerItemIdentifier == .workingSet || containerItemIdentifier == .trashContainer {
            return BinEnumerator(container: containerItemIdentifier, watchedBy: poller)
        }

        guard let identity = ItemIdentity(containerItemIdentifier), !isDocument(identity) else {
            throw NSFileProviderError(.noSuchItem)
        }
        return FolderEnumerator(identity: identity, watchedBy: poller)
    }

    /// A document is never a container. A personal identity may be either, and the enumerator finds
    /// out by asking — listing a file answers 404, which is the right answer to enumerating one.
    private func isDocument(_ identity: ItemIdentity) -> Bool {
        if case .file = identity { return true }
        return false
    }

}

/// Turns what this app's own layers throw into what the file provider framework understands.
///
/// The distinction that matters is between "this item is gone" and "this did not work just now":
/// the first makes the system drop the item, the second makes it retry. Anything unrecognised is
/// left alone rather than flattened into one of the two, since guessing wrong in either direction
/// is worse than an opaque error.
enum FileProviderError {

    static func translate(_ error: Error) -> Error {
        switch error {
        case is CancellationError:
            // What the framework asks for by name when a returned progress is cancelled. Reporting
            // anything else — this used to say `.serverUnreachable` — turns a cancellation into a
            // failure the system retries, which is the opposite of what was asked.
            return CocoaError(.userCancelled)
        case let url as URLError where url.code == .cancelled:
            return CocoaError(.userCancelled)
        case OAuthError.notAuthenticated:
            // What puts "Sign in" next to the volume in Finder rather than an error nobody can act on.
            return NSFileProviderError(.notAuthenticated)
        case let api as APIError where api.isNotFound:
            return NSFileProviderError(.noSuchItem)
        default:
            return error
        }
    }

    /// An operation the portal has no equivalent for. `NSFeatureUnsupportedError` is what makes
    /// Finder say so and stop, rather than retrying something that will never begin to work.
    static func unsupported(_ message: String) -> Error {
        NSError(
            domain: NSCocoaErrorDomain,
            code: NSFeatureUnsupportedError,
            userInfo: [NSLocalizedDescriptionKey: message]
        )
    }
}
