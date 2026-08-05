import FileProvider
import Foundation
import UniformTypeIdentifiers

/// The extension the system loads to service the mounted volume.
///
/// A *replicated* extension: macOS keeps its own record of the items and asks this class for
/// metadata, contents and changes, rather than the extension owning a folder on disk. That is the
/// right shape for a store that is not a folder anywhere — the portal's tree is rows in a table and
/// bytes in a bucket, and nothing about it maps onto a directory a provider could hand over.
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
                Log.provider.error("item(for: \(identity.logDescription, privacy: .public)) failed: \(error.localizedDescription, privacy: .public)")
                completionHandler(nil, FileProviderError.translate(error))
            }
            progress.completedUnitCount = 1
        }
        return progress
    }

    /// One item, by identity.
    ///
    /// Everything below the mount point answers directly, folder or file, in the bin or not: it is a
    /// row with an id, and `/items/:id` says what it is, where it sits, and what may be done to it.
    /// One request, whatever was asked for — which is the whole of what one tree bought.
    ///
    /// A nameless row is refused here rather than passed on, since handing one over aborts the
    /// process (`FileProviderItem.isNameable`). `.noSuchItem` because that is what it is, and
    /// because it makes the system drop the item rather than ask for it again forever.
    private func item(for identity: ItemIdentity) async throws -> NSFileProviderItem {
        let item: FileProviderItem

        switch identity {
        case .root:
            item = FileProviderItem.root
        case .node(let id):
            item = FileProviderItem.item(try await api.item(id: id), as: identity)
        }

        guard item.isNameable else {
            Log.provider.error("\(identity.logDescription, privacy: .public) has no name — refused rather than handed over")
            throw NSFileProviderError(.noSuchItem)
        }
        return item
    }

    // MARK: - Contents

    func fetchContents(
        for itemIdentifier: NSFileProviderItemIdentifier,
        version requestedVersion: NSFileProviderItemVersion?,
        request: NSFileProviderRequest,
        completionHandler: @escaping (URL?, NSFileProviderItem?, Error?) -> Void
    ) -> Progress {
        let progress = Progress()

        // A trashed file included, because something in the bin is still there to be looked at. Only
        // the mount point has no id and no bytes behind it.
        guard let identity = ItemIdentity(itemIdentifier), let contentID = identity.nodeID else {
            completionHandler(nil, nil, NSFileProviderError(.noSuchItem))
            return progress
        }

        let work = Task {
            do {
                // Metadata first: the item handed back must describe the bytes handed back, and a
                // file replaced between the system's last listing and this fetch would otherwise be
                // delivered under the old version and never re-fetched.
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

        // A folder is a row wherever it is in this tree, so the server can always be asked for one.
        // Whether it will take it is the destination's own answer, which the listing already carried
        // — a folder that refuses one never offered `.allowsAddingSubItems`, so Finder does not get
        // this far, and a race against somebody else's change is refused in a sentence worth showing.
        if itemTemplate.contentType == .folder {
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
                // No pending fields and nothing still uploading: the file is filed by the time this
                // returns, because the finalise step is what created it.
                //
                // The row is new and in a folder that just took it, so everything is open to it —
                // which is what the server answers, and what is assumed of a portal that does not.
                completionHandler(
                    FileProviderItem.file(
                        in: parent,
                        remote: remote,
                        permissions: remote.permissions ?? Permissions(assumedFrom: true)
                    ),
                    [], false, nil
                )
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
                // A folder somebody just made in a folder that took it: theirs to rename, move,
                // fill and throw away, which is what the server says of one and what is assumed of
                // a portal that does not say.
                completionHandler(
                    FileProviderItem.folder(
                        in: parent,
                        remote: remote,
                        permissions: remote.permissions ?? Permissions(assumedFrom: true)
                    ),
                    [], false, nil
                )
            } catch {
                Log.provider.error("creating a folder in \(parent.logDescription, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
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

        // A save. The framework hands over the whole file rather than a patch, and the portal takes
        // it as a replacement for the row's bytes — so what arrives here is the new contents on
        // disk, and what goes back is the row it landed on.
        let rewriting = changedFields.contains(.contents)
        if rewriting && newContents == nil {
            completionHandler(nil, [], false, FileProviderError.unsupported("A save has to arrive with the file's new contents."))
            return progress
        }

        // Anything else Finder wants to record — a tag, a last-used date — is local, so it is
        // accepted unchanged. The mount point is not a row and has nothing to change.
        let relocation = changedFields.intersection([.filename, .parentItemIdentifier])
        guard let itemID = identity.nodeID, rewriting || !relocation.isEmpty else {
            return acknowledge(identity, progress: progress, completionHandler: completionHandler)
        }

        let target = item.parentItemIdentifier
        let work = Task {
            do {
                if relocation.contains(.parentItemIdentifier) {
                    // Whether the item is in the bin *now*, asked of the server rather than read off
                    // the item: the one handed over describes what it should become, so where it is
                    // leaving from is not in it — and that is what tells a restore from a move.
                    let wasTrashed = try await api.item(id: itemID).isTrashed
                    try await self.reparent(itemID, fromTrash: wasTrashed, to: target)
                }
                // Moved first, then renamed — because the two settle a name clash differently. A
                // move numbers what it cannot keep, so doing it first parks the item in the target
                // under whatever name is free; the rename then asks for the name the user actually
                // typed, in the folder where it has to be free, and says so if it is taken. The
                // other order would refuse a rename over a clash in the folder being left behind.
                if relocation.contains(.filename) {
                    try await api.rename(id: itemID, to: item.filename)
                }

                // The bytes last, so a save that also moved the file writes them where it ended up
                // — and so the row's Date Modified is the save's, which is the moment the user will
                // recognise. The type is the item's own: a save cannot rename anything, so it is
                // the one the name already implied.
                if rewriting, let contents = newContents {
                    Log.provider.info("saving \(item.filename, privacy: .public)")
                    _ = try await api.replaceContents(
                        id: itemID,
                        mime: item.contentType?.preferredMIMEType,
                        fileURL: contents,
                        reporting: progress
                    )
                }

                let updated = try await self.item(for: identity)
                await signal.fire()
                completionHandler(updated, [], false, nil)
            } catch {
                Log.provider.error("modifying \(itemID, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                completionHandler(nil, [], false, FileProviderError.translate(error))
            }
            // Only where nothing was transferred. A save hands this progress to the upload, which
            // takes over its units and completes them as the bytes go; counting a second unit on
            // top of that would report a transfer as more than finished.
            if !rewriting { progress.completedUnitCount = 1 }
        }
        progress.cancellationHandler = { work.cancel() }
        return progress
    }

    /// Moving an item between containers, which is three different operations depending on which of
    /// them is the trash.
    ///
    /// The framework has no separate verb for throwing something away: it reparents the item into
    /// `.trashContainer` and expects the item to come back saying it is trashed. Taking it out again
    /// is the same move in reverse — an undo, or a drag out of the bin. Finder's own Put Back never
    /// sends one and cannot be made to; the trash section of the README says why.
    private func reparent(
        _ itemID: String,
        fromTrash: Bool,
        to target: NSFileProviderItemIdentifier
    ) async throws {
        if target == .trashContainer {
            return try await api.trash(id: itemID)
        }

        // Any folder of the tree is a destination, since all of them are rows — dragging something
        // from a client's folder into Shared is a move like any other, and the server takes the
        // whole subtree across in one go. Whether this particular folder will have it is the
        // server's to answer; what arrives here is only an identifier that has to name one.
        guard let destination = ItemIdentity(target) else {
            throw FileProviderError.unsupported("That is not a folder of the Helmsley tree.")
        }
        if fromTrash {
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

        // An identifier this volume no longer mints is one the system should drop, not one to
        // explain — anything left over from the two-tree era arrives here as `.noSuchItem`.
        guard let identity = ItemIdentity(identifier) else {
            completionHandler(NSFileProviderError(.noSuchItem))
            return progress
        }
        // Anything but the mount point, which is not a row and so has nothing to remove.
        //
        // This is the permanent delete. A Finder delete on something that can be trashed arrives as
        // a reparent into the bin instead (modifyItem), so what reaches here is Delete Immediately
        // or an emptied trash.
        guard let itemID = identity.nodeID else {
            completionHandler(FileProviderError.unsupported("The Helmsley volume itself cannot be deleted."))
            return progress
        }

        Task {
            do {
                // Nothing is looked up first: a replicated extension's signals are only ever about
                // the working set, so naming the folder the item sat in would buy nothing — what
                // the folder lost is worked out when the system comes asking for those changes.
                //
                // A folder takes what is under it, including anything already in the bin from
                // inside it, which is gone either way once the folder holding it is.
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
        // One bin for the volume, over the whole tree — as the framework offers and the portal has.
        //
        // The working set answers with the same listing, and with nothing else: the framework asks
        // for trashed items there by name, and holding the rest of this tree in it would mean
        // indexing every file of every client of every syndicate.
        if containerItemIdentifier == .workingSet || containerItemIdentifier == .trashContainer {
            return BinEnumerator(container: containerItemIdentifier, watchedBy: poller)
        }

        // Whether the identifier names a folder rather than a file is not asked here: an identifier
        // is a row id and nothing about it says which it is. The enumerator finds out by listing —
        // the server answers 404 for a file, which is the right answer to enumerating one.
        guard let identity = ItemIdentity(containerItemIdentifier) else {
            throw NSFileProviderError(.noSuchItem)
        }
        return FolderEnumerator(identity: identity, watchedBy: poller)
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
