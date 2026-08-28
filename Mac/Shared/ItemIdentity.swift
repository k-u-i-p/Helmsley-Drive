import FileProvider
import Foundation

/// What an `NSFileProviderItemIdentifier` means here.
///
/// One tree, so one namespace: the portal keeps its files in a single table, a folder and a file are
/// both rows, and a row's id is the whole of what identifies either. That is what makes a move, a
/// rename or a trip through the bin leave the identity alone — none of the three touches the id.
///
/// The mount point is the one exception, since the framework fixes its identifier as
/// `.rootContainer`. It is addressed by the empty path instead, which is why a row directly under
/// the root reports no parent rather than the root row's id: two names for one container would be
/// two folders.
///
/// A folder the portal has declared but not yet written — a client's Compliance, standing empty
/// until something is filed in it — has no row, so the server mints it a `v<parent>_<type>`
/// reference. That is as opaque here as a serial: the one Finder holds goes on resolving even after
/// the folder is materialised and the reference stops being minted.
///
/// Identifiers persist across launches and reboots, so the encoding has to stay stable — a change to
/// it orphans everything already synced. Which is what the change of prefix was for: identifiers
/// minted before the portal's two trees became one name rows in tables this volume no longer reads,
/// and a small serial means something else in the new table, so resolving one would hand back the
/// wrong file's bytes. The old prefixes are not decoded at all; the system drops what it held and
/// enumerates from the root once.
enum ItemIdentity: Equatable {

    /// The mount point itself, and the only thing here not named by an id.
    case root

    /// A row of the tree — a file or a folder, in the bin or not — addressed by its id alone.
    ///
    /// Where it sits is not encoded, because where it sits changes. Only the server can say where a
    /// row is now, and `/api/files/items/:id` is what says it.
    case node(id: String)

    // MARK: - Encoding

    // A prefix and the id verbatim. Nothing is parsed back out of the id, so whatever the server
    // chooses to mint next cannot confuse this: the prefix comes off, and the rest is the id.
    //
    // The letters are wire format, present in every identifier the system has stored — the constant
    // may be renamed but its value never, and a letter that has meant something else before may
    // never be reused, which is why this one is neither D., F. nor P.
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

    /// For the log, which wants something short.
    var logDescription: String { destination.logDescription }
}

/// Where a read or a write is aimed.
///
/// Two shapes because the tree has two kinds of address, not because it has two trees: everything is
/// a row and carries an id, except the top, which is what the empty path means. The server takes
/// either and resolves both to the same directory.
enum Destination: Sendable, Equatable {
    case root
    case item(String)

    /// A request body naming this destination, plus whatever else the call carries.
    ///
    /// The root sends an empty path rather than nothing at all. A restore reads the presence of
    /// either field as "the caller said where this should land" — so an absent one would mean back
    /// where it came from, and putting something back at the top of the tree would quietly become
    /// something else.
    func body(with extra: [String: Any] = [:]) -> [String: Any] {
        var body = extra
        switch self {
        case .root: body["path"] = [String]()
        case .item(let id): body["parent"] = id
        }
        return body
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
