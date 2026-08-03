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

    /// A document as listed in `path`.
    case file(path: [String], documentID: Int)

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
            return NSFileProviderItemIdentifier(Self.filePrefix + Self.encode(path + ["\(documentID)"]))
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
            guard var parts = Self.decode(String(raw.dropFirst(Self.filePrefix.count))),
                  let last = parts.popLast(),
                  let documentID = Int(last) else { return nil }
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
}
