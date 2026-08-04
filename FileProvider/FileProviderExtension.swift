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

    private let domain: NSFileProviderDomain
    private let api = HelmsleyAPI.shared

    required init(domain: NSFileProviderDomain) {
        self.domain = domain
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
    /// Anything in the admin's own tree answers directly, folder or file, in the bin or not: it is a
    /// row with an id, and `/items/:id` says what it is and where it sits.
    ///
    /// A document answers directly too — a document id is a document id whichever folder is showing
    /// it. A folder of the classified tree cannot: its name and whether it takes uploads are
    /// properties of how its parent lists it, and the portal has no endpoint that describes a filter
    /// in isolation. So it is found by listing the folder above it, which is a request the system has
    /// almost always just made anyway.
    private func item(for identity: ItemIdentity) async throws -> NSFileProviderItem {
        switch identity {
        case .root:
            return FileProviderItem.root

        case .personal(let id):
            return FileProviderItem.personal(try await api.item(id: id))

        case .file(let path, let documentID):
            let remote = try await api.document(id: documentID)
            return FileProviderItem.file(in: container(of: path), remote: remote)

        case .folder(let path):
            guard let segment = path.last else { return FileProviderItem.root }
            let above = container(of: path.dropLast())
            let parent = try await api.list(above.destination)
            guard let remote = parent.folders.first(where: { $0.segment == segment }) else {
                throw NSFileProviderError(.noSuchItem)
            }
            return FileProviderItem.folder(in: above, remote: remote)
        }
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
            // A folder is a row only in the admin's own branch. In the rest of the tree it is a
            // filter over `documents` that the directory spec defines, so there is nothing there to
            // create. Refused rather than silently ignored, so a new folder never sits in Finder
            // looking as though it exists.
            guard parent.isPersonal else {
                completionHandler(nil, [], false, FileProviderError.unsupported("Folders can only be made in your own folder — the rest of the Helmsley tree is the portal's, and fixed."))
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
                await signalChange(at: itemTemplate.parentItemIdentifier)
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
                await signalChange(at: parent.identifier)
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

        // Renaming and moving reach the server only inside the admin's own folder. A document's
        // title, its type and its links are the filing an admin chose in the portal, which refuses
        // to refile some of them outright (a compliance document, for one) — and it has no path to
        // move one along in any case. Anything else Finder wants to record — a tag, a last-used
        // date — is local, so it is accepted unchanged.
        let relocation = changedFields.intersection([.filename, .parentItemIdentifier])
        guard let itemID = identity.personalItemID else {
            guard relocation.isEmpty else {
                completionHandler(nil, [], false, FileProviderError.unsupported(identity.isPersonal
                    ? "Your own folder is named after you and follows your name — it cannot be renamed or moved."
                    : "Helmsley documents cannot be renamed or moved. Change the filing in the portal instead."))
                return progress
            }
            return acknowledge(identity, progress: progress, completionHandler: completionHandler)
        }
        guard !relocation.isEmpty else {
            return acknowledge(identity, progress: progress, completionHandler: completionHandler)
        }

        // The identifier the answer comes back under. Everything the system has enumerated since
        // personal items became id-addressed already is one; anything still held by path becomes one
        // here, once, and the folder re-syncs around it.
        let settled = identity.asPersonal
        let target = item.parentItemIdentifier
        let work = Task {
            do {
                // Where the item is *now*, asked of the server rather than read off the item. The
                // one handed over describes what it should become — its parentItemIdentifier is the
                // destination — so the folder being left behind has to be looked up, and it is what
                // tells a put-back apart from an ordinary move.
                let source = FileProviderItem.personal(try await api.item(id: itemID)).parentItemIdentifier

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
                await signalChange(at: source)
                if updated.parentItemIdentifier != source { await signalChange(at: updated.parentItemIdentifier) }
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
    /// `.trashContainer` and expects the item to come back saying it is trashed. Putting it back is
    /// the same move in reverse, and Finder's Put Back names the folder it came from — which is the
    /// folder the row never stopped recording, so the two agree without this having to remember
    /// anything.
    private func reparent(
        _ itemID: String,
        from source: NSFileProviderItemIdentifier,
        to target: NSFileProviderItemIdentifier
    ) async throws {
        if target == .trashContainer {
            return try await api.trash(id: itemID)
        }

        guard let destination = ItemIdentity(target), destination.isPersonal else {
            throw FileProviderError.unsupported("An item can only be moved within your own folder.")
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
                // Which listing loses an item, worked out before there is nothing left to ask. A
                // personal item's identifier does not say where it sits, and after the delete
                // neither does the server — so it is looked up first, and best-effort: signalling is
                // what makes Finder notice at once rather than at the next thing that asks, so
                // failing to work it out is a slower refresh and not a wrong one.
                let container = await self.containerOf(identity)

                // Deletes the document, not this folder's view of it: a row listed in several
                // folders disappears from all of them, which is what deleting the file means. A
                // folder takes what is under it — including anything already in the bin from inside
                // it, which is gone either way once the folder holding it is.
                try await api.delete(id: itemID)
                if let container { await signalChange(at: container) }
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
        if containerItemIdentifier == .workingSet { return WorkingSetEnumerator() }
        // One bin for the volume, holding what has been thrown out of the admin's own folder. The
        // classified tree puts nothing in it — a document has no path to be put back along, and
        // deleting one there is final, as it is in the dashboard.
        if containerItemIdentifier == .trashContainer { return TrashEnumerator() }

        guard let identity = ItemIdentity(containerItemIdentifier), !isDocument(identity) else {
            throw NSFileProviderError(.noSuchItem)
        }
        return FolderEnumerator(identity: identity)
    }

    /// A document is never a container. A personal identity may be either, and the enumerator finds
    /// out by asking — listing a file answers 404, which is the right answer to enumerating one.
    private func isDocument(_ identity: ItemIdentity) -> Bool {
        if case .file = identity { return true }
        return false
    }

    /// The container currently listing an item, or nil where it cannot be worked out.
    ///
    /// A path-based identity carries its own answer. A personal one does not — that is the point of
    /// it — so the server is asked, and asked before whatever is about to change it.
    private func containerOf(_ identity: ItemIdentity) async -> NSFileProviderItemIdentifier? {
        guard case .personal(let id) = identity else { return identity.parentIdentifier }
        guard let remote = try? await api.item(id: id) else { return nil }
        return FileProviderItem.personal(remote).parentItemIdentifier
    }

    /// Tells the system a folder's contents moved under it, so Finder reflects an upload or a
    /// delete at once instead of at the next time something happens to ask.
    private func signalChange(at container: NSFileProviderItemIdentifier) async {
        try? await NSFileProviderManager(for: domain)?.signalEnumerator(for: container)
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
