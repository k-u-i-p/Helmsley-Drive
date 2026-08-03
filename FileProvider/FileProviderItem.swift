import CryptoKit
import FileProvider
import Foundation
import UniformTypeIdentifiers

/// One entry in the mounted volume — a folder of the portal's document tree, or a document listed
/// in one.
final class FileProviderItem: NSObject, NSFileProviderItem {

    let itemIdentifier: NSFileProviderItemIdentifier
    let parentItemIdentifier: NSFileProviderItemIdentifier
    let filename: String
    let contentType: UTType
    let capabilities: NSFileProviderItemCapabilities
    let documentSize: NSNumber?
    let itemVersion: NSFileProviderItemVersion

    /// Nothing is downloaded until something opens it, and the system may reclaim the bytes of
    /// anything nobody has opened lately. The portal holds tens of gigabytes across every client;
    /// materialising that on a laptop because a folder was glanced at would be indefensible.
    var contentPolicy: NSFileProviderContentPolicy { .downloadLazily }

    private init(
        identity: ItemIdentity,
        filename: String,
        contentType: UTType,
        capabilities: NSFileProviderItemCapabilities,
        documentSize: NSNumber?,
        version: NSFileProviderItemVersion
    ) {
        self.itemIdentifier = identity.identifier
        self.parentItemIdentifier = identity.parentIdentifier
        self.filename = filename
        self.contentType = contentType
        self.capabilities = capabilities
        self.documentSize = documentSize
        self.itemVersion = version
        super.init()
    }

    // MARK: - Folders

    /// The mount point. Named for the portal rather than for the domain so the sidebar entry and
    /// the folder agree.
    static var root: FileProviderItem {
        FileProviderItem(
            identity: .root,
            filename: Configuration.domainDisplayName,
            contentType: .folder,
            // Read-only at the top: the root of the tree holds no documents of its own, and every
            // folder below it is part of a fixed structure the portal defines. Nothing here can
            // create one, which is why `.allowsAddingSubItems` is absent.
            capabilities: [.allowsReading, .allowsContentEnumerating],
            documentSize: nil,
            version: Self.version(content: "root", metadata: "root")
        )
    }

    static func folder(path: [String], remote: RemoteFolder) -> FileProviderItem {
        var capabilities: NSFileProviderItemCapabilities = [.allowsReading, .allowsContentEnumerating]
        // Exactly the folders the portal's own tree marks as taking uploads. A drop anywhere else
        // is refused by Finder before a byte moves, rather than by the server after all of them.
        if remote.writable { capabilities.insert(.allowsAddingSubItems) }

        return FileProviderItem(
            identity: .folder(path: path),
            filename: remote.name,
            contentType: .folder,
            capabilities: capabilities,
            documentSize: nil,
            // A folder's content version is fixed: the portal has no per-folder revision to read,
            // and what actually drives re-listing is the enumerator's sync anchor, which is
            // computed from the listing itself (FolderEnumerator).
            version: Self.version(content: "folder", metadata: "\(remote.name)|\(remote.writable)")
        )
    }

    // MARK: - Files

    static func file(path: [String], remote: RemoteFile) -> FileProviderItem {
        FileProviderItem(
            identity: .file(path: path, documentID: remote.id),
            filename: remote.filename,
            contentType: contentType(for: remote),
            // No `.allowsWriting` and no `.allowsRenaming`: the portal has no endpoint that
            // replaces a document's bytes or its title, so offering either would mean accepting an
            // edit in Finder that quietly never reached the server. Finder shows these as locked,
            // which is the truth. Deleting is real — it deletes the document.
            capabilities: [.allowsReading, .allowsDeleting],
            documentSize: remote.size.map(NSNumber.init(value:)),
            // The content hash. It changes when and only when the bytes do, so a materialised copy
            // stays valid until the document is genuinely replaced.
            version: Self.version(content: remote.version, metadata: remote.filename)
        )
    }

    /// What the file is, in the terms the OS understands. The stored mime first, since that is what
    /// the portal recorded at upload; the extension second, for legacy rows that carry no mime at
    /// all; and `.data` last, which shows a generic icon rather than a wrong one.
    private static func contentType(for remote: RemoteFile) -> UTType {
        if let mime = remote.mime, let type = UTType(mimeType: mime) { return type }
        let ext = (remote.filename as NSString).pathExtension
        if !ext.isEmpty, let type = UTType(filenameExtension: ext) { return type }
        return .data
    }

    // MARK: - Versions

    /// `NSFileProviderItemVersion` takes opaque data capped at 128 bytes, so both halves are hashed
    /// rather than stored: a folder label is user-entered and has no length limit worth trusting,
    /// and a truncated one would make two long names that share a prefix look like the same version.
    private static func version(content: String, metadata: String) -> NSFileProviderItemVersion {
        NSFileProviderItemVersion(
            contentVersion: Data(SHA256.hash(data: Data(content.utf8))),
            metadataVersion: Data(SHA256.hash(data: Data(metadata.utf8)))
        )
    }
}
