import CryptoKit
import FileProvider
import Foundation

/// Remembers what each folder held last time it was enumerated.
///
/// `enumerateChanges` has to report deletions, and a deletion is the one thing a listing cannot
/// show you: the server says what is there now, never what has stopped being there. So the last
/// listing is kept and the two are diffed.
///
/// On disk in the app group container rather than in memory, because the system asks for changes
/// from an anchor issued by an earlier launch of the extension — an in-memory snapshot would make
/// every cold start look like "everything is new, nothing was deleted", and stale files would sit
/// in Finder until the user thought to unmount.
actor SnapshotStore {

    static let shared = SnapshotStore()

    /// item identifier -> version signature, for one container.
    typealias Snapshot = [String: String]

    private let directory: URL?
    private var memory: [String: Snapshot] = [:]

    init() {
        directory = FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: Configuration.appGroupIdentifier)?
            .appendingPathComponent("Snapshots", isDirectory: true)
        if let directory {
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
    }

    func snapshot(for container: NSFileProviderItemIdentifier) -> Snapshot {
        let key = Self.key(container)
        if let cached = memory[key] { return cached }
        guard let url = fileURL(key),
              let data = try? Data(contentsOf: url),
              let stored = try? JSONDecoder().decode(Snapshot.self, from: data) else { return [:] }
        memory[key] = stored
        return stored
    }

    func store(_ snapshot: Snapshot, for container: NSFileProviderItemIdentifier) {
        let key = Self.key(container)
        memory[key] = snapshot
        guard let url = fileURL(key), let data = try? JSONEncoder().encode(snapshot) else { return }
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
