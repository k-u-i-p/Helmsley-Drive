import Foundation

// MARK: - Wire types

/// The portal's `created_at` and `updated_at`, which reach here as a `timestamptz` serialised into
/// JSON: ISO 8601 in UTC, with fractional seconds where the instant has them.
///
/// `created_at` is when a row was written. `updated_at` is when a file's bytes were last replaced —
/// it starts equal to `created_at` and moves only on a save, never on a rename, a move or a trip
/// through the bin — which is what makes it a Date Modified rather than a record of the last time
/// anybody touched the row. A folder has no bytes to date, so for one it is the other thing: when
/// the row itself last changed, which is the only "modified" a folder here has.
enum Timestamp {

    static func date(_ value: String?) -> Date? {
        guard let value else { return nil }
        return fractional.date(from: value) ?? whole.date(from: value)
    }

    // Two, because `ISO8601DateFormatter` matches fractional seconds only when told to and Postgres
    // omits them on a whole second. Static because this runs for every row of every listing, and the
    // formatter — unlike `DateFormatter` — is documented as safe to share.
    private static let fractional: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    private static let whole = ISO8601DateFormatter()
}

/// What the portal will let this admin do to one row, decided by the row's whole ancestor chain.
///
/// Asked rather than guessed at, because the rules are statements about where a row sits and only
/// the server knows: an admin's own folder is readable by a super admin who still may not write in
/// it, `/Orphaned` takes nothing at all, a folder the tree itself placed may be renamed but never
/// moved. The answer becomes what Finder offers, so an operation the portal would refuse is one the
/// user is never given rather than one that fails after they have committed to it.
struct Permissions: Codable, Sendable, Equatable {
    /// Whether things can be put *into* this folder — a new file, or a new folder.
    let writable: Bool
    let renamable: Bool
    let movable: Bool
    let deletable: Bool

    /// What a portal that predates these flags leaves the volume to assume.
    ///
    /// Everything the folder's own `writable` allows, which is what this app offered before it could
    /// ask. Wrong only in the direction of offering something the server then refuses — with its own
    /// sentence, which Finder shows — rather than hiding something that would have worked.
    init(assumedFrom writable: Bool) {
        self.writable = writable
        self.renamable = writable
        self.movable = writable
        self.deletable = writable
    }

    /// Part of an item's metadata version, so a folder that becomes read-only under somebody is
    /// re-reported rather than left offering writes it no longer has.
    var signature: String { "\(writable ? "w" : "-")\(renamable ? "r" : "-")\(movable ? "m" : "-")\(deletable ? "d" : "-")" }
}

/// One row of the tree, as `/api/files` describes it. `version` is the content hash: it changes when
/// and only when the bytes do, which is what the file provider needs and what a date would not give.
///
/// `id` is opaque, and has to stay that way. It is a serial for a row and a `v<parent>_<type>`
/// reference for a folder the portal has declared but not yet written, and nothing here may assume
/// which — reading it as a number loses half of what the tree can name.
struct RemoteFile: Codable, Sendable, Equatable {
    let id: String
    let filename: String
    let mime: String?
    let size: Int64?
    let version: String

    /// The row's two instants, as `Timestamp` describes them. Both optional: a portal that predates
    /// them sends neither, and an item simply has no dates to show.
    let uploadDate: String?
    let modifiedDate: String?

    /// Which folder holds it, or null for a row directly under the tree's root — which is the mount
    /// point, and is addressed as a container rather than by the root row's own id.
    let parent: String?

    /// Optional so that a listing from a portal that predates the trash still decodes; both read a
    /// missing value as false, which is what it meant.
    let isDir: Bool?
    let trashed: Bool?

    /// Whether something *above* this row was thrown away, which `/items/:id` answers and a listing
    /// does not need to: a listing learns it once, from the folder it asked for.
    ///
    /// Not the same as being trashed, and deliberately not folded into it. A covered row still hangs
    /// under the folder it always did — putting it in the bin as well would show it twice — but the
    /// writes it may offer are the same none, since the portal refuses to rename or refile anything
    /// under a trashed folder and answers a restore of one by doing nothing at all.
    let covered: Bool?

    /// Absent from a portal that predates the flags, which is what `Permissions(assumedFrom:)` is for.
    let permissions: Permissions?

    var isFolder: Bool { isDir == true }
    var isTrashed: Bool { trashed == true }
    var isCovered: Bool { covered == true }
}

/// One subfolder. `id` addresses it; `name` is separate because a folder is *labelled* rather than
/// named — renaming a client in the portal renames their folder, and nothing anybody holds stops
/// resolving.
struct RemoteFolder: Codable, Sendable, Equatable {
    let name: String
    let id: String
    let writable: Bool
    let permissions: Permissions?

