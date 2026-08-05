import FileProvider
import Foundation

/// What an `NSFileProviderItemIdentifier` means here.
///
/// One tree, so one namespace. The portal keeps its files in a single table: a folder is a row, a
/// file is a row, and the row's id is the whole of what identifies either. So everything below the
/// mount point is addressed by that id and by nothing else — which is exactly what makes a move, a
/// rename or a trip through the bin leave the identity alone. None of the three touches the id, and
/// an identifier built from where a thing sits would call every one of them a different item.
///
/// The mount point is the one exception, because the framework fixes its identifier as
/// `.rootContainer` and the tree's own root row has no say in it. It is addressed instead by the
/// empty path, which is what `/api/files/list` answers for with the top of the tree — and it is why
/// a row directly under the root reports no parent at all rather than the root row's id: there is
/// one root container, and two names for it would be two folders.
///
/// A folder the portal has declared but not yet written — a client's Compliance, say, standing empty
/// until something is filed in it — has no row and so no id of its own. The server gives it one
/// anyway (`v<parent>_<type>`), and that reference is as opaque here as a serial is: it is a string
/// the server answers to, it stops being minted the moment the folder is materialised, and the one
/// Finder is still holding goes on resolving to the row that replaced it.
///
/// Identifiers are opaque to the system but persist across launches and across reboots, so the
/// encoding below has to stay stable: a change to it orphans everything already synced.
///
/// Which is what the change of prefix is *for*. Identifiers minted before the portal's two trees
/// became one name rows in tables this volume no longer reads, and their ids mean nothing here — a
/// document id is a small serial, and small serials exist in the new table too, belonging to
/// something else entirely. Resolving one would hand back the wrong file's bytes under a name
/// somebody trusted. So the old prefixes are not decoded at all: they fail, the system drops what it
/// was holding, and the volume enumerates itself again from the root. Once.
enum ItemIdentity: Equatable {

    /// The mount point itself, and the only thing here not named by an id.
    case root

    /// A row of the tree — a file or a folder, in the bin or not — addressed by its id alone.
    ///
    /// Where it sits is not encoded, because where it sits changes: dragging a folder somewhere else
    /// moves everything under it, and every one of those rows keeps the identity it had. Only the
    /// server can say where a row is now, and `/api/files/items/:id` is what says it.
    case node(id: String)

    // MARK: - Encoding

    // A prefix and the id verbatim. The id is opaque — it is a serial today and a `v` reference for
    // a declared folder — but nothing has to be parsed back out of it, so there is nothing here that
    // can be confused by whatever the server chooses to mint next: the prefix comes off, and the
    // rest is the id.
    //
    // The letters are wire format. They are in every identifier the system has stored, so a constant
    // may be renamed here but its value never — and a letter that has meant something else before
    // may never be reused, which is why this one is neither D., F. nor P.
    private static let nodePrefix = "N."

    var identifier: NSFileProviderItemIdentifier {
        switch self {
        case .root:
            return .rootContainer
        case .node(let id):
            return NSFileProviderItemIdentifier(Self.nodePrefix + id)
        }
    }

    init?(_ identifier: NSFileProviderItemIdentifier) {
        if identifier == .rootContainer {
            self = .root
            return
        }
        let raw = identifier.rawValue
        guard raw.hasPrefix(Self.nodePrefix) else { return nil }
        let id = String(raw.dropFirst(Self.nodePrefix.count))
        guard !id.isEmpty else { return nil }
        self = .node(id: id)
    }

    // MARK: - Navigation

    /// Where a read or a write aimed at this identity should be sent.
    var destination: Destination {
        switch self {
        case .root: return .root
        case .node(let id): return .item(id)
        }
    }

    /// The id the item endpoints take, or nil for the mount point, which is not a row and has none.
    var nodeID: String? {
        if case .node(let id) = self { return id }
        return nil
    }
}

/// Where a read or a write is aimed.
///
/// Two shapes because the tree has two kinds of address, not because it has two trees: everything is
/// a row and carries an id, except the top, which is what the empty path means. The server takes
/// either and resolves both to the same directory.
enum Destination: Sendable, Equatable {
    case root
    case item(String)

    /// The request body fields that name it, ready to be merged into a payload.
    ///
    /// The root sends an empty path rather than nothing at all. A restore reads the presence of
    /// either field as "the caller said where this should land" — so an absent one would mean back
    /// where it came from, and putting something back at the top of the tree would quietly become
    /// something else.
    var body: [String: Any] {
        switch self {
        case .root: return ["path": [String]()]
        case .item(let id): return ["parent": id]
        }
    }

    /// The same, as query items for the reads. Nothing at all for the root: no `path` is what the
    /// server walks from the top for.
    var query: [URLQueryItem] {
        switch self {
        case .root: return []
        case .item(let id): return [URLQueryItem(name: "item", value: id)]
        }
    }

    /// For the log, which wants something short.
    var logDescription: String {
        switch self {
        case .root: return "/"
        case .item(let id): return id
        }
    }
}
