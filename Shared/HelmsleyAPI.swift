import Foundation

// MARK: - Wire types

/// One document, as `/api/files` describes it. `version` is the content hash: it changes when and
/// only when the bytes do, which is what the file provider needs and what a date would not give.
///
/// `id` is opaque, and has to stay that way. Two trees hang off this one volume — `documents`, and
/// the admin's own files, which are a different table with its own sequence — so the server marks
/// which one an id came from and hands the whole thing back as a string. Reading it as a number
/// silently loses that mark, and every personal item fails to decode at all.
struct RemoteFile: Codable, Sendable, Equatable {
    let id: String
    let filename: String
    let title: String
    let type: String?
    let mime: String?
    let size: Int64?
    let version: String
    let uploadDate: String?

    // The three the admin's own tree answers and a document never does — a document has no parent to
    // report, appearing as it does in every folder whose filter matches it, and it is never a folder
    // and never in a bin. Optional so that a listing from a portal that predates the trash still
    // decodes; `isFolder` and `isTrashed` read a missing value as false, which is what it meant.
    let parent: String?
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

    var isFolder: Bool { isDir == true }
    var isTrashed: Bool { trashed == true }
    var isCovered: Bool { covered == true }
}

/// One subfolder. `segment` is what goes back in a path; `name` is what a person reads. They differ
/// wherever the tree fans out over rows — a client's folder is segmented by id and named by the
/// client — so both are kept, and renaming a client in the portal never invalidates a stored path.
struct RemoteFolder: Codable, Sendable, Equatable {
    let segment: String
    let name: String
    let writable: Bool

    /// Null outside the admin's own tree, whose folders are rows and so have an identity apart from
    /// where they sit. Carried by the server rather than spelled from the segment: how an id marks
    /// which table it came from is not something this app should have to know.
    let id: String?
}

struct Listing: Codable, Sendable {
    let folders: [RemoteFolder]
    let files: [RemoteFile]
    let writable: Bool
    let accept: [String]

    /// Whether the folder listed is in the bin — itself thrown away, or under something that was.
    ///
    /// Not inferable from `writable`, which is false all over the classified tree for folders that
    /// are simply read-only. Optional so that a listing from a portal that predates a browsable bin
    /// still decodes, and read as false, which is what it meant.
    let trashed: Bool?

    var isTrashed: Bool { trashed == true }
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

    private var base: URL { Configuration.baseURL.appendingPathComponent("api/files") }

    // MARK: Reads

    /// One directory, named either by the path the classified tree is addressed by or by the id of a
    /// folder in the admin's own.
    ///
    /// Repeated `path`, in order — the same shape the dashboard's own /browse takes, and the reason
    /// a segment may safely contain a slash or a space.
    func list(_ destination: Destination) async throws -> Listing {
        var components = URLComponents(url: base.appendingPathComponent("list"), resolvingAgainstBaseURL: false)!
        components.queryItems = destination.query
        return try await get(components.url!)
    }

    /// One item of the admin's own tree, which is the only lookup that answers for a directory and
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

    func document(id: String) async throws -> RemoteFile {
        struct Wrapper: Decodable { let file: RemoteFile }
        let wrapper: Wrapper = try await get(documentURL(id))
        return wrapper.file
    }

    func whoami() async throws -> Admin {
        struct Wrapper: Decodable { let admin: Admin }
        let wrapper: Wrapper = try await get(base.appendingPathComponent("whoami"))
        return wrapper.admin
    }