    /// As on `RemoteFile`, and optional for the same reason — with one more: a folder the portal has
    /// declared but not yet written has no row, so it has no instants to send and never will.
    let uploadDate: String?
    let modifiedDate: String?
}

struct Listing: Codable, Sendable {
    let folders: [RemoteFolder]
    let files: [RemoteFile]

    /// Whether things can be put into the folder that was listed.
    let writable: Bool

    /// Whether that folder is in the bin — itself thrown away, or under something that was.
    ///
    /// Not inferable from `writable`, which is false wherever a folder is merely read-only.
    /// Optional so that a listing from a portal that predates a browsable bin still decodes, and
    /// read as false, which is what it meant.
    let trashed: Bool?

    var isTrashed: Bool { trashed == true }

    /// What to credit a *file* in this folder with when the portal did not say.
    ///
    /// A file is never one of the folders the tree placed, so it may be moved and thrown away
    /// wherever its folder can be written to at all.
    var assumedForFiles: Permissions { Permissions(assumedFrom: writable) }
}

struct Admin: Codable, Sendable {
    let id: Int
    let name: String?
    let email: String?
    let role: String?
}

// MARK: - Errors

enum APIError: LocalizedError {
    case http(status: Int, message: String)
    case malformedResponse
    case uploadRejected(status: Int)

    var errorDescription: String? {
        switch self {
        case .http(let status, let message): return message.isEmpty ? "The server returned HTTP \(status)." : message
        case .malformedResponse: return "The server sent a response this app could not read."
        case .uploadRejected(let status): return "Cloud Storage refused the upload (HTTP \(status))."
        }
    }

    /// The distinction the file provider acts on: gone means remove the item, everything else means
    /// report a transient failure and let the system retry.
    var isNotFound: Bool {
        if case .http(let status, _) = self { return status == 404 }
        return false
    }
}

// MARK: - Client

/// Everything the app and the extension ask of the portal.
///
/// Not an actor: each call is independent and `URLSession` is already safe to use concurrently, so
/// serialising them would only stop the extension fetching two documents at once. What does need
/// serialising — minting an access token — is `TokenProvider`'s job.
struct HelmsleyAPI: Sendable {

    static let shared = HelmsleyAPI()

    private let base = Configuration.baseURL.appendingPathComponent("api/files")

    // MARK: Reads

    /// One directory, named by the id of the folder or — for the top of the tree, which has no id
    /// the volume can use — by the empty path.
    func list(_ destination: Destination) async throws -> Listing {
        var components = URLComponents(url: base.appendingPathComponent("list"), resolvingAgainstBaseURL: false)!
        components.queryItems = destination.query
        return try await get(components.url!)
    }

    /// One row by id, which is the only lookup that answers for a directory as well as a file and
    /// the only one that says where the item sits and whether it is in the bin.
    func item(id: String) async throws -> RemoteFile {
        struct Wrapper: Decodable { let item: RemoteFile }
        let wrapper: Wrapper = try await get(itemURL(id))
        return wrapper.item
    }

    /// What the bin holds — the top of each thing thrown away, not everything marked: a directory
    /// takes its contents with it, and the trash shows the directory rather than each file inside.
    func trashed() async throws -> [RemoteFile] {
        struct Wrapper: Decodable { let items: [RemoteFile] }
        let wrapper: Wrapper = try await get(base.appendingPathComponent("trash"))
        return wrapper.items
    }

    func whoami() async throws -> Admin {
        struct Wrapper: Decodable { let admin: Admin }
        let wrapper: Wrapper = try await get(base.appendingPathComponent("whoami"))
        return wrapper.admin
    }

    /// Downloads a file's bytes to a temporary file, which the caller owns and must move or delete.
    /// Streamed to disk rather than held in memory: files run to hundreds of megabytes, and the
    /// portal never sees them at all — the request is answered with a redirect into Cloud Storage,
    /// so the transfer is between this device and the bucket.
    ///
    /// `reporting` is the progress the file provider handed back to the system. The transfer's own
    /// progress is attached to it as a child, which both drives the percentage the user watches and
    /// makes cancelling it cancel the actual transfer.
    func downloadContents(id: String, reporting parent: Progress? = nil) async throws -> URL {
        var request = URLRequest(url: documentURL(id).appendingPathComponent("content"))
        request.setValue("Bearer \(try await TokenProvider.shared.accessToken())", forHTTPHeaderField: "Authorization")

        let (url, response) = try await Transport.download(request, reporting: parent)
        guard let http = response as? HTTPURLResponse else {
            try? FileManager.default.removeItem(at: url)
            throw APIError.malformedResponse
        }
        guard (200..<300).contains(http.statusCode) else {
            try? FileManager.default.removeItem(at: url)
            throw APIError.http(status: http.statusCode, message: "Could not download file \(id).")
        }
        return url
    }

