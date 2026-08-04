import FileProvider
import Foundation

/// What an `NSFileProviderItemIdentifier` means here.
///
/// The portal's document tree is a set of views over one table, not a hierarchy of stored paths: a
/// row has no path of its own and appears in every folder whose filter matches it. A filesystem
/// insists on the opposite — one item, one parent — so a file's identity here is *the document seen
/// from a particular folder*, and a document listed in two folders is two items with the same bytes.
/// That is the honest translation: each is exactly the file that folder shows, deleting either
/// deletes the document, and nothing has to invent a canonical home the portal does not have.
///
/// Identifiers are opaque to the system but persist across launches and across reboots, so the
/// encoding below has to stay stable: a change to it orphans everything already synced.
enum ItemIdentity: Equatable {

    /// The mount point itself.
    case root

    /// A folder, addressed by the ordered path segments `/api/files/list` takes.
    case folder(path: [String])

    /// A document as listed in `path`, or an admin's own file in the one folder that holds it.
    ///
    /// The id is the server's, verbatim and unparsed. It says which of the two tables the item came
    /// from as well as which row, so anything that takes it apart — reading it as a number, most of
    /// all — throws away half of what identifies the item.
    case file(path: [String], documentID: String)

    // MARK: - Encoding

    // A single character prefix and a base64url payload, rather than a delimiter-joined path: a
    // segment may legitimately contain any character a client name or folder label contains, and a
    // separator that must never appear in the data is a bug waiting for the first client called
    // "Smith / Jones".
    private static let folderPrefix = "D."
    private static let filePrefix = "F."

    var identifier: NSFileProviderItemIdentifier {
        switch self {
        case .root:
            return .rootContainer
        case .folder(let path):
            return NSFileProviderItemIdentifier(Self.folderPrefix + Self.encode(path))
        case .file(let path, let documentID):
            return NSFileProviderItemIdentifier(Self.filePrefix + Self.encode(path + [documentID]))
        }
    }

    init?(_ identifier: NSFileProviderItemIdentifier) {
        let raw = identifier.rawValue
        if identifier == .rootContainer {
            self = .root
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

    /// The path this identity lists from — a folder's own path, and for a file the folder it was
    /// seen in.
    var path: [String] {
        switch self {
        case .root: return []
        case .folder(let path): return path
        case .file(let path, _): return path
        }
    }

    /// The container an item hangs under. A file's parent is the folder that listed it; a folder's
    /// is the folder above; the root's is itself, which is what the framework expects.
    var parentIdentifier: NSFileProviderItemIdentifier {
        switch self {
        case .root:
            return .rootContainer
        case .folder(let path):
            return path.count <= 1 ? .rootContainer : ItemIdentity.folder(path: path.dropLast()).identifier
        case .file(let path, _):
            return path.isEmpty ? .rootContainer : ItemIdentity.folder(path: path).identifier
        }
    }

    // MARK: - The admin's own folder

    /// The segment the portal's directory spec gives the mount, and the reason it is a literal here.
    ///
    /// Everything below this one folder is a real filesystem — a row sits in exactly one directory
    /// because someone put it there — and everything else in the tree is a view over `documents`
    /// that has no path to move anything along. Only the first branch can be written to structurally,
    /// so the extension has to be able to tell them apart before it offers Finder either operation.
    ///
    /// A literal rather than something read off a listing because the listing does not say: a folder
    /// arrives as a segment, a name and whether it takes uploads, and the mount is not distinguished
    /// among them. The segment itself is stable by the same contract every entity folder relies on —
    /// it stays put while the folder is *labelled* with the admin's name, so that renaming them
    /// cannot break a path already held.
    private static let personalRoot = "My Files"

    /// Whether this identity sits in the admin's own folder — the mount itself, or anything under it.
    var isPersonal: Bool { path.first == Self.personalRoot }

    /// The identifier `/api/files/items/:id` takes, or nil for an item that endpoint would refuse.
    ///
    /// A file carries the server's own id, which is exactly what this is for. A folder does not: a
    /// listing gives it a `segment`, and a segment is a bare row id — the marker saying which table
    /// it came from is written only onto *file* ids. So it is added here, and this is the one place
    /// in the app that knows how an id is spelled. If `/list` ever carries an id per folder beside
    /// its segment, this should read that instead and the knowledge goes away.
    ///
    /// Nil for the mount folder itself, which has no id of its own to rename or move: it is named
    /// after the admin and follows their name.
    var personalItemID: String? {
        guard isPersonal else { return nil }
        switch self {
        case .root:
            return nil
        case .file(_, let documentID):
            return documentID
        case .folder(let path):
            return path.count > 1 ? "af" + path[path.count - 1] : nil
        }
    }

    /// What `DELETE /api/files/documents/:id` would take for this item, or nil where there is
    /// nothing a delete could remove. Every file is deletable — that has always been true of a
    /// document and is true of a personal one — and among folders, only the ones someone made.
    var deletableID: String? {
        if case .file(_, let documentID) = self { return documentID }
        return personalItemID
    }

    /// The same item, seen in `path` instead — what a move produces.
    ///
    /// Note that this changes the item's *identifier*, because a path is part of one. That is sound
    /// for the tree this encoding was designed around, where a document genuinely is a different
    /// item in each folder that lists it, and it is the weak point of applying that encoding to a
    /// branch where things move: the system knows the item by the identifier it passed in, and gets
    /// a different one back. Signalling both containers is what settles it — the item leaves one
    /// listing and appears in the other — but an id-based identity would not need settling. That
    /// needs the server to say what an item's parent is, which no endpoint answers today.
    func relocated(under path: [String]) -> ItemIdentity {
        switch self {
        case .root:
            return .root
        case .file(_, let documentID):
            return .file(path: path, documentID: documentID)
        case .folder(let own):
            guard let segment = own.last else { return self }
            return .folder(path: path + [segment])
        }
    }
}
