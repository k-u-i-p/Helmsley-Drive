import FileProvider
import Foundation

/// What an `NSFileProviderItemIdentifier` means here.
///
/// The volume mounts two trees that disagree about what a file is, and the encoding has to serve
/// both.
///
/// The portal's document tree is a set of views over one table, not a hierarchy of stored paths: a
/// row has no path of its own and appears in every folder whose filter matches it. A filesystem
/// insists on the opposite — one item, one parent — so a document's identity here is *the document
/// seen from a particular folder*, and one listed in two folders is two items with the same bytes.
/// That is the honest translation: each is exactly the file that folder shows, deleting either
/// deletes the document, and nothing has to invent a canonical home the portal does not have.
///
/// The admin's own folder is a real filesystem, and a path is exactly the wrong thing to identify
/// something in it. A row there sits in one directory and can be moved to another, or thrown away
/// and put back — and if the path were part of the identifier, every one of those would change what
/// the system thinks the item *is*. So those are identified by the id the server gave them and
/// nothing else, which no move or trashing touches.
///
/// Identifiers are opaque to the system but persist across launches and across reboots, so the
/// encoding below has to stay stable: a change to it orphans everything already synced.
enum ItemIdentity: Equatable {

    /// The mount point itself.
    case root

    /// A folder, addressed by the ordered path segments `/api/files/list` takes.
    case folder(path: [String])

    /// A document as listed in `path`.
    ///
    /// The id is the server's, verbatim and unparsed. It says which of the two tables the item came
    /// from as well as which row, so anything that takes it apart — reading it as a number, most of
    /// all — throws away half of what identifies the item.
    case file(path: [String], documentID: String)

    /// An item in the admin's own folder — a file or a directory, in the bin or not — addressed by
    /// its id alone.
    ///
    /// Its parent is not encoded here because it is not fixed. Where it sits is something only the
    /// server can answer, and `/api/files/items/:id` is what answers it.
    case personal(id: String)

    // MARK: - Encoding

    // A single character prefix and a base64url payload, rather than a delimiter-joined path: a
    // segment may legitimately contain any character a client name or folder label contains, and a
    // separator that must never appear in the data is a bug waiting for the first client called
    // "Smith / Jones". An id is encoded the same way for the same reason — it is opaque, so nothing
    // here may assume which characters it will not contain.
    private static let folderPrefix = "D."
    private static let filePrefix = "F."
    private static let personalPrefix = "P."

    /// The mount's own segment, from the portal's directory spec.
    ///
    /// Everything below this folder is the admin's own and is identified by id; everything else is
    /// a view over `documents` and is identified by path. A literal rather than something read off a
    /// listing because the listing does not say which tree a folder belongs to — and the segment is
    /// stable by the same contract every entity folder relies on, the folder being *labelled* with
    /// the admin's name rather than named by it, so that renaming them breaks no held path.
    static let personalRoot = "My Files"

    /// The mount folder itself, which is the one part of the admin's tree still addressed by path:
    /// it is where the two trees meet, and the classified half above it has no ids at all.
    static var personalMount: ItemIdentity { .folder(path: [personalRoot]) }

    var identifier: NSFileProviderItemIdentifier {
        switch self {
        case .root:
            return .rootContainer
        case .folder(let path):
            return NSFileProviderItemIdentifier(Self.folderPrefix + Self.encode(path))
        case .file(let path, let documentID):
            return NSFileProviderItemIdentifier(Self.filePrefix + Self.encode(path + [documentID]))
        case .personal(let id):
            return NSFileProviderItemIdentifier(Self.personalPrefix + Self.encode([id]))
        }
    }

    init?(_ identifier: NSFileProviderItemIdentifier) {
        let raw = identifier.rawValue
        if identifier == .rootContainer {
            self = .root
        } else if raw.hasPrefix(Self.personalPrefix) {
            guard let parts = Self.decode(String(raw.dropFirst(Self.personalPrefix.count))),
                  let id = parts.first else { return nil }
            self = .personal(id: id)
        } else if raw.hasPrefix(Self.folderPrefix) {
            guard let path = Self.decode(String(raw.dropFirst(Self.folderPrefix.count))) else { return nil }
            self = path.isEmpty ? .root : .folder(path: path)
        } else if raw.hasPrefix(Self.filePrefix) {
            // The last segment is the id and the rest is the path. Nothing is parsed out of the id:
            // an identifier minted before the admin's own folder existed is a bare number and one
            // minted since may not be, and both are just the string the server answers to.
            guard var parts = Self.decode(String(raw.dropFirst(Self.filePrefix.count))),
                  let documentID = parts.popLast() else { return nil }
            self = .file(path: parts, documentID: documentID)
        } else {
            return nil
        }
    }

