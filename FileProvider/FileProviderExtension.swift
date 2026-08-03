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
    /// A file can be looked up directly — a document id is a document id whichever folder is
    /// showing it. A folder cannot: its name and whether it takes uploads are properties of how its
    /// parent lists it, and the portal's tree has no endpoint that describes a folder in isolation.
    /// So a folder is found by listing the folder above it, which is a request the system has
    /// almost always just made anyway.
    private func item(for identity: ItemIdentity) async throws -> NSFileProviderItem {
        switch identity {
        case .root:
            return FileProviderItem.root

        case .file(let path, let documentID):
            return FileProviderItem.file(path: path, remote: try await api.document(id: documentID))

        case .folder(let path):
            guard let segment = path.last else { return FileProviderItem.root }
            let parent = try await api.list(path: Array(path.dropLast()))
            guard let remote = parent.folders.first(where: { $0.segment == segment }) else {
                throw NSFileProviderError(.noSuchItem)
            }
            return FileProviderItem.folder(path: path, remote: remote)
        }
    }

    // MARK: - Contents

    func fetchContents(
        for itemIdentifier: NSFileProviderItemIdentifier,
        version requestedVersion: NSFileProviderItemVersion?,
        request: NSFileProviderRequest,
        completionHandler: @escaping (URL?, NSFileProviderItem?, Error?) -> Void
    ) -> Progress {
        let progress = Progress()

        guard let identity = ItemIdentity(itemIdentifier),
              case .file(let path, let documentID) = identity else {
            completionHandler(nil, nil, NSFileProviderError(.noSuchItem))
            return progress
        }

        let work = Task {
            do {
                // Metadata first: the item handed back must describe the bytes handed back, and a
                // document replaced between the system's last listing and this fetch would
                // otherwise be delivered under the old version and never re-fetched.
                let remote = try await api.document(id: documentID)
                // The most expensive thing this extension does, and the only one whose cost the
                // user feels — worth a line each way, so a slow mount can be told from a slow link.
                Log.provider.info("fetching \(remote.filename, privacy: .public) (\(remote.size ?? -1, privacy: .public) bytes)")
                let url = try await api.downloadContents(id: documentID, reporting: progress)
                Log.provider.info("fetched \(remote.filename, privacy: .public)")
                completionHandler(url, FileProviderItem.file(path: path, remote: remote), nil)
            } catch {
                Log.provider.error("fetch of document \(documentID, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
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

        // The tree is the portal's, and it is fixed: a folder means a filter over `documents`, not
        // a container that can be created. Refused rather than silently ignored, so a new folder
        // never sits in Finder looking as though it exists.
        guard itemTemplate.contentType != .folder else {
            completionHandler(nil, [], false, FileProviderError.unsupported("Folders cannot be created — the Helmsley document tree is fixed."))
            return progress
        }
        guard let contents = url else {
            completionHandler(nil, [], false, FileProviderError.unsupported("A file must have contents to be filed."))
            return progress
        }
        guard let parent = ItemIdentity(itemTemplate.parentItemIdentifier) else {
            completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
            return progress
        }

        let path = parent.path
        let filename = itemTemplate.filename
        // The template's type is what Finder settled from the extension it is being filed under;
        // the server signs it into the upload URL, so it has to be decided before a byte is sent.
        let mime = itemTemplate.contentType?.preferredMIMEType

        let work = Task {
            do {
                let remote = try await api.upload(path: path, filename: filename, mime: mime, fileURL: contents, reporting: progress)
                await signalChange(at: itemTemplate.parentItemIdentifier)
                // No pending fields and nothing still uploading: the document is filed by the time
                // this returns, because the finalise step is what created it.
                completionHandler(FileProviderItem.file(path: path, remote: remote), [], false, nil)
            } catch {
                Log.provider.error("upload to \(path.logPath, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
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

        // Renaming, moving and rewriting all have no endpoint behind them: a document's title, its
        // type and its links are the filing an admin chose in the portal, and the portal refuses to
        // refile some of them outright (a compliance document, for one). Anything else Finder wants
        // to record — a tag, a last-used date — is local, so it is accepted unchanged.
        let unsupported: NSFileProviderItemFields = [.contents, .filename, .parentItemIdentifier]
        guard changedFields.intersection(unsupported).isEmpty else {
            completionHandler(nil, [], false, FileProviderError.unsupported("Helmsley documents cannot be renamed, moved or edited in place. Change them in the portal instead."))
            return progress
        }

        Task {
            do {
                guard let identity = ItemIdentity(item.itemIdentifier) else { throw NSFileProviderError(.noSuchItem) }
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

        guard let identity = ItemIdentity(identifier), case .file(_, let documentID) = identity else {
            completionHandler(FileProviderError.unsupported("Only documents can be deleted — the folders are part of the portal's structure."))
            return progress
        }

        Task {
            do {
                // Deletes the document, not this folder's view of it: a row listed in several
                // folders disappears from all of them, which is what deleting the file means.
                try await api.delete(id: documentID)
                await signalChange(at: identity.parentIdentifier)
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
        // Nothing is ever trashed: a Finder delete removes the document outright, so there is no
        // holding area to enumerate and the system should not offer one.
        if containerItemIdentifier == .trashContainer { throw NSFileProviderError(.noSuchItem) }

        guard let identity = ItemIdentity(containerItemIdentifier), !isFile(identity) else {
            throw NSFileProviderError(.noSuchItem)
        }
        return FolderEnumerator(identity: identity)
    }

    private func isFile(_ identity: ItemIdentity) -> Bool {
        if case .file = identity { return true }
        return false
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