    // MARK: Writes

    /// Files a new file into `destination`, in the three steps the portal's upload has always taken:
    /// a ticket, a PUT straight to Cloud Storage, then a finalise that writes the row.
    ///
    /// The bytes never pass through the portal — App Engine caps a request at 32MB — so the middle
    /// step talks to a completely different host, using a URL signed for exactly this one object.
    func upload(to destination: Destination, filename: String, mime: String?, fileURL: URL, reporting parent: Progress? = nil) async throws -> RemoteFile {
        // NSNull, not a missing key: the server reads null as "this drop declares no content type"
        // and falls back to octet-stream, which is a different thing from the key being absent.
        let ticket: Ticket = try await post(
            base.appendingPathComponent("upload-ticket"),
            body: destination.body(with: ["contentType": mime ?? NSNull()])
        )
        try await send(ticket, fromFile: fileURL, reporting: parent)

        struct Wrapper: Decodable { let file: RemoteFile }
        let wrapper: Wrapper = try await post(
            base.appendingPathComponent("finalise"),
            body: destination.body(with: ["uploadId": ticket.uploadId, "filename": filename])
        )
        return wrapper.file
    }

    /// Replaces one file's bytes under the same row — a save in the volume rather than a drop into
    /// a folder.
    ///
    /// The same three steps, differing at each end: the ticket names the file being replaced instead
    /// of the folder something is landing in, so an unwritable one is refused before a byte moves,
    /// and the last step updates the row that is already there instead of writing a new one. The row
    /// keeps its id, so nothing the system is holding stops resolving; what changes is its content
    /// hash, which is its version, and its `updated_at`, which is its Date Modified.
    func replaceContents(id: String, mime: String?, fileURL: URL, reporting parent: Progress? = nil) async throws -> RemoteFile {
        let ticket: Ticket = try await post(
            base.appendingPathComponent("upload-ticket"),
            body: ["replaces": id, "contentType": mime ?? NSNull()]
        )
        try await send(ticket, fromFile: fileURL, reporting: parent)

        struct Wrapper: Decodable { let file: RemoteFile }
        let wrapper: Wrapper = try await post(
            documentURL(id).appendingPathComponent("content"),
            body: ["uploadId": ticket.uploadId]
        )
        return wrapper.file
    }

    /// What the portal answers a request for somewhere to put bytes with.
    private struct Ticket: Decodable {
        let uploadId: String
        let uploadUrl: String
        let contentType: String
        let maxBytes: Int64
    }

    /// The middle step of both: the bytes, straight to Cloud Storage.
    private func send(_ ticket: Ticket, fromFile fileURL: URL, reporting parent: Progress?) async throws {
        var put = URLRequest(url: URL(string: ticket.uploadUrl)!)
        put.httpMethod = "PUT"
        // Both headers were signed into the URL, so they must be sent back exactly: Cloud Storage
        // recomputes the signature over them and refuses the PUT if either differs.
        put.setValue(ticket.contentType, forHTTPHeaderField: "Content-Type")
        put.setValue("0,\(ticket.maxBytes)", forHTTPHeaderField: "x-goog-content-length-range")

        // No Authorization header of ours goes to the bucket: the signature in the URL is the whole
        // credential, and the portal's bearer token has no business on another host.
        let (_, response) = try await Transport.upload(put, fromFile: fileURL, reporting: parent)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else { throw APIError.uploadRejected(status: status) }
    }

    func delete(id: String) async throws {
        var request = URLRequest(url: documentURL(id))
        request.httpMethod = "DELETE"
        _ = try await send(request) as Empty
    }

    // MARK: Structure

    // Making a directory, renaming and moving are the whole tree's now, not one branch's — every
    // folder in it is a row, and where a row may be changed is the server's answer rather than a
    // shape this app can read off a path. What it answers arrives as `Permissions`, and the
    // extension offers exactly what it says.

    /// Makes a directory in `destination`.
    ///
    /// A name already in use is numbered rather than refused — which is what a filesystem does — so
    /// the folder to show is the one that comes back, not the one that was asked for.
    func createFolder(in destination: Destination, name: String) async throws -> RemoteFolder {
        struct Wrapper: Decodable { let folder: RemoteFolder }
        let wrapper: Wrapper = try await post(
            base.appendingPathComponent("folders"),
            body: destination.body(with: ["name": name])
        )
        return wrapper.folder
    }