    private static func encode(_ path: [String]) -> String {
        let data = (try? JSONEncoder().encode(path)) ?? Data("[]".utf8)
        return data.base64URLEncodedString()
    }

    private static func decode(_ encoded: String) -> [String]? {
        var padded = encoded.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        padded += String(repeating: "=", count: (4 - padded.count % 4) % 4)
        guard let data = Data(base64Encoded: padded) else { return nil }
        return try? JSONDecoder().decode([String].self, from: data)
    }

    // MARK: - Navigation

    /// The path this identity lists from — a folder's own path, and for a document the folder it was
    /// seen in. Empty for a personal item, which has no path and is not addressed by one.
    var path: [String] {
        switch self {
        case .root: return []
        case .folder(let path): return path
        case .file(let path, _): return path
        case .personal: return []
        }
    }

    /// Where a read or a write aimed at this identity should be sent.
    var destination: Destination {
        if case .personal(let id) = self { return .item(id) }
        return .path(path)
    }

    /// The container an item hangs under. A document's parent is the folder that listed it; a
    /// folder's is the folder above; the root's is itself, which is what the framework expects.
    ///
    /// Nil for a personal item, and deliberately so: where one sits is the server's to say, changes
    /// under it, and is carried on the item itself rather than derived from its name.
    var parentIdentifier: NSFileProviderItemIdentifier? {
        switch self {
        case .root:
            return .rootContainer
        case .folder(let path):
            return path.count <= 1 ? .rootContainer : ItemIdentity.folder(path: path.dropLast()).identifier
        case .file(let path, _):
            return path.isEmpty ? .rootContainer : ItemIdentity.folder(path: path).identifier
        case .personal:
            return nil
        }
    }

    // MARK: - The admin's own folder

    /// Whether this identity is in the admin's own folder — the mount itself, or anything under it.
    var isPersonal: Bool {
        if case .personal = self { return true }
        return path.first == Self.personalRoot
    }

    /// The id the item endpoints take, or nil for something they would refuse.
    ///
    /// The second half of this is the bridge for identifiers minted before personal items were
    /// addressed by id. Those carry a path, and a folder's last segment is its row id with the
    /// marker naming its table left off — so it is put back on here, and only here. Anything the
    /// system has enumerated since arrives as `.personal` and needs none of it.
    ///
    /// Nil for the mount folder itself, which has no id of its own: it is named after the admin and
    /// follows their name.
    var personalItemID: String? {
        switch self {
        case .personal(let id):
            return id
        case .file(_, let documentID) where isPersonal:
            return documentID
        case .folder(let path) where isPersonal:
            return path.count > 1 ? "af" + path[path.count - 1] : nil
        default:
            return nil
        }
    }

    /// The same item under the identifier it would be given today.
    ///
    /// A one-time step for anything the system still holds by path: the identifier changes once, the
    /// folder re-syncs, and nothing does it again. Everything already addressed by id is returned
    /// unchanged, which is the whole reason a move or a trip through the bin no longer disturbs it.
    var asPersonal: ItemIdentity {
        guard let id = personalItemID else { return self }
        return .personal(id: id)
    }

    /// What `DELETE /api/files/documents/:id` would take for this item, or nil where there is
    /// nothing a delete could remove. Every file is deletable — that has always been true of a
    /// document and is true of a personal one — and among folders, only the ones someone made.
    var deletableID: String? {
        if case .file(_, let documentID) = self { return documentID }
        return personalItemID
    }
}

/// Where a read or a write is aimed.
///
/// The classified tree has only paths — a folder there is a filter, not a row, and there is nothing
/// else to call it by. The admin's own tree has ids, and those are what survive the folder being
/// moved or thrown away. The server takes either and resolves both to the same directory, so nothing
/// above this has to care which tree it is talking to.
enum Destination: Sendable, Equatable {
    case path([String])
    case item(String)

    /// The request body fields that name it, ready to be merged into a payload.
    var body: [String: Any] {
        switch self {
        case .path(let path): return ["path": path]
        case .item(let id): return ["parent": id]
        }
    }

    /// The same, as query items for the reads.
    var query: [URLQueryItem] {
        switch self {
        case .path(let path): return path.map { URLQueryItem(name: "path", value: $0) }
        case .item(let id): return [URLQueryItem(name: "item", value: id)]
        }
    }

    /// For the log, which wants something short and has no business printing a whole tree.
    var logDescription: String {
        switch self {
        case .path(let path): return path.logPath
        case .item(let id): return id
        }
    }
}