    /// Downloads a document's bytes to a temporary file, which the caller owns and must move or
    /// delete. Streamed to disk rather than held in memory: documents run to hundreds of megabytes,
    /// and the portal never sees them at all — the request is answered with a redirect into Cloud
    /// Storage, so the transfer is between this device and the bucket.
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
            throw APIError.http(status: http.statusCode, message: "Could not download document \(id).")
        }
        return url
    }

    // MARK: Writes

    /// Files a new document into `path`, in the three steps the portal's upload has always taken:
    /// a ticket, a PUT straight to Cloud Storage, then a finalise that writes the row.
    ///
    /// The bytes never pass through the portal — App Engine caps a request at 32MB — so the middle
    /// step talks to a completely different host, using a URL signed for exactly this one object.
    func upload(to destination: Destination, filename: String, mime: String?, fileURL: URL, reporting parent: Progress? = nil) async throws -> RemoteFile {
        struct Ticket: Decodable {
            let uploadId: String
            let uploadUrl: String
            let contentType: String
            let maxBytes: Int64
        }

        // NSNull, not a missing key: the server reads null as "this drop declares no content type"
        // and falls back to octet-stream, which is a different thing from the key being absent.
        let ticket: Ticket = try await post(
            base.appendingPathComponent("upload-ticket"),
            body: destination.body.merging(["contentType": mime ?? NSNull()]) { current, _ in current }
        )

        var put = URLRequest(url: URL(string: ticket.uploadUrl)!)
        put.httpMethod = "PUT"
        // Both headers were signed into the URL, so they must be sent back exactly: Cloud Storage
        // recomputes the signature over them and refuses the PUT if either differs.
        put.setValue(ticket.contentType, forHTTPHeaderField: "Content-Type")
        put.setValue("0,\(ticket.maxBytes)", forHTTPHeaderField: "x-goog-content-length-range")

        // No Authorization header of ours goes to the bucket: the signature in the URL is the whole
        // credential, and the portal's bearer token has no business on another host.
        let (_, putResponse) = try await Transport.upload(put, fromFile: fileURL, reporting: parent)
        let putStatus = (putResponse as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(putStatus) else { throw APIError.uploadRejected(status: putStatus) }

        struct Wrapper: Decodable { let file: RemoteFile }
        let wrapper: Wrapper = try await post(
            base.appendingPathComponent("finalise"),
            body: destination.body.merging(["uploadId": ticket.uploadId, "filename": filename]) { current, _ in current }
        )
        return wrapper.file
    }

    func delete(id: String) async throws {
        var request = URLRequest(url: documentURL(id))
        request.httpMethod = "DELETE"
        _ = try await send(request) as Empty
    }

    // MARK: The admin's own folder

    // Making a directory, renaming and moving exist only for this one branch of the tree. Everywhere
    // else a folder is a filter over `documents` and a file is a row with no path — nothing there to
    // create, nothing to move along. The server refuses the rest by name, and so does the extension,
    // before Finder offers an operation that could never have reached anything.

    /// Makes a directory in the admin's own folder.
    ///
    /// A name already in use is numbered rather than refused — which is what a filesystem does — so
    /// the folder to show is the one that comes back, not the one that was asked for.
    func createFolder(in destination: Destination, name: String) async throws -> RemoteFolder {
        struct Wrapper: Decodable { let folder: RemoteFolder }
        let wrapper: Wrapper = try await post(
            base.appendingPathComponent("folders"),
            body: destination.body.merging(["name": name]) { current, _ in current }
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

    /// Moves one item into the folder at `path`, and answers the name it landed under — which is not
    /// always the name it left with, since a collision in the target is numbered rather than refused.
    @discardableResult
    func move(id: String, to destination: Destination) async throws -> String {
        struct Wrapper: Decodable { let name: String }
        let wrapper: Wrapper = try await post(itemURL(id, "move"), body: destination.body)
        return wrapper.name
    }

    /// Into the bin. The bytes stay and the row keeps the folder it was in, which is what makes
    /// putting it back a matter of undoing this rather than of remembering where it came from.
    func trash(id: String) async throws {
        _ = try await post(itemURL(id, "trash"), body: [:]) as Empty
    }

    /// Out again — into `destination` where one is named, which is what a drag out of the bin
    /// supplies, and back where it was otherwise. The name may come back numbered: while the row sat in the bin its
    /// name was not in use, so something else may have taken it in the meantime.
    @discardableResult
    func restore(id: String, to destination: Destination?) async throws -> String {
        struct Wrapper: Decodable { let name: String }
        let wrapper: Wrapper = try await post(itemURL(id, "restore"), body: destination?.body ?? [:])
        return wrapper.name
    }

    // MARK: Plumbing

    private struct Empty: Decodable {}

    // Both take the id as a path component rather than interpolating it into one. An identifier is
    // whatever the server chose to make it, so it is escaped rather than trusted to be URL-safe.
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
        let sameHost = request.url?.host == task.originalRequest?.url?.host
        guard !sameHost else { return completionHandler(request) }

        var stripped = request
        stripped.setValue(nil, forHTTPHeaderField: "Authorization")
        // Logged because it is a security property nobody can otherwise observe: without this line
        // there is no way to tell a stripped redirect from one that quietly carried the token on.
        Log.api.info("redirect to \(request.url?.host ?? "?", privacy: .public) — Authorization stripped")
        completionHandler(stripped)
    }
}