    /// Renames one item, and answers the name it now has.
    ///
    /// Unlike a move, a taken name is refused rather than numbered: a rename is someone typing a
    /// name they mean, and quietly making it "Report 2" would hide that "Report" is already there.
    @discardableResult
    func rename(id: String, to name: String) async throws -> String {
        struct Wrapper: Decodable { let name: String }
        let wrapper: Wrapper = try await post(itemURL(id, "rename"), body: ["name": name])
        return wrapper.name
    }

    /// Moves one item into `destination`, and answers the name it landed under — which is not always
    /// the name it left with, since a collision in the target is numbered rather than refused.
    @discardableResult
    func move(id: String, to destination: Destination) async throws -> String {
        struct Wrapper: Decodable { let name: String }
        let wrapper: Wrapper = try await post(itemURL(id, "move"), body: destination.body())
        return wrapper.name
    }

    /// Into the bin. The bytes stay and the row keeps the folder it was in, which is what makes
    /// putting it back a matter of undoing this rather than of remembering where it came from.
    func trash(id: String) async throws {
        _ = try await post(itemURL(id, "trash"), body: [:]) as Empty
    }

    /// Out again — into `destination` where one is named, which is what a drag out of the bin
    /// supplies, and back where it was otherwise. The name may come back numbered: while the row sat
    /// in the bin its name was not in use, so something else may have taken it in the meantime.
    @discardableResult
    func restore(id: String, to destination: Destination?) async throws -> String {
        struct Wrapper: Decodable { let name: String }
        let wrapper: Wrapper = try await post(itemURL(id, "restore"), body: destination?.body() ?? [:])
        return wrapper.name
    }

    // MARK: Push

    /// Says where this device can be reached when the tree moves, and answers whether the portal is
    /// in a position to reach it — which is not the same question. A portal with no APNs key
    /// configured takes the token and says so, and the extension goes on asking on its short timer
    /// rather than waiting for a push that will never come.
    ///
    /// The environment and the platform ride along because the server cannot work either out: which
    /// APNs host a token belongs to follows how the app was signed, and nothing in the token says.
    @discardableResult
    func registerPushToken(_ token: String, domain: String) async throws -> Bool {
        struct Wrapper: Decodable { let push: Bool? }
        let wrapper: Wrapper = try await post(base.appendingPathComponent("push-token"), body: [
            "token": token,
            "domain": domain,
            "environment": Configuration.pushEnvironment,
            "platform": Configuration.platformName,
            "bundleId": Bundle.main.bundleIdentifier ?? "",
        ])
        return wrapper.push ?? false
    }

    /// Withdraws it. Sent when the system invalidates the token, and when the volume is unmounted by
    /// signing out — the only two moments anything here knows that pushing this device has stopped
    /// meaning anything. APNs itself only reports a token dead once the app is uninstalled.
    func forgetPushToken(_ token: String) async throws {
        var request = URLRequest(url: base.appendingPathComponent("push-token"))
        request.httpMethod = "DELETE"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["token": token])
        _ = try await send(request) as Empty
    }

    // MARK: Plumbing

    private struct Empty: Decodable {}

    // Both take the id as a path component rather than interpolating it into one. An identifier is
    // whatever the server chose to make it, so it is escaped rather than trusted to be URL-safe.

    /// The bytes, and the permanent delete. Still spelled `documents` on the server, from when this
    /// volume served the document table — the route is the route, and renaming it would break every
    /// build of this app already installed.
    private func documentURL(_ id: String) -> URL {
        base.appendingPathComponent("documents").appendingPathComponent(id)
    }

    private func itemURL(_ id: String) -> URL {
        base.appendingPathComponent("items").appendingPathComponent(id)
    }

    private func itemURL(_ id: String, _ action: String) -> URL {
        itemURL(id).appendingPathComponent(action)
    }

    private func get<T: Decodable>(_ url: URL) async throws -> T {
        try await send(URLRequest(url: url))
    }

    private func post<T: Decodable>(_ url: URL, body: [String: Any]) async throws -> T {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try await send(request)
    }

    private func send<T: Decodable>(_ request: URLRequest) async throws -> T {
        var authorised = request
        authorised.setValue("Bearer \(try await TokenProvider.shared.accessToken())", forHTTPHeaderField: "Authorization")

        let (data, response) = try await URLSession.shared.data(for: authorised, delegate: Transport.sanitiser)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            let message = Self.serverMessage(data) ?? ""
            Log.api.error("\(authorised.httpMethod ?? "GET", privacy: .public) \(authorised.url?.path ?? "?", privacy: .public) -> \(status, privacy: .public) \(message, privacy: .public)")
            throw APIError.http(status: status, message: message)
        }
        if T.self == Empty.self { return Empty() as! T }
        do {
            return try JSONDecoder().decode(T.self, from: data)
        } catch {
            throw APIError.malformedResponse
        }
    }

    /// The portal answers every refusal with `{ error: "..." }`, and that sentence is written to be
    /// read by a person — so it is what Finder shows rather than a status code.
    private static func serverMessage(_ data: Data) -> String? {
        struct Failure: Decodable { let error: String? }
        return (try? JSONDecoder().decode(Failure.self, from: data))?.error
    }
}

