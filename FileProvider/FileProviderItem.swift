import CryptoKit
import FileProvider
import Foundation
import UniformTypeIdentifiers

/// One entry in the mounted volume — a folder of the portal's tree, or a file in one.
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
    /// the folder above. Offering a restore down there would be a gesture that silently did nothing.
    ///
    /// What is left inside is reading and purging, and both are real: bytes are still fetched by id,
    /// and a delete still takes the row and everything under it.
    ///
    /// Kept apart from `Permissions`, which the server answers and which says nothing about the bin:
    /// the rules there are about where a row *sits*, and a row in the trash sits where it always did.
    enum Standing {
        /// In the tree, where everything the portal allows this row is allowed.
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

    // MARK: - The mount point

    /// The top of the tree. Named for the portal rather than for the domain so the sidebar entry and
    /// the folder agree.
    static var root: FileProviderItem {
        FileProviderItem(
            identity: .root,
            parent: .rootContainer,
            filename: Configuration.domainDisplayName,
            contentType: .folder,
            // Read-only at the top, which is stricter than the portal is: the server would take a
            // new folder here, but the tree's top level is its skeleton — Clients, Properties,
            // Loans, Shared, Orphaned and the staff folders — and something dragged in beside them
            // would be neither. Everything the volume is for happens a level down.
            capabilities: [.allowsReading, .allowsContentEnumerating],
            documentSize: nil,
            version: Self.version(content: "root", metadata: "root")
        )
    }

    // MARK: - As a folder listed them

    /// A subfolder, as the folder `container` listed it.
    ///
    /// `permissions` is the server's answer for this folder, which is the only thing that decides
    /// what Finder offers on it: a folder the tree placed can be renamed but never moved or thrown
    /// away, one under `/Orphaned` takes nothing at all, and neither is anything a name or a depth
    /// could be read off.
    static func folder(
        in container: ItemIdentity,
        remote: RemoteFolder,
        permissions: Permissions,
        standing: Standing = .live
    ) -> FileProviderItem {
        FileProviderItem(
            identity: .node(id: remote.id),
            parent: container.identifier,
            filename: remote.name,
            contentType: .folder,
            capabilities: capabilities(permissions, folder: true, standing: standing),
            documentSize: nil,
            // A folder's content version is fixed: the portal has no per-folder revision to read,
            // and what actually drives re-listing is the enumerator's sync anchor, which is
            // computed from the listing itself (FolderEnumerator).
            version: Self.version(content: "folder", metadata: "\(remote.name)|\(permissions.signature)")
        )
    }

    /// A file, as the folder `container` listed it.
    static func file(
        in container: ItemIdentity,
        remote: RemoteFile,
        permissions: Permissions,
        standing: Standing = .live
    ) -> FileProviderItem {
        FileProviderItem(
            identity: .node(id: remote.id),
            parent: container.identifier,
            filename: remote.filename,
            contentType: contentType(for: remote),
            capabilities: capabilities(permissions, folder: false, standing: standing),
            documentSize: remote.size.map(NSNumber.init(value:)),
            // The content hash. It changes when and only when the bytes do, so a materialised copy
            // stays valid until the file is genuinely replaced.
            version: Self.version(content: remote.version, metadata: "\(remote.filename)|\(permissions.signature)")
        )
    }

    // MARK: - As the server describes one on its own

    /// One row, built from what the server says about it rather than from where it was found — which
    /// is the only way to build one that is in the bin, since nothing is listing it from a folder.
    ///
    /// `as` is the identifier to answer under, for the one case where it is not the row's own: a
    /// declared folder that has since been materialised resolves by the reference Finder is still
    /// holding, and answers with the row that replaced it. Handing back the row's new identifier
    /// would be answering a question about one item with another. The listing above it vends the new
    /// one, the diff retires the old, and the system is never told two things at once.
    static func item(_ remote: RemoteFile, as identity: ItemIdentity? = nil) -> FileProviderItem {
        // Trashed items hang under the trash rather than under the folder they came from. The row
        // still records that folder — which is what puts them back — but a system that enumerated
        // them in both places would show one item twice.
        //
        // No parent means the row sits directly under the tree's root, which the volume addresses as
        // its mount point rather than by the root row's own id.
        let parent: NSFileProviderItemIdentifier = remote.isTrashed
            ? .trashContainer
            : remote.parent.map { ItemIdentity.node(id: $0).identifier } ?? .rootContainer

        // Trashed first: a row cannot be both, and the mark is only ever on the top of what was
        // thrown away — so anything the server calls covered is by definition below one.
        let standing: Standing = remote.isTrashed ? .binned : (remote.isCovered ? .covered : .live)
        let permissions = remote.permissions ?? Permissions(assumedFrom: true)

        return FileProviderItem(
            identity: identity ?? .node(id: remote.id),
            parent: parent,
            filename: remote.filename,
            contentType: remote.isFolder ? .folder : contentType(for: remote),
            capabilities: capabilities(permissions, folder: remote.isFolder, standing: standing),
            documentSize: remote.isFolder ? nil : remote.size.map(NSNumber.init(value:)),
            // Being thrown away is a metadata change and nothing else — the bytes are untouched, so
            // a materialised copy stays valid through the bin and back out again.
            version: Self.version(
                content: remote.version,
                metadata: "\(remote.filename)|\(remote.isTrashed)|\(permissions.signature)"
            )
        )
    }

    // MARK: - Capabilities

    /// What may be done to an item: what the portal allows it, narrowed by where it stands in
    /// relation to the bin.
    ///
    /// The two are asked separately because they are separate questions. `Permissions` is the
    /// server's rule about where the row sits in the tree — whose folder it is under, whether the
    /// tree itself placed it — and it says the same thing about a row in the bin as it did the
    /// moment before, since throwing something away does not move it. What being in the bin costs is
    /// added here.
    ///
    /// No `.allowsWriting` anywhere. Nothing replaces a file's bytes in place: a row is stored under
    /// a key derived from its content hash, so different bytes are a different row rather than an
    /// edit to this one. Offering it would mean accepting a save in Finder that quietly never
    /// reached the server.
    ///
    /// In the bin, what is left is reading it, putting it back — which is a reparent, hence
    /// `.allowsReparenting` — and purging it. Renaming and refiling are refused by the server while
    /// something is trashed, so they are not offered here either. A step further in, under a folder
    /// that was thrown away, even putting back goes: it is the folder above that carries the mark.
    private static func capabilities(
        _ permissions: Permissions,
        folder: Bool,
        standing: Standing
    ) -> NSFileProviderItemCapabilities {
        var capabilities: NSFileProviderItemCapabilities = [.allowsReading]
        if folder { capabilities.insert(.allowsContentEnumerating) }

        switch standing {
        case .live:
            if folder && permissions.writable { capabilities.insert(.allowsAddingSubItems) }
            if permissions.renamable { capabilities.insert(.allowsRenaming) }
            if permissions.movable { capabilities.insert(.allowsReparenting) }
            // Both are the server's `deletable`, because both are what it checks: a Finder delete
            // arrives as a reparent into the bin, and Delete Immediately as this.
            if permissions.deletable { capabilities.formUnion([.allowsTrashing, .allowsDeleting]) }
        case .binned:
            if permissions.movable { capabilities.insert(.allowsReparenting) }
            if permissions.deletable { capabilities.insert(.allowsDeleting) }
        case .covered:
            if permissions.deletable { capabilities.insert(.allowsDeleting) }
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
