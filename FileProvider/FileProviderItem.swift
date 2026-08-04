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

    #if os(macOS)
    /// Nothing is downloaded until something opens it, and the system may reclaim the bytes of
    /// anything nobody has opened lately. The portal holds tens of gigabytes across every client;
    /// materialising that on a laptop because a folder was glanced at would be indefensible.
    ///
    /// macOS only, because only macOS offers the choice: on iOS the Files app is lazy already, and
    /// `.downloadLazily` is not a case the platform declares.
    var contentPolicy: NSFileProviderContentPolicy { .downloadLazily }
    #endif

    // Nothing here declares `isTrashed`. The framework's own is iOS-only — it belongs to the older,
    // unreplicated API — and on a replicated extension the parent is the whole signal: "when an item
    // is trashed, its parentItemIdentifier becomes NSFileProviderTrashContainerItemIdentifier". So
    // being in the bin is expressed by hanging under `.trashContainer` and nothing else, which also
    // means there is only one place it can be got wrong.

    private init(
        identity: ItemIdentity,
        parent: NSFileProviderItemIdentifier,
        filename: String,
        contentType: UTType,
        capabilities: NSFileProviderItemCapabilities,
        documentSize: NSNumber?,
        version: NSFileProviderItemVersion
    ) {
        self.itemIdentifier = identity.identifier
        self.parentItemIdentifier = parent
        self.filename = filename
        self.contentType = contentType
        self.capabilities = capabilities
        self.documentSize = documentSize
        self.itemVersion = version
        super.init()
    }

    // MARK: - Standing

    /// Where an item stands in relation to the bin, which is what decides the writes it may offer.
    ///
    /// Three states rather than two, because the top of something thrown away is not the same as the
    /// middle of it. The bin lists the top — a directory takes its contents with it, so only the top
    /// is even marked — and that is the one that can be put back. Everything under it is reachable
    /// only by looking inside, and the portal refuses to rename or refile any of it; a restore is
    /// worse than refused, answering by doing nothing at all, since the mark it would clear is on
    /// the folder above. Offering Put Back down there would be a gesture that silently did nothing.
    ///
    /// What is left inside is reading and purging, and both are real: bytes are still fetched by id,
    /// and a delete still takes the row and everything under it.
    enum Standing {
        /// In the tree, where everything this volume can do is allowed.
        case live
        /// The top of something thrown away: listed by the bin, put back or purged from there.
        case binned
        /// Inside something thrown away. Read it or purge it; nothing else reaches it.
        case covered
    }

    // MARK: - What the framework will accept

    /// Whether this item can be handed over at all, which comes down to its having a name.
    ///
    /// An empty filename is not an error as far as `FileProvider` is concerned — it is a programming
    /// mistake, and the assertion behind it (`__FILEPROVIDER_BAD_ITEM_MISSING_FILENAME__`) aborts the
    /// process. So a nameless row is not a request that fails: it is the extension dying mid-call,
    /// taking every other operation in flight down with it, and the system sees only that the
    /// connection was invalidated. Nothing surfaces to the user, and what they asked for silently
    /// never happened.
    ///
    /// A row like that is always the server's mistake, and there is nothing a filesystem could show
    /// for one in any case — so it is refused at this end, where refusing is something the framework
    /// can act on and something the log can record.
    var isNameable: Bool { !filename.isEmpty }

    // MARK: - Folders

    /// The mount point. Named for the portal rather than for the domain so the sidebar entry and
    /// the folder agree.
    static var root: FileProviderItem {
        FileProviderItem(
            identity: .root,
            parent: .rootContainer,
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

    /// A subfolder, as the folder `container` listed it.
    ///
    /// A folder of the admin's own carries an id and is identified by it; everywhere else a folder
    /// is a filter the directory spec defines, which has no identity apart from where it sits and so
    /// keeps its path. The mount itself falls in the second group — it is listed by the classified
    /// tree above it, which has no ids to give.
    static func folder(in container: ItemIdentity, remote: RemoteFolder, standing: Standing = .live) -> FileProviderItem {
        let identity = remote.id.map(ItemIdentity.personal(id:))
            ?? .folder(path: container.path + [remote.segment])

        var capabilities: NSFileProviderItemCapabilities = [.allowsReading, .allowsContentEnumerating]
        switch standing {
        case .live:
            // Exactly the folders the portal's own tree marks as taking uploads. A drop anywhere
            // else is refused by Finder before a byte moves, rather than by the server after all of
            // them.
            //
            // The admin's own folder is added by identity rather than by the flag, because the flag
            // is false for the mount seen from the level above it: what a drop there would write is
            // a directory row, which only walking into the mount resolves, so the listing that names
            // it has nothing to report. Walk in and the same folder says it is writable — and it is.
            if remote.writable || identity.isPersonal { capabilities.insert(.allowsAddingSubItems) }
            // Structural writes, only where a folder is a row someone made. Everywhere else a folder
            // is a filter over `documents` that the spec defines, with nothing to rename or move.
            if identity.personalItemID != nil {
                capabilities.formUnion([.allowsRenaming, .allowsReparenting, .allowsDeleting, .allowsTrashing])
            }
        case .binned, .covered:
            // A folder listed out of the bin. Open it and purge it — nothing else reaches inside a
            // thrown-away subtree, and the bin's own top level is listed by BinEnumerator rather
            // than by this, so what arrives here is always something further in.
            capabilities.insert(.allowsDeleting)
        }

        return FileProviderItem(
            identity: identity,
            parent: container.identifier,
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

    /// A file, as the folder `container` listed it.
    static func file(in container: ItemIdentity, remote: RemoteFile, standing: Standing = .live) -> FileProviderItem {
        let identity: ItemIdentity = container.isPersonal
            ? .personal(id: remote.id)
            : .file(path: container.path, documentID: remote.id)

        return FileProviderItem(
            identity: identity,
            parent: container.identifier,
            filename: remote.filename,
            contentType: contentType(for: remote),
            capabilities: capabilities(forPersonal: identity.isPersonal, folder: false, standing: standing),
            documentSize: remote.size.map(NSNumber.init(value:)),
            // The content hash. It changes when and only when the bytes do, so a materialised copy
            // stays valid until the document is genuinely replaced.
            version: Self.version(content: remote.version, metadata: remote.filename)
        )
    }

    // MARK: - The admin's own items

    /// An item of the admin's own tree, built from what the server says about it rather than from
    /// where it was found — which is the only way to build one that is in the bin, since nothing is
    /// listing it from a folder.
    static func personal(_ remote: RemoteFile) -> FileProviderItem {
        // Trashed items hang under the trash rather than under the folder they came from. The row
        // still records that folder — which is what puts them back — but a system that enumerated
        // them in both places would show one item twice.
        let parent: NSFileProviderItemIdentifier = remote.isTrashed
            ? .trashContainer
            : remote.parent.map { ItemIdentity.personal(id: $0).identifier }
                ?? ItemIdentity.personalMount.identifier

        return FileProviderItem(
            identity: .personal(id: remote.id),
            parent: parent,
            filename: remote.filename,
            contentType: remote.isFolder ? .folder : contentType(for: remote),
            // Trashed first: a row cannot be both, and the mark is only ever on the top of what was
            // thrown away — so anything the server calls covered is by definition below one.
            capabilities: capabilities(
                forPersonal: true,
                folder: remote.isFolder,
                standing: remote.isTrashed ? .binned : (remote.isCovered ? .covered : .live)
            ),
            documentSize: remote.isFolder ? nil : remote.size.map(NSNumber.init(value:)),
            // Being thrown away is a metadata change and nothing else — the bytes are untouched, so
            // a materialised copy stays valid through the bin and back out again.
            version: Self.version(content: remote.version, metadata: "\(remote.filename)|\(remote.isTrashed)")
        )
    }

    /// What may be done to an item, which comes down to which tree it is in and whether it is
    /// already in the bin.
    ///
    /// No `.allowsWriting` on either tree: nothing replaces a file's bytes in place. A document's are
    /// the filing an admin chose, and an admin's own file is stored under a key derived from its
    /// content hash, so different bytes are a different row rather than an edit to this one. Offering
    /// it would mean accepting a save in Finder that quietly never reached the server.
    ///
    /// A document has no name of its own to change and no path to move along — its title and its
    /// links are the filing, and the portal refuses to refile some of them at all. A file in the
    /// admin's own folder is the opposite: its name is a filename someone typed, and it got where it
    /// is because they put it there.
    ///
    /// In the bin, all that is left is reading it, putting it back — which is a reparent, hence
    /// `.allowsReparenting` — and purging it. Renaming and refiling are refused by the server while
    /// something is trashed, so they are not offered here either. A step further in, under a folder
    /// that was thrown away, even putting back goes: it is the folder above that carries the mark.
    private static func capabilities(forPersonal personal: Bool, folder: Bool, standing: Standing) -> NSFileProviderItemCapabilities {
        var capabilities: NSFileProviderItemCapabilities = [.allowsReading, .allowsDeleting]
        if folder { capabilities.insert(.allowsContentEnumerating) }
        guard personal else { return capabilities }

        switch standing {
        case .live:
            capabilities.formUnion([.allowsRenaming, .allowsReparenting, .allowsTrashing])
            if folder { capabilities.insert(.allowsAddingSubItems) }
        case .binned:
            capabilities.insert(.allowsReparenting)
        case .covered:
            break
        }
        return capabilities
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