/// Where the bytes actually move.
///
/// Both transfers are between this device and Cloud Storage — the portal only ever hands over a
/// redirect or a signed URL — so neither passes through App Engine, which caps a request at 32MB
/// against a 500MB document limit.
///
/// Task-based rather than the async conveniences, because the framework wants the transfer's
/// `Progress`: it is what the user watches, and cancelling it has to cancel the transfer itself
/// (`NSFileProviderReplicatedExtension`'s contract — the system cancels a fetch that stalls, and
/// expects the extension to stop and answer promptly).
enum Transport {

    static let sanitiser = RedirectSanitiser()

    /// One session for every transfer, with the sanitiser as its delegate. A delegate-backed
    /// session is what makes the redirect callback fire for completion-handler tasks.
    private static let session = URLSession(configuration: .default, delegate: sanitiser, delegateQueue: nil)

    static func download(_ request: URLRequest, reporting parent: Progress?) async throws -> (URL, URLResponse) {
        try await withCheckedThrowingContinuation { continuation in
            let task = session.downloadTask(with: request) { url, response, error in
                guard let url, let response else {
                    return continuation.resume(throwing: error ?? APIError.malformedResponse)
                }
                // The completion-handler form deletes its temporary file the moment this returns,
                // so it has to be moved now rather than by whoever is awaiting.
                let kept = FileManager.default.temporaryDirectory
                    .appendingPathComponent(UUID().uuidString)
                do {
                    try FileManager.default.moveItem(at: url, to: kept)
                    continuation.resume(returning: (kept, response))
                } catch {
                    continuation.resume(throwing: error)
                }
            }
            attach(task, to: parent)
            task.resume()
        }
    }

    static func upload(_ request: URLRequest, fromFile file: URL, reporting parent: Progress?) async throws -> (Data, URLResponse) {
        try await withCheckedThrowingContinuation { continuation in
            let task = session.uploadTask(with: request, fromFile: file) { data, response, error in
                guard let response else {
                    return continuation.resume(throwing: error ?? APIError.malformedResponse)
                }
                continuation.resume(returning: (data ?? Data(), response))
            }
            attach(task, to: parent)
            task.resume()
        }
    }

    /// Makes the caller's progress the parent of the transfer's own.
    ///
    /// `URLSessionTask.progress` already counts bytes and already cancels the task when it is
    /// cancelled, and `NSProgress` propagates cancellation down to its children — so this one line
    /// is both the percentage the user sees and the cancellation path the framework requires.
    private static func attach(_ task: URLSessionTask, to parent: Progress?) {
        guard let parent else { return }
        parent.totalUnitCount = 100
        parent.addChild(task.progress, withPendingUnitCount: 100)
    }
}

/// Strips the `Authorization` header when a request is redirected to another host.
///
/// A document download is a 302 into Cloud Storage carrying a signed URL, and `URLSession` follows
/// redirects by replaying the original headers — which would hand the portal's bearer token to a
/// completely unrelated host. The signature is the only credential the bucket wants anyway.
final class RedirectSanitiser: NSObject, URLSessionTaskDelegate, @unchecked Sendable {

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        // Scheme as well as host, and case-insensitively: a host is not case-sensitive, and an
        // https -> http redirect back to the portal would otherwise put the token on the wire.
        let origin = task.originalRequest?.url
        let sameOrigin = request.url?.host?.lowercased() == origin?.host?.lowercased()
            && request.url?.scheme == origin?.scheme
        guard !sameOrigin else { return completionHandler(request) }

        var stripped = request
        stripped.setValue(nil, forHTTPHeaderField: "Authorization")
        // Logged because it is a security property nobody can otherwise observe: without this line
        // there is no way to tell a stripped redirect from one that quietly carried the token on.
        Log.api.info("redirect to \(request.url?.host ?? "?", privacy: .public) — Authorization stripped")
        completionHandler(stripped)
    }
}
