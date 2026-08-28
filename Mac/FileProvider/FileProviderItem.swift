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

    /// The two dates Finder shows. Nil where the portal did not say, which is what a portal
    /// predating them and a folder with no row of its own both amount to.
    ///
    /// For a file they are one instant until it is first saved over, since the portal starts
    /// `updated_at` at `created_at` and moves it only when the bytes are replaced. A folder has no
    /// content of its own to date, so what it reports as modified is when the row last changed —
    /// see `Timestamp`.
    let creationDate: Date?
    let contentModificationDate: Date?

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
        dates: Dates = Dates(),
        version: NSFileProviderItemVersion
    ) {
        self.itemIdentifier = identity.identifier
        self.parentItemIdentifier = parent
        self.filename = filename
        self.contentType = contentType
        self.capabilities = capabilities
        self.documentSize = documentSize
        self.creationDate = Timestamp.date(dates.created)
        self.contentModificationDate = Timestamp.date(dates.modified)
        self.itemVersion = version
        super.init()
    }

    /// An item's two instants, as the server spelled them.
    ///
    /// Kept together and unparsed until the item is built, because the same pair goes into the
    /// metadata version — as strings, so that a date the formatters cannot read is still something
    /// that has changed when it changes.
    struct Dates {
        var created: String?
        var modified: String?

        /// A file's: when the row was written, and when its bytes were last replaced. The same
        /// instant under both names until somebody saves over it, which is the truth about a file
        /// nobody has edited. The upload date stands in where a portal predating the save answers
        /// with no `updated_at` at all, since the two agreed then by definition.
        static func file(_ remote: RemoteFile) -> Dates {
            Dates(created: remote.uploadDate, modified: remote.modifiedDate ?? remote.uploadDate)
        }
    }

    // MARK: - Standing

    /// Where an item stands in relation to the bin, which is what decides the writes it may offer.
    ///
    /// Three states rather than two, because the top of something thrown away is not the same as the
    /// middle of it. Only the top is marked — a directory takes its contents with it — and only the
    /// top can be put back. Below it the portal refuses a rename or a refile, and answers a restore
    /// by doing nothing at all, since the mark it would clear is on the folder above; offering one
    /// there would be a gesture that silently did nothing.
    ///
    /// Kept apart from `Permissions`, which says nothing about the bin: those rules are about where
    /// a row *sits*, and a row in the trash sits where it always did.
    ///
    /// `String`-backed because it goes into the item's metadata version, which is what makes an
    /// item whose standing changed under it get re-reported.
    enum Standing: String {
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
    /// An empty filename is not an error as far as `FileProvider` is concerned but a programming
    /// mistake: the assertion behind it (`__FILEPROVIDER_BAD_ITEM_MISSING_FILENAME__`) aborts the
    /// process, taking every other operation in flight with it, and the system sees only that the
    /// connection was invalidated. Nothing surfaces to the user.
    ///
    /// A row like that is always the server's mistake, and there is nothing a filesystem could show
    /// for one — so it is refused at this end, where the framework can act on it and the log can
    /// record it.
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
        let dates = Dates(created: remote.uploadDate, modified: remote.modifiedDate)
        return FileProviderItem(
            identity: .node(id: remote.id),
            parent: container.identifier,
            filename: remote.name,
            contentType: .folder,
            capabilities: capabilities(permissions, folder: true, standing: standing),
            documentSize: nil,
            dates: dates,
            // A folder's content version is fixed: the portal has no per-folder revision to read,
            // and what drives re-listing is the child enumerator's own sync anchor.
            version: Self.version(
                content: "folder",
                metadata: Self.metadata(remote.name, permissions, standing, dates)
            )
        )
    }

    /// A file, as the folder `container` listed it.
    static func file(
        in container: ItemIdentity,
        remote: RemoteFile,
        permissions: Permissions,
        standing: Standing = .live
    ) -> FileProviderItem {
        let dates = Dates.file(remote)
        return FileProviderItem(
            identity: .node(id: remote.id),
            parent: container.identifier,
            filename: remote.filename,
            contentType: contentType(for: remote),
            capabilities: capabilities(permissions, folder: false, standing: standing),
            documentSize: remote.size.map(NSNumber.init(value:)),
            dates: dates,
            // The content hash. It changes when and only when the bytes do, so a materialised copy
            // stays valid until the file is genuinely replaced.
            version: Self.version(
                content: remote.version,
                metadata: Self.metadata(remote.filename, permissions, standing, dates)
            )
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

        // A folder is described exactly as the listing above it describes one, which means taking
        // neither of the two things `/items/:id` offers and `/list` does not: the `id:` version it
        // synthesises for a row with no content hash, and — where the row is a file's dates read off
        // a folder — a modified date that means something else. A folder that answered differently
        // depending on which way the system asked would be reported as changed for having done
        // nothing at all.
        let folder = remote.isFolder
        let dates = folder
            ? Dates(created: remote.uploadDate, modified: remote.modifiedDate)
            : Dates.file(remote)

        return FileProviderItem(
            identity: identity ?? .node(id: remote.id),
            parent: parent,
            filename: remote.filename,
            contentType: folder ? .folder : contentType(for: remote),
            capabilities: capabilities(permissions, folder: folder, standing: standing),
            documentSize: folder ? nil : remote.size.map(NSNumber.init(value:)),
            dates: dates,
            // Being thrown away is a metadata change and nothing else — the bytes are untouched, so
            // a materialised copy stays valid through the bin and back out again.
            version: Self.version(
                content: folder ? "folder" : remote.version,
                metadata: Self.metadata(remote.filename, permissions, standing, dates)
            )
        )
    }

    // MARK: - Capabilities

    /// What may be done to an item: what the portal allows it, narrowed by where it stands in
    /// relation to the bin.
    ///
    /// Two separate questions. `Permissions` is the server's rule about where the row sits, and it
    /// says the same thing about a row in the bin as it did the moment before, since throwing
    /// something away does not move it; what being in the bin costs is added here.
    ///
    /// `.allowsWriting` is a file's, and the row's own `writable` decides it — the same flag that
    /// lets a folder be filed into, which is the server's answer for the row rather than anything
    /// this app works out. The portal now takes a replacement for a file's bytes under the same row,
    /// so a save in Finder reaches it: the row keeps its id and its place, and what changes is its
    /// content hash and its Date Modified. Nothing in the bin offers it, because the portal refuses
    /// to change anything it is holding.
    ///
    /// In the bin: reading, putting back (a reparent, hence `.allowsReparenting`), and purging.
    /// The server refuses a rename or a refile while something is trashed. A step further in, under
    /// a trashed folder, even putting back goes — it is the folder above that carries the mark.
    private static func capabilities(
        _ permissions: Permissions,
        folder: Bool,
        standing: Standing
    ) -> NSFileProviderItemCapabilities {
        var capabilities: NSFileProviderItemCapabilities = [.allowsReading]
        if folder { capabilities.insert(.allowsContentEnumerating) }

        switch standing {
        case .live:
            if permissions.writable { capabilities.insert(folder ? .allowsAddingSubItems : .allowsWriting) }
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

    /// Everything about an item that is not its bytes, spelled once so that the two ways of building
    /// one — from the listing above it, and from `/items/:id` — cannot describe it differently.
    private static func metadata(
        _ name: String,
        _ permissions: Permissions,
        _ standing: Standing,
        _ dates: Dates
    ) -> String {
        "\(name)|\(permissions.signature)|\(standing.rawValue)|\(dates.created ?? "")|\(dates.modified ?? "")"
    }

    /// `NSFileProviderItemVersion` takes opaque data capped at 128 bytes, so both halves are hashed
    /// rather than stored: a folder label is user-entered and has no length limit worth trusting,
    /// and a truncated one would make two long names that share a prefix look like the same version.
    private static func version(content: String, metadata: String) -> NSFileProviderItemVersion {
        NSFileProviderItemVersion(
            contentVersion: Data(SHA256.hash(data: Data(content.utf8))),
            metadataVersion: Data(SHA256.hash(data: Data(metadata.utf8)))
        )
    }

    /// What the enumerators record for this item and diff against next time.
    ///
    /// The version itself, rather than a string assembled a second time alongside it: the diff and
    /// the version have to agree about what counts as a change, and two places spelling that out
    /// separately is how they come to disagree — silently, since what it costs is a change the
    /// system is never told about.
    var snapshotSignature: String {
        itemVersion.contentVersion.base64EncodedString() + itemVersion.metadataVersion.base64EncodedString()
    }
}
