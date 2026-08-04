import CryptoKit
import FileProvider
import Foundation

/// Remembers what each folder held the last few times it was enumerated.
///
/// `enumerateChanges` has to report deletions, and a deletion is the one thing a listing cannot
/// show you: the server says what is there now, never what has stopped being there. So past listings
/// are kept and the two are diffed.
///
/// On disk in the app group container rather than in memory, because the system asks for changes
/// from an anchor issued by an earlier launch of the extension — an in-memory store would make
/// every cold start look like "everything is new, nothing was deleted", and stale files would sit
/// in Finder until the user thought to unmount.
actor SnapshotStore {

    static let shared = SnapshotStore()

    /// item identifier -> version signature, for one container.
    typealias Snapshot = [String: String]

    /// How many past listings a container keeps.
    ///
    /// One would do if the anchor the system holds always named this store's most recent listing,
    /// and most of the time it does. It does not have to. The system samples `currentSyncAnchor` on
    /// its own schedule, abandons enumerations it has decided against, and comes back from a
    /// relaunch holding whatever it had when it stopped — any of which leaves it asking from a point
    /// this process has already moved past. Diffing that against the newest listing is how a change
    /// gets quietly answered "nothing here", so a few are kept and the right one is chosen.
    private static let retained = 5

    private let directory: URL?

    /// Oldest first; the last is the container as it was last listed.
    private var memory: [String: [Snapshot]] = [:]

    init() {
        directory = FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: Configuration.appGroupIdentifier)?
            .appendingPathComponent("Snapshots", isDirectory: true)
        if let directory {
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
    }

    /// The container as it was last listed — what `currentSyncAnchor` describes.
    func current(for container: NSFileProviderItemIdentifier) -> Snapshot {
        history(for: container).last ?? Snapshot()
    }

    /// The listing an anchor was issued for, or nil where it is no longer held.
    ///
    /// Nil is the answer that matters: it means the diff the system asked for cannot be computed,
    /// and the caller must say so with `.syncAnchorExpired` rather than reporting no changes.
    func snapshot(matching anchor: NSFileProviderSyncAnchor, for container: NSFileProviderItemIdentifier) -> Snapshot? {
        history(for: container).last { Self.signature(of: $0) == anchor.rawValue }
    }

    /// Adds a listing to what the container is known to have been.
    func record(_ snapshot: Snapshot, for container: NSFileProviderItemIdentifier) {
        var history = history(for: container)
        // A listing identical to the last adds nothing to remember, and would push a genuinely
        // older one out of the window for no gain.
        if history.last != snapshot { history.append(snapshot) }
        if history.count > Self.retained { history.removeFirst(history.count - Self.retained) }

        let key = Self.key(container)
        memory[key] = history
        guard let url = fileURL(key), let data = try? JSONEncoder().encode(history) else { return }
        try? data.write(to: url, options: .atomic)
    }

    func forget(_ container: NSFileProviderItemIdentifier) {
        let key = Self.key(container)
        memory[key] = nil
        if let url = fileURL(key) { try? FileManager.default.removeItem(at: url) }
    }

    /// Wipes every snapshot — for a sign-out, after which nothing this store remembers is about an
    /// account that is still mounted.
    func forgetEverything() {
        memory.removeAll()
        guard let directory else { return }
        for url in (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? [] {
            try? FileManager.default.removeItem(at: url)
        }
    }

    private func history(for container: NSFileProviderItemIdentifier) -> [Snapshot] {
        let key = Self.key(container)
        if let cached = memory[key] { return cached }
        // A container nothing has been listed for is empty, which is a state an anchor can name —
        // so it is remembered as one rather than as nothing at all.
        let loaded = load(key) ?? [Snapshot()]
        memory[key] = loaded
        return loaded
    }

    private func load(_ key: String) -> [Snapshot]? {
        guard let url = fileURL(key), let data = try? Data(contentsOf: url) else { return nil }
        if let history = try? JSONDecoder().decode([Snapshot].self, from: data) { return history }
        // What a version of this store that kept only the newest listing wrote. Read rather than
        // discarded, so an update does not re-list every folder the user has ever opened.
        guard let single = try? JSONDecoder().decode(Snapshot.self, from: data) else { return nil }
        return [single]
    }

    private func fileURL(_ key: String) -> URL? {
        directory?.appendingPathComponent(key).appendingPathExtension("json")
    }

    /// An item identifier is base64url of arbitrary path segments, which is filename-safe in
    /// principle and unbounded in length in practice — a hash keeps it to something a filesystem
    /// will actually accept.
    private static func key(_ container: NSFileProviderItemIdentifier) -> String {
        SHA256.hash(data: Data(container.rawValue.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    /// A stable fingerprint of a whole listing, which is what a sync anchor is: two enumerations
    /// that produce the same items in any order must produce the same anchor, or the system
    /// re-syncs a folder that has not changed.
    nonisolated static func signature(of snapshot: Snapshot) -> Data {
        let joined = snapshot.keys.sorted().map { "\($0)=\(snapshot[$0] ?? "")" }.joined(separator: "\n")
        return Data(SHA256.hash(data: Data(joined.utf8)))
    }
}
